using System.Text.Json;
using Npgsql;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

public sealed class LegacyBackfillInventoryTests
{
    [Fact]
    public void RequiresExplicitApprovalStrictSchemaAndRejectsDuplicates()
    {
        using var manifest = new TemporaryInventory();
        var actor1 = new { tenantId = "tenant-a", ownerSubject = "alice" };
        var actor2 = new { tenantId = "tenant-a", ownerSubject = "bob" };
        manifest.Write(new
        {
            schemaVersion = 1, operatorApproved = true,
            actors = new[] { actor1, actor2 }
        });

        var actors = LegacyBackfillInventory.Load(manifest.Path);
        Assert.Equal(2, actors.Count);
        Assert.Equal("tenant-a", actors[0].TenantId);
        Assert.Equal("bob", actors[1].OwnerSubject);

        var duplicates = new
        {
            schemaVersion = 1, operatorApproved = true,
            actors = new[] { actor1, actor1 }
        };
        manifest.Write(duplicates);
        Assert.Throws<InvalidDataException>(() => LegacyBackfillInventory.Load(manifest.Path));

        manifest.Write(new { schemaVersion = 1, operatorApproved = false, actors = new[] { actor1 } });
        Assert.Throws<InvalidDataException>(() => LegacyBackfillInventory.Load(manifest.Path));

        manifest.Write(new { schemaVersion = 2, operatorApproved = true, actors = new[] { actor1 } });
        Assert.Throws<InvalidDataException>(() => LegacyBackfillInventory.Load(manifest.Path));

        manifest.Write(new { schemaVersion = 1, operatorApproved = true, actors = new[] { actor1 }, unexpected = "value" });
        Assert.Throws<InvalidDataException>(() => LegacyBackfillInventory.Load(manifest.Path));

        manifest.Write(new { schemaVersion = 1, operatorApproved = true,
            actors = new[] { new { tenantId = "tenant-a ", ownerSubject = "alice" } } });
        Assert.Throws<InvalidDataException>(() => LegacyBackfillInventory.Load(manifest.Path));

        manifest.WriteRaw("""{"schemaVersion":1,"operatorApproved":true,"actors":[{"tenantId":"t","ownerSubject":"s","tenantId":"u"}]}""");
        Assert.Throws<InvalidDataException>(() => LegacyBackfillInventory.Load(manifest.Path));
    }

    [Fact]
    public void RejectsRelativeFilePathsOversizeAndInsecurePermissions()
    {
        Assert.Throws<InvalidDataException>(() => LegacyBackfillInventory.Load("relative-inventory.json"));
        using var manifest = new TemporaryInventory();
        manifest.WriteRaw(new string('x', 300_000));
        Assert.Throws<InvalidDataException>(() => LegacyBackfillInventory.Load(manifest.Path));

        manifest.Write(new
        {
            schemaVersion = 1, operatorApproved = true,
            actors = new[] { new { tenantId = "t", ownerSubject = "s" } }
        });
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(manifest.Path, UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Assert.Throws<InvalidDataException>(() => LegacyBackfillInventory.Load(manifest.Path));
        }
    }

