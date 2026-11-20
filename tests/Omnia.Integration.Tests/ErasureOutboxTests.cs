using Npgsql;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Real PostgreSQL + non-owner RLS checks for an erasure transactional
/// outbox. Synthetic actors only. Outbox rows are NOT yet durable offsite.
/// </summary>
public sealed class ErasureOutboxTests
{
    [Fact]
    public async Task ErasureAndOutboxAreAtomicAndCannotExposeOtherActors()
    {
        var adminString = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var appString = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        Assert.False(string.IsNullOrWhiteSpace(adminString));
        Assert.False(string.IsNullOrWhiteSpace(appString));
        await using var admin = NpgsqlDataSource.Create(adminString!);
        await new PostgresMigrator(admin, "omnia_app").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(appString!);
        await PostgresRuntimeGuard.VerifyAsync(app);

        var tenant = "outbox-" + Guid.NewGuid().ToString("N");
        var unrelated = tenant + "-other";
        var owner = "synthetic-subject";
        var consent = new PostgresConsentRepository(app);
        var memory = new PostgresMemoryRepository(app, TestEnvelopeCrypto.Create());
        var erasure = new PostgresPersonalDataService(app, TestEnvelopeCrypto.Create());
        var now = DateTimeOffset.UtcNow;

        async Task CreateAsync(string t)
        {
            await consent.GrantAsync(ConsentGrant.Issue(t, owner, "manual",
                ConsentGrant.MemoryCapture, "privacy-v1", now,
                now.AddDays(2)), default);
            await memory.UpsertAsync(MemoryEntry.Capture(t, owner, "manual",
                Guid.NewGuid().ToString("N"), "text/plain", "synthetic",
                "{\"test\":true}", "synthetic summary", [], now), default);
        }
        await CreateAsync(tenant);
        await CreateAsync(unrelated);

        var receipt = await erasure.EraseAsync(tenant, owner, default);
        Assert.Equal(1, receipt.DeletedMemories);
        Assert.Equal(1, receipt.DeletedConsents);
        Assert.Empty(await memory.RecentAsync(tenant, owner, 20, default));
        Assert.Single(await memory.RecentAsync(unrelated, owner, 20, default));

        await using (var cmd = admin.CreateCommand("""
            SELECT id, event_type, created_at
            FROM public.erasure_outbox
            WHERE tenant_id = @tenant AND owner_sub = @owner
            """))
        {
            cmd.Parameters.AddWithValue("tenant", tenant);
            cmd.Parameters.AddWithValue("owner", owner);
            await using var rows = await cmd.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.NotEqual(Guid.Empty, rows.GetGuid(0));
            Assert.Equal("erase-actor", rows.GetString(1));
            Assert.InRange(rows.GetFieldValue<DateTimeOffset>(2),
                now.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.False(await rows.ReadAsync());
        }

        // An explicit repeated request is another event, even if no primary
        // rows remain; event dedup by stable request ID is a separate slice.
        var repeated = await erasure.EraseAsync(tenant, owner, default);
        Assert.Equal(0, repeated.DeletedMemories);
        Assert.Equal(0, repeated.DeletedConsents);
        Assert.Equal(2L, await CountOutboxAsync(admin, tenant));

        // The runtime identity cannot enumerate, update, delete or truncate
        // the durable outbox even while scoped to a valid JWT actor.
        await using (var unscoped = app.CreateCommand(
            "SELECT count(*) FROM public.erasure_outbox"))
            await Assert.ThrowsAsync<PostgresException>(() =>
                unscoped.ExecuteScalarAsync());

        await using (var scope = await PostgresActorScope.OpenAsync(
            app, tenant, owner, default))
        {
            await using var select = new NpgsqlCommand(
                "SELECT count(*) FROM public.erasure_outbox",
                scope.Connection, scope.Transaction);
            await Assert.ThrowsAsync<PostgresException>(() =>
                select.ExecuteScalarAsync());
        }

        await using (var scope = await PostgresActorScope.OpenAsync(
            app, tenant, owner, default))
        {
            await using var crossActor = new NpgsqlCommand("""
                INSERT INTO public.erasure_outbox
                    (id, tenant_id, owner_sub, event_type)
                VALUES (@id, @tenant, @owner, 'erase-actor')
                """, scope.Connection, scope.Transaction);
            crossActor.Parameters.AddWithValue("id", Guid.NewGuid());
            crossActor.Parameters.AddWithValue("tenant", unrelated);
            crossActor.Parameters.AddWithValue("owner", owner);
            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                crossActor.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState); // RLS WITH CHECK denied
        }
        Assert.Equal(0L, await CountOutboxAsync(admin, unrelated));
    }

