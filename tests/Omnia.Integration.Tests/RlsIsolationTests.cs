using Npgsql;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// SQL checks deliberately omit the application's tenant WHERE clauses:
/// only PostgreSQL RLS is allowed to enforce the actor boundary.
/// </summary>
public sealed class RlsIsolationTests
{
    private static async Task<(NpgsqlDataSource Admin, NpgsqlDataSource App)> OpenAsync()
    {
        var ownerCs = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var runtimeCs = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        Assert.False(string.IsNullOrWhiteSpace(ownerCs), "Disposable schema-owner PostgreSQL is required.");
        Assert.False(string.IsNullOrWhiteSpace(runtimeCs), "Non-superuser app role is required.");
        var admin = NpgsqlDataSource.Create(ownerCs!);
        try
        {
            await new PostgresMigrator(admin, "omnia_app").MigrateAsync();
            // Force one physical connection to be reused: this exposes leaked
            // custom GUC settings across pooled calls if SET LOCAL is missing.
            var runtime = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(runtimeCs!)
            {
                MaxPoolSize = 1
            }.ConnectionString);
            await PostgresRuntimeGuard.VerifyAsync(runtime);
            return (admin, runtime);
        }
        catch
        {
            await admin.DisposeAsync();
            throw;
        }
    }

    [Fact]
    public async Task RuntimeRoleAndForcedPoliciesAreMandatory()
    {
        var (admin, app) = await OpenAsync();
        await using (admin)
        await using (app)
        {
            await PostgresRuntimeGuard.VerifyAsync(app);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => PostgresRuntimeGuard.VerifyAsync(admin));
            await using var role = app.CreateCommand("""
                SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user
                """);
            await using var reader = await role.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0));
            Assert.False(reader.GetBoolean(1));
        }
    }

    [Fact]
    public async Task UnscopedSqlAndOtherTenantsSeeNoMemoryOrConsent()
    {
        var (admin, app) = await OpenAsync();
        await using (admin)
        await using (app)
        {
            var tenant = "rls-" + Guid.NewGuid().ToString("N");
            var external = Guid.NewGuid().ToString("N");
            var source = "manual";
            var now = DateTimeOffset.UtcNow;
            var consents = new PostgresConsentRepository(app);
            await consents.GrantAsync(ConsentGrant.Issue(
                tenant, "alice", source, ConsentGrant.MemoryCapture,
                "privacy-v1", now, now.AddDays(1)), CancellationToken.None);
            await consents.GrantAsync(ConsentGrant.Issue(
                tenant, "bob", source, ConsentGrant.MemoryCapture,
                "privacy-v1", now, now.AddDays(1)), CancellationToken.None);
            var memory = new PostgresMemoryRepository(app, TestEnvelopeCrypto.Create());
            var saved = await memory.UpsertAsync(MemoryEntry.Capture(
                tenant, "alice", source, external, "text/plain",
                "Alice private synthetic note", "{\"text\":\"Alice\"}",
                "Alice private synthetic note", [], now), CancellationToken.None);
            Assert.NotEqual(Guid.Empty, saved.Id);

            async Task<long> RawCountAsync(string table)
            {
                Assert.Contains(table, new[] { "memory_entries", "consent_grants" });
                await using var command = app.CreateCommand(
                    "SELECT count(*) FROM public." + table); // no WHERE on purpose
                return (long)(await command.ExecuteScalarAsync()
                    ?? throw new InvalidOperationException());
            }
            Assert.Equal(0, await RawCountAsync("memory_entries"));
            Assert.Equal(0, await RawCountAsync("consent_grants"));

            await using (var alice = await PostgresActorScope.OpenAsync(
                app, tenant, "alice", CancellationToken.None))
            {
                await using var unfiltered = new NpgsqlCommand(
                    "SELECT id FROM memory_entries", alice.Connection, alice.Transaction);
                Assert.Equal(saved.Id, (Guid)(await unfiltered.ExecuteScalarAsync()
                    ?? throw new InvalidOperationException()));
            }

            // A transaction-local context must disappear even when the same pooled
            // physical connection is reused by the next request.
            Assert.Equal(0, await RawCountAsync("memory_entries"));
            Assert.Equal(0, await RawCountAsync("consent_grants"));

            await using (var bob = await PostgresActorScope.OpenAsync(
                app, tenant, "bob", CancellationToken.None))
            {
                await using var all = new NpgsqlCommand(
                    "SELECT count(*) FROM memory_entries", bob.Connection, bob.Transaction);
                Assert.Equal(0L, (long)(await all.ExecuteScalarAsync() ?? -1L));
                await using var update = new NpgsqlCommand(
                    "UPDATE memory_entries SET summary='illicit' WHERE id=@id",
                    bob.Connection, bob.Transaction);
                update.Parameters.AddWithValue("id", saved.Id);
                Assert.Equal(0, await update.ExecuteNonQueryAsync());
                await using var consentCount = new NpgsqlCommand(
                    "SELECT count(*) FROM consent_grants", bob.Connection, bob.Transaction);
                Assert.Equal(1L, (long)(await consentCount.ExecuteScalarAsync() ?? -1L));
            }

            await using (var otherTenant = await PostgresActorScope.OpenAsync(
                app, tenant + "-other", "alice", CancellationToken.None))
            {
                await using var all = new NpgsqlCommand(
                    "SELECT count(*) FROM memory_entries", otherTenant.Connection,
                    otherTenant.Transaction);
                Assert.Equal(0L, (long)(await all.ExecuteScalarAsync() ?? -1L));
            }

            // Even an unfiltered SQL query still returns only the signed-in actor's rows.
            await using (var alice = await PostgresActorScope.OpenAsync(
                app, tenant, "alice", CancellationToken.None))
            {
                await using var count = new NpgsqlCommand(
                    "SELECT count(*) FROM memory_entries", alice.Connection, alice.Transaction);
                Assert.Equal(1L, (long)(await count.ExecuteScalarAsync() ?? -1L));
            }
        }
    }

    [Fact]
    public async Task RlsRejectsForgedOwnerInsertAndCrossOwnerMutation()
    {
        var (admin, app) = await OpenAsync();
        await using (admin)
        await using (app)
        {
            var tenant = "rls-" + Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            var consents = new PostgresConsentRepository(app);
            var bobGrant = await consents.GrantAsync(ConsentGrant.Issue(
                tenant, "bob", "manual", ConsentGrant.MemoryCapture,
                "privacy-v1", now, now.AddDays(1)), CancellationToken.None);

            await using (var bob = await PostgresActorScope.OpenAsync(
                app, tenant, "bob", CancellationToken.None))
            {
                await using var forged = new NpgsqlCommand("""
                    INSERT INTO consent_grants
                      (id, tenant_id, owner_sub, source, purpose, policy_version,
                       granted_at, expires_at)
                    VALUES (@id, @tenant, 'alice', 'crm', 'memory.capture',
                            'privacy-v1', @now, @expires)
                    """, bob.Connection, bob.Transaction);
                forged.Parameters.AddWithValue("id", Guid.NewGuid());
                forged.Parameters.AddWithValue("tenant", tenant);
                forged.Parameters.AddWithValue("now", now);
                forged.Parameters.AddWithValue("expires", now.AddDays(1));
                var error = await Assert.ThrowsAsync<PostgresException>(
                    () => forged.ExecuteNonQueryAsync());
                Assert.Equal("42501", error.SqlState);
                // The rejected operation aborts this transaction automatically.
            }

            await using (var bob = await PostgresActorScope.OpenAsync(
                app, tenant, "bob", CancellationToken.None))
            {
                await using var relocate = new NpgsqlCommand("""
                    UPDATE consent_grants SET owner_sub='alice' WHERE id=@id
                    """, bob.Connection, bob.Transaction);
                relocate.Parameters.AddWithValue("id", bobGrant.Id);
                var error = await Assert.ThrowsAsync<PostgresException>(
                    () => relocate.ExecuteNonQueryAsync());
                Assert.Equal("42501", error.SqlState);
            }

            await using (var bob = await PostgresActorScope.OpenAsync(
                app, tenant, "bob", CancellationToken.None))
            {
                await using var turnOff = new NpgsqlCommand(
                    "SET LOCAL row_security = off", bob.Connection, bob.Transaction);
                await turnOff.ExecuteNonQueryAsync();
                await using var all = new NpgsqlCommand(
                    "SELECT count(*) FROM consent_grants", bob.Connection, bob.Transaction);
                var error = await Assert.ThrowsAsync<PostgresException>(
                    () => all.ExecuteScalarAsync());
                Assert.Equal("42501", error.SqlState);
            }
            await using var unscoped = app.CreateCommand("SELECT count(*) FROM memory_entries");
            Assert.Equal(0L, (long)(await unscoped.ExecuteScalarAsync() ?? -1L));
        }
    }
}
