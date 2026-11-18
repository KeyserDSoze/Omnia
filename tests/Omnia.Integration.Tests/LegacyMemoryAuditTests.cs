using Npgsql;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// A global count is a privileged maintenance signal, never a web API
/// response. The auditor sees no row identifiers or personal contents.
/// </summary>
public sealed class LegacyMemoryAuditTests
{
    [Fact]
    public async Task DedicatedAuditorSeesAllTenantsButCannotReadRowsOrWriteAndWebRolesCannotAudit()
    {
        var adminCs = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var apiCs = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        var backfillCs = Environment.GetEnvironmentVariable("OMNIA_TEST_BACKFILL_DB");
        var auditCs = Environment.GetEnvironmentVariable("OMNIA_TEST_AUDIT_DB");
        Assert.False(string.IsNullOrWhiteSpace(adminCs));
        Assert.False(string.IsNullOrWhiteSpace(apiCs));
        Assert.False(string.IsNullOrWhiteSpace(backfillCs));
        Assert.False(string.IsNullOrWhiteSpace(auditCs));

        await using var admin = NpgsqlDataSource.Create(adminCs!);
        await new PostgresMigrator(admin, "omnia_app", "omnia_retention",
            "omnia_backfill", "omnia_backfill_audit").MigrateAsync();
        await using var api = NpgsqlDataSource.Create(apiCs!);
        await using var workerDb = NpgsqlDataSource.Create(backfillCs!);
        await using var auditDb = NpgsqlDataSource.Create(auditCs!);
        var audit = new PostgresLegacyBackfillAudit(auditDb);
        var baseline = await audit.CountRemainingAsync(default);
        var tenantA = "audit-" + Guid.NewGuid().ToString("N");
        var tenantB = "audit-" + Guid.NewGuid().ToString("N");
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        try
        {
            // Admin-only historical fixture: no plaintext capture is allowed by API.
            await using (var conn = await admin.OpenConnectionAsync())
            await using (var tx = await conn.BeginTransactionAsync())
            {
                await using (var disable = new NpgsqlCommand(
                    "ALTER TABLE public.memory_entries DISABLE TRIGGER trg_memory_sealed_writes",
                    conn, tx))
                    await disable.ExecuteNonQueryAsync();
                for (var i = 0; i < ids.Length; i++)
                {
                    await using var cmd = new NpgsqlCommand("""
                        INSERT INTO public.memory_entries
                          (id, tenant_id, owner_sub, source, external_id, content_type,
                           raw_content, normalized_json, summary, tags, occurred_at)
                        VALUES (@id, @tenant, @owner, 'manual', @external, 'text/plain',
                           'synthetic-legacy', '{"text":"synthetic-legacy"}'::jsonb,
                           'synthetic-legacy', ARRAY[]::text[], now())
                        """, conn, tx);
                    cmd.Parameters.AddWithValue("id", ids[i]);
                    cmd.Parameters.AddWithValue("tenant", i == 2 ? tenantB : tenantA);
                    cmd.Parameters.AddWithValue("owner", i == 1 ? "bob" : "alice");
                    cmd.Parameters.AddWithValue("external", Guid.NewGuid().ToString("N"));
                    Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
                }
                await using (var enable = new NpgsqlCommand(
                    "ALTER TABLE public.memory_entries ENABLE TRIGGER trg_memory_sealed_writes",
                    conn, tx))
                    await enable.ExecuteNonQueryAsync();
                await tx.CommitAsync();
            }

            Assert.Equal(baseline + 3, await audit.CountRemainingAsync(default));

            // Auditor is NOT allowed SELECT against the underlying table,
            // even if it tries to forge an RLS actor context.
            await using (var direct = auditDb.CreateCommand(
                "SELECT count(*) FROM public.memory_entries"))
                Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(
                    () => direct.ExecuteScalarAsync())).SqlState);
            await using (var mutation = auditDb.CreateCommand(
                "DELETE FROM public.memory_entries WHERE false"))
                Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(
                    () => mutation.ExecuteNonQueryAsync())).SqlState);
            await using (var apiAttempt = api.CreateCommand(
                "SELECT public.omnia_count_legacy_memories()"))
                Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(
                    () => apiAttempt.ExecuteScalarAsync())).SqlState);
            await using (var workerAttempt = workerDb.CreateCommand(
                "SELECT public.omnia_count_legacy_memories()"))
                Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(
                    () => workerAttempt.ExecuteScalarAsync())).SqlState);

            // Actor-scoped sealing decreases the GLOBAL count by exactly one.
            var worker = new PostgresLegacyMemoryBackfill(workerDb, TestEnvelopeCrypto.Create());
            Assert.Equal(1, await worker.BackfillBatchAsync(tenantB, "alice", 1, default));
            Assert.Equal(baseline + 2, await audit.CountRemainingAsync(default));

            // Using the ordinary API identity as audit must fail closed too.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new PostgresLegacyBackfillAudit(api).CountRemainingAsync(default));
        }
        finally
        {
            await using var cleanup = admin.CreateCommand("""
                DELETE FROM public.memory_entries WHERE id = ANY(@ids)
                """);
            cleanup.Parameters.AddWithValue("ids", ids);
            await cleanup.ExecuteNonQueryAsync();
        }

        Assert.Equal(baseline, await audit.CountRemainingAsync(default));
    }
}