    [Fact]
    public async Task FailureWritingOutboxRollsBackConsentAndMemoryDeletion()
    {
        var adminString = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var appString = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        Assert.False(string.IsNullOrWhiteSpace(adminString));
        Assert.False(string.IsNullOrWhiteSpace(appString));
        await using var admin = NpgsqlDataSource.Create(adminString!);
        await new PostgresMigrator(admin, "omnia_app").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(appString!);
        var tenant = "outbox-rollback-" + Guid.NewGuid().ToString("N");
        const string subject = "synthetic-rollback";
        var now = DateTimeOffset.UtcNow;
        var grants = new PostgresConsentRepository(app);
        var notes = new PostgresMemoryRepository(app, TestEnvelopeCrypto.Create());
        await grants.GrantAsync(ConsentGrant.Issue(tenant, subject, "manual",
            ConsentGrant.MemoryCapture, "privacy-v1", now, now.AddDays(2)),
            default);
        await notes.UpsertAsync(MemoryEntry.Capture(tenant, subject,
            "manual", Guid.NewGuid().ToString("N"), "text/plain",
            "test-only", "{\"test\":true}", "test-only", [], now), default);

        // Test-only constraint applies only to this unique synthetic actor.
        // If the outbox write fails, the same transaction MUST roll back
        // both earlier DELETE statements. Integration tests run serially.
        const string constraint = "ck_omnia_test_outbox_fail";
        try
        {
            await using (var add = admin.CreateCommand($"""
                ALTER TABLE public.erasure_outbox
                ADD CONSTRAINT {constraint}
                CHECK (tenant_id <> '${tenant}') NOT VALID
                """))
            {
                // tenant is a locally generated prefixed UUID, never input.
                await add.ExecuteNonQueryAsync();
            }
            var service = new PostgresPersonalDataService(
                app, TestEnvelopeCrypto.Create());
            await Assert.ThrowsAsync<PostgresException>(() =>
                service.EraseAsync(tenant, subject, default));
            Assert.Single(await notes.RecentAsync(tenant, subject, 20, default));
            Assert.Single(await grants.ListAsync(tenant, subject, default));
            Assert.Equal(0L, await CountOutboxAsync(admin, tenant));
        }
        finally
        {
            await using var drop = admin.CreateCommand($"""
                ALTER TABLE public.erasure_outbox
                DROP CONSTRAINT IF EXISTS {constraint}
                """);
            await drop.ExecuteNonQueryAsync();
        }

        var success = await new PostgresPersonalDataService(
            app, TestEnvelopeCrypto.Create()).EraseAsync(
                tenant, subject, default);
        Assert.Equal(1, success.DeletedMemories);
        Assert.Equal(1, success.DeletedConsents);
        Assert.Equal(1L, await CountOutboxAsync(admin, tenant));
    }

    [Fact]
    public async Task OutboxIsForceRlsAppendOnlyAndSeparatedFromAuditAndRetention()
    {
        var adminString = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var appString = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        var retentionString = Environment.GetEnvironmentVariable("OMNIA_TEST_RETENTION_DB");
        Assert.False(string.IsNullOrWhiteSpace(adminString));
        Assert.False(string.IsNullOrWhiteSpace(appString));
        Assert.False(string.IsNullOrWhiteSpace(retentionString));
        await using var admin = NpgsqlDataSource.Create(adminString!);
        await new PostgresMigrator(admin, "omnia_app", "omnia_retention",
            "omnia_backfill", "omnia_backfill_audit").MigrateAsync();

        await using var verify = admin.CreateCommand("""
            SELECT relrowsecurity, relforcerowsecurity,
              has_table_privilege('omnia_app', 'public.erasure_outbox', 'INSERT'),
              has_table_privilege('omnia_app', 'public.erasure_outbox',
                'SELECT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER'),
              has_table_privilege('omnia_retention', 'public.erasure_outbox', 'SELECT, INSERT, UPDATE, DELETE'),
              has_table_privilege('omnia_backfill', 'public.erasure_outbox', 'SELECT, INSERT, UPDATE, DELETE'),
              has_table_privilege('omnia_backfill_audit', 'public.erasure_outbox', 'SELECT, INSERT, UPDATE, DELETE')
            FROM pg_class WHERE oid = 'public.erasure_outbox'::regclass
            """);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.False(reader.GetBoolean(6));
    }

    private static async Task<long> CountOutboxAsync(
        NpgsqlDataSource admin, string tenant)
    {
        await using var cmd = admin.CreateCommand("""
            SELECT count(*) FROM public.erasure_outbox WHERE tenant_id = @tenant
            """);
        cmd.Parameters.AddWithValue("tenant", tenant);
        return (long)(await cmd.ExecuteScalarAsync() ?? -1L);
    }
}