    [Fact]
    public async Task BoundedManifestRunnerIsResumableAndDoesNotAlterOtherActors()
    {
        var ownerCs = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var backfillCs = Environment.GetEnvironmentVariable("OMNIA_TEST_BACKFILL_DB");
        Assert.False(string.IsNullOrWhiteSpace(ownerCs));
        Assert.False(string.IsNullOrWhiteSpace(backfillCs));
        await using var ownerDb = NpgsqlDataSource.Create(ownerCs!);
        await new PostgresMigrator(ownerDb, "omnia_app", "omnia_retention", "omnia_backfill",
            "omnia_backfill_audit").MigrateAsync();
        await using var workerDb = NpgsqlDataSource.Create(backfillCs!);
        await PostgresRuntimeGuard.VerifyAsync(workerDb);
        var tenant = "inventory-" + Guid.NewGuid().ToString("N");
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        try
        {
            await using (var connection = await ownerDb.OpenConnectionAsync())
            await using (var tx = await connection.BeginTransactionAsync())
            {
                await using (var disable = new NpgsqlCommand(
                    "ALTER TABLE public.memory_entries DISABLE TRIGGER trg_memory_sealed_writes",
                    connection, tx)) await disable.ExecuteNonQueryAsync();
                for (var i = 0; i < ids.Length; i++)
                {
                    await using var insert = new NpgsqlCommand("""
                        INSERT INTO public.memory_entries
                          (id, tenant_id, owner_sub, source, external_id, content_type,
                           raw_content, normalized_json, summary, tags, occurred_at)
                        VALUES (@id, @tenant, @subject, 'manual', @external, 'text/plain',
                          'synthetic-legacy', '{"text":"synthetic-legacy"}'::jsonb,
                          'synthetic-legacy', ARRAY[]::text[], now())
                        """, connection, tx);
                    insert.Parameters.AddWithValue("id", ids[i]);
                    insert.Parameters.AddWithValue("tenant", tenant);
                    insert.Parameters.AddWithValue("subject", i == 2 ? "bob" : "alice");
                    insert.Parameters.AddWithValue("external", Guid.NewGuid().ToString("N"));
                    Assert.Equal(1, await insert.ExecuteNonQueryAsync());
                }
                await using (var enable = new NpgsqlCommand(
                    "ALTER TABLE public.memory_entries ENABLE TRIGGER trg_memory_sealed_writes",
                    connection, tx)) await enable.ExecuteNonQueryAsync();
                await tx.CommitAsync();
            }

            using var manifest = new TemporaryInventory();
            manifest.Write(new
            {
                schemaVersion = 1, operatorApproved = true,
                actors = new[] { new { tenantId = tenant, ownerSubject = "alice" } }
            });
            var actors = LegacyBackfillInventory.Load(manifest.Path);
            var worker = new PostgresLegacyMemoryBackfill(workerDb, TestEnvelopeCrypto.Create());

            // First run deliberately exhausts the bounded work budget.
            var partial = await LegacyBackfillRunner.RunAsync(worker, actors,
                batchSize: 1, maxBatchesPerActor: 1, default);
            Assert.Equal(1, partial.SealedRows);
            Assert.Equal(1, partial.IncompleteActors);
            Assert.Equal(1L, await worker.CountLegacyAsync(tenant, "alice", default));
            Assert.Equal(1L, await worker.CountLegacyAsync(tenant, "bob", default));

            var resumed = await LegacyBackfillRunner.RunAsync(worker, actors,
                batchSize: 1, maxBatchesPerActor: 5, default);
            Assert.Equal(1, resumed.SealedRows);
            Assert.Equal(0, resumed.IncompleteActors);
            Assert.Equal(0L, await worker.CountLegacyAsync(tenant, "alice", default));
            Assert.Equal(1L, await worker.CountLegacyAsync(tenant, "bob", default));
        }
        finally
        {
            await using var cleanup = ownerDb.CreateCommand(
                "DELETE FROM public.memory_entries WHERE id = ANY(@ids)");
            cleanup.Parameters.AddWithValue("ids", ids);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task DataBackfillRoleMustNotBeTheApiOrGlobalAuditLogin()
    {
        var adminCs = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var appCs = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        var workerCs = Environment.GetEnvironmentVariable("OMNIA_TEST_BACKFILL_DB");
        var auditCs = Environment.GetEnvironmentVariable("OMNIA_TEST_AUDIT_DB");
        Assert.False(string.IsNullOrWhiteSpace(adminCs));
        Assert.False(string.IsNullOrWhiteSpace(appCs));
        Assert.False(string.IsNullOrWhiteSpace(workerCs));
        Assert.False(string.IsNullOrWhiteSpace(auditCs));
        await using var admin = NpgsqlDataSource.Create(adminCs!);
        await new PostgresMigrator(admin, "omnia_app", "omnia_retention", "omnia_backfill",
            "omnia_backfill_audit").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(appCs!);
        await using var worker = NpgsqlDataSource.Create(workerCs!);
        await using var audit = NpgsqlDataSource.Create(auditCs!);
        await PostgresBackfillRoleGuard.VerifyAsync(worker);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgresBackfillRoleGuard.VerifyAsync(app));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgresBackfillRoleGuard.VerifyAsync(audit));
    }

    private sealed class TemporaryInventory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "omnia-test-inventory-" + Guid.NewGuid().ToString("N") + ".json");

        public void Write(object value) => WriteRaw(JsonSerializer.Serialize(value));
        public void WriteRaw(string text)
        {
            File.WriteAllText(Path, text);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
    }
}
