using Npgsql;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Real pgvector/PostgreSQL retention checks: expiry is database-enforced,
/// the API role cannot run the privileged purge, and a non-owner worker
/// can delete only expired notes across subjects without reading content.
/// </summary>
public sealed class RetentionTests
{
    [Fact]
    public async Task ServerExpiryUsesReceivedAtAndRetryCannotExtendStorageLifetime()
    {
        var ownerCs = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var appCs = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        Assert.False(string.IsNullOrWhiteSpace(ownerCs));
        Assert.False(string.IsNullOrWhiteSpace(appCs));
        await using var admin = NpgsqlDataSource.Create(ownerCs!);
        await new PostgresMigrator(admin, "omnia_app").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(appCs!);
        await PostgresRuntimeGuard.VerifyAsync(app);

        var tenant = "retention-" + Guid.NewGuid().ToString("N");
        var subject = "alice";
        var now = DateTimeOffset.UtcNow;
        await new PostgresConsentRepository(app).GrantAsync(ConsentGrant.Issue(
            tenant, subject, "manual", ConsentGrant.MemoryCapture,
            "privacy-v1", now, now.AddDays(7)), default);
        var repo = new PostgresMemoryRepository(app, TestEnvelopeCrypto.Create());
        var entry = MemoryEntry.Capture(tenant, subject, "manual",
            Guid.NewGuid().ToString("N"), "text/plain", "synthetic raw",
            "{\"text\":\"synthetic\"}", "synthetic", [], now,
            Enumerable.Repeat(0.125f, 768).ToArray(), "fixture-model");
        var saved = await repo.UpsertAsync(entry, default);

        async Task<(DateTimeOffset Received, DateTimeOffset Expires)> LifetimeAsync()
        {
            await using var actor = await PostgresActorScope.OpenAsync(
                app, tenant, subject, default);
            await using var command = new NpgsqlCommand("""
                SELECT received_at, expires_at FROM memory_entries
                WHERE id = @id
                """, actor.Connection, actor.Transaction);
            command.Parameters.AddWithValue("id", saved.Id);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetFieldValue<DateTimeOffset>(0),
                reader.GetFieldValue<DateTimeOffset>(1));
        }

        var first = await LifetimeAsync();
        Assert.Equal(first.Received.AddDays(30), first.Expires);
        await repo.UpsertAsync(entry, default);
        var again = await LifetimeAsync();
        Assert.Equal(first, again); // idempotent retry does not reset the clock

        // Fixture-only admin change: simulate time passage without sleeping.
        await using (var age = admin.CreateCommand("""
            UPDATE memory_entries SET received_at = now() - interval '31 days',
              expires_at = now() - interval '1 day' WHERE id = @id
            """))
        {
            age.Parameters.AddWithValue("id", saved.Id);
            Assert.Equal(1, await age.ExecuteNonQueryAsync());
        }
        Assert.DoesNotContain(await repo.RecentAsync(tenant, subject, 25, default),
            row => row.Id == saved.Id);
    }

    [Fact]
    public async Task DedicatedWorkerPurgesExpiredAcrossTenantsButCannotReadPersonalData()
    {
        var ownerCs = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var appCs = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        var retentionCs = Environment.GetEnvironmentVariable("OMNIA_TEST_RETENTION_DB");
        Assert.False(string.IsNullOrWhiteSpace(ownerCs));
        Assert.False(string.IsNullOrWhiteSpace(appCs));
        Assert.False(string.IsNullOrWhiteSpace(retentionCs),
            "A separate, non-owner, non-bypass retention login is mandatory.");
        await using var admin = NpgsqlDataSource.Create(ownerCs!);
        await new PostgresMigrator(admin, "omnia_app", "omnia_retention").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(appCs!);
        await using var worker = NpgsqlDataSource.Create(retentionCs!);

        // No direct table visibility, RLS bypass or access to the routine by the API.
        await using (var privilege = worker.CreateCommand("""
            SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user
            """))
        await using (var reader = await privilege.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0));
            Assert.False(reader.GetBoolean(1));
        }
        await using (var direct = worker.CreateCommand("SELECT count(*) FROM memory_entries"))
            await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteScalarAsync());
        await using (var forbidden = app.CreateCommand(
            "SELECT public.omnia_purge_expired_memories(100)"))
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(
                () => forbidden.ExecuteScalarAsync());
            Assert.Equal("42501", denied.SqlState);
        }

        var tenants = new[] { "ret-" + Guid.NewGuid().ToString("N"),
            "ret-" + Guid.NewGuid().ToString("N") };
        var memories = new PostgresMemoryRepository(app, TestEnvelopeCrypto.Create());
        var consents = new PostgresConsentRepository(app);
        var now = DateTimeOffset.UtcNow;
        var items = new List<(string Tenant, string Owner, Guid Id)>();
        foreach (var tenant in tenants)
        foreach (var owner in new[] { "alice", "bob" })
        {
            await consents.GrantAsync(ConsentGrant.Issue(tenant, owner, "manual",
                ConsentGrant.MemoryCapture, "privacy-v1", now, now.AddDays(7)), default);
            var entry = MemoryEntry.Capture(tenant, owner, "manual",
                Guid.NewGuid().ToString("N"), "text/plain", "synthetic",
                "{\"v\":1}", "fixture", [], now,
                Enumerable.Repeat(0.1f, 768).ToArray(), "fixture-model");
            var saved = await memories.UpsertAsync(entry, default);
            items.Add((tenant, owner, saved.Id));
        }
        // Expire three out of four, leaving one live memory to prove isolation.
        await using (var age = admin.CreateCommand("""
            UPDATE memory_entries SET expires_at = now() - interval '1 second'
            WHERE id = ANY(@ids)
            """))
        {
            age.Parameters.AddWithValue("ids", items.Take(3).Select(x => x.Id).ToArray());
            Assert.Equal(3, await age.ExecuteNonQueryAsync());
        }

        async Task<int> PurgeAsync(int batch)
        {
            await using var command = worker.CreateCommand(
                "SELECT public.omnia_purge_expired_memories(@batch)");
            command.Parameters.AddWithValue("batch", batch);
            return (int)(await command.ExecuteScalarAsync() ?? -1);
        }
        Assert.Equal(2, await PurgeAsync(2));
        Assert.Equal(1, await PurgeAsync(2));
        Assert.Equal(0, await PurgeAsync(2));
        await Assert.ThrowsAsync<PostgresException>(() => PurgeAsync(0));
        await Assert.ThrowsAsync<PostgresException>(() => PurgeAsync(501));
        foreach (var item in items.Take(3))
            Assert.DoesNotContain(await memories.RecentAsync(
                item.Tenant, item.Owner, 10, default), x => x.Id == item.Id);
        var remaining = items[3];
        Assert.Contains(await memories.RecentAsync(
            remaining.Tenant, remaining.Owner, 10, default), x => x.Id == remaining.Id);

        await using var total = admin.CreateCommand("""
            SELECT count(*) FROM memory_entries WHERE id = ANY(@ids)
            """);
        total.Parameters.AddWithValue("ids", items.Select(x => x.Id).ToArray());
        Assert.Equal(1L, (long)(await total.ExecuteScalarAsync() ?? -1L));
    }
}
