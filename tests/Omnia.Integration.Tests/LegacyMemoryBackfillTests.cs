using System.Text.Json;
using Npgsql;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Legacy fixtures are inserted with the migration-owner identity solely for
/// integration tests. The trigger is disabled and re-enabled atomically in one
/// transaction, so other test sessions never observe an unguarded write path.
/// </summary>
public sealed class LegacyMemoryBackfillTests
{
    [Fact]
    public async Task BackfillIsActorScopedBatchedAndPreservesCanonicalDataAndDataRights()
    {
        var adminCs = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var appCs = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        var backfillCs = Environment.GetEnvironmentVariable("OMNIA_TEST_BACKFILL_DB");
        Assert.False(string.IsNullOrWhiteSpace(adminCs));
        Assert.False(string.IsNullOrWhiteSpace(appCs));
        Assert.False(string.IsNullOrWhiteSpace(backfillCs));
        await using var admin = NpgsqlDataSource.Create(adminCs!);
        await new PostgresMigrator(admin, "omnia_app", "omnia_retention", "omnia_backfill").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(appCs!);
        await PostgresRuntimeGuard.VerifyAsync(app);
        var crypto = TestEnvelopeCrypto.Create();
        await using var backfillDb = NpgsqlDataSource.Create(backfillCs!);
        await PostgresRuntimeGuard.VerifyAsync(backfillDb);
        var worker = new PostgresLegacyMemoryBackfill(backfillDb, crypto);
        await using (var denied = backfillDb.CreateCommand("DELETE FROM memory_entries WHERE false"))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteNonQueryAsync());
            Assert.Equal("42501", error.SqlState);
        }
        var tenant = "legacy-" + Guid.NewGuid().ToString("N");
        var otherTenant = "legacy-" + Guid.NewGuid().ToString("N");
        var marker = "LEGACY_SYNTHETIC_" + Guid.NewGuid().ToString("N");
        var clock = DateTimeOffset.UtcNow;
        var now = new DateTimeOffset(clock.Ticks - (clock.Ticks % 10), TimeSpan.Zero);
        var oldReceived = now.AddDays(-31);
        var source = "manual";
        var aliceOne = Guid.NewGuid();
        var aliceTwo = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var outsider = Guid.NewGuid();

        await InsertLegacyAsync(admin, [
            (aliceOne, tenant, "alice", marker + "-1", now, marker),
            (aliceTwo, tenant, "alice", marker + "-2", oldReceived, marker),
            (bob, tenant, "bob", marker + "-bob", now, marker),
            (outsider, otherTenant, "alice", marker + "-other", now, marker)
        ]);

        // No grant is required to encrypt content already collected. A revoked
        // or expired consent must NOT be silently replaced by this maintenance.
        Assert.Equal(2L, await worker.CountLegacyAsync(tenant, "alice", default));
        Assert.Equal(1, await worker.BackfillBatchAsync(tenant, "alice", 1, default));
        Assert.Equal(1L, await worker.CountLegacyAsync(tenant, "alice", default));
        Assert.Equal(1, await worker.BackfillBatchAsync(tenant, "alice", 1, default));
        Assert.Equal(0, await worker.BackfillBatchAsync(tenant, "alice", 1, default));
        Assert.Equal(0L, await worker.CountLegacyAsync(tenant, "alice", default));
        Assert.Equal(1L, await worker.CountLegacyAsync(tenant, "bob", default));
        Assert.Equal(1L, await worker.CountLegacyAsync(otherTenant, "alice", default));

        await using (var inspect = admin.CreateCommand("""
            SELECT id, raw_content, normalized_json::text, summary, tags,
                   sealed_payload, received_at, expires_at, source, external_id,
                   embedding::text, occurred_at
            FROM memory_entries WHERE id = ANY(@ids) ORDER BY id
            """))
        {
            inspect.Parameters.AddWithValue("ids", new[] { aliceOne, aliceTwo });
            await using var reader = await inspect.ExecuteReaderAsync();
            var count = 0;
            while (await reader.ReadAsync())
            {
                count++;
                var id = reader.GetGuid(0);
                Assert.Equal("[sealed]", reader.GetString(1));
                Assert.Equal("{}", reader.GetString(2).Replace(" ", ""));
                Assert.Equal("[sealed]", reader.GetString(3));
                Assert.Empty(reader.GetFieldValue<string[]>(4));
                Assert.NotEmpty(reader.GetFieldValue<byte[]>(5));
                var originalReceived = id == aliceTwo ? oldReceived : now;
                Assert.Equal(originalReceived, reader.GetFieldValue<DateTimeOffset>(6));
                Assert.Equal(originalReceived.AddDays(30),
                    reader.GetFieldValue<DateTimeOffset>(7));
                Assert.Equal(source, reader.GetString(8));
                Assert.Equal(id == aliceOne ? marker + "-1" : marker + "-2",
                    reader.GetString(9));
                Assert.True(reader.IsDBNull(10));
                Assert.Equal(originalReceived, reader.GetFieldValue<DateTimeOffset>(11));
            }
            Assert.Equal(2, count);
        }

        // Expired rows remain excluded; encryption must never renew retention.
        var repo = new PostgresMemoryRepository(app, crypto);
        Assert.Contains(await repo.RecentAsync(tenant, "alice", 25, default),
            item => item.Id == aliceOne && item.Summary == marker);
        Assert.DoesNotContain(await repo.RecentAsync(tenant, "alice", 25, default),
            item => item.Id == aliceTwo);
        await using (var stream = new MemoryStream())
        {
            await new PostgresPersonalDataService(app, crypto).WriteExportAsync(
                tenant, "alice", stream, default);
            using var json = JsonDocument.Parse(stream.ToArray());
            var memories = json.RootElement.GetProperty("memoryEntries").EnumerateArray().ToArray();
            Assert.Equal(2, memories.Length);
            Assert.All(memories, item => {
                Assert.Equal("sealed-v1", item.GetProperty("storageProtection").GetString());
                Assert.Equal(marker, item.GetProperty("rawContent").GetString());
            });
        }
        Assert.Equal(2, (await new PostgresPersonalDataService(app, crypto)
            .EraseAsync(tenant, "alice", default)).DeletedMemories);
        Assert.Equal(0L, await worker.CountLegacyAsync(tenant, "alice", default));
        Assert.Equal(0, await worker.BackfillBatchAsync(tenant, "alice", 10, default));
    }

    [Fact]
    public async Task InvalidBatchSizeIsRejectedWithoutTouchingData()
    {
        var cs = Environment.GetEnvironmentVariable("OMNIA_TEST_BACKFILL_DB");
        Assert.False(string.IsNullOrWhiteSpace(cs));
        await using var app = NpgsqlDataSource.Create(cs!);
        var worker = new PostgresLegacyMemoryBackfill(app, TestEnvelopeCrypto.Create());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => worker.BackfillBatchAsync("fixture", "owner", 0, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => worker.BackfillBatchAsync("fixture", "owner", 501, default));
    }

    private static async Task InsertLegacyAsync(NpgsqlDataSource admin,
        IEnumerable<(Guid Id, string Tenant, string Owner, string External,
            DateTimeOffset Received, string Content)> fixtures)
    {
        await using var connection = await admin.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var disable = new NpgsqlCommand(
            "ALTER TABLE public.memory_entries DISABLE TRIGGER trg_memory_sealed_writes",
            connection, tx))
            await disable.ExecuteNonQueryAsync();
        foreach (var fixture in fixtures)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO public.memory_entries (
                    id, tenant_id, owner_sub, source, external_id, content_type,
                    raw_content, normalized_json, summary, tags, occurred_at,
                    received_at, expires_at)
                VALUES (@id, @tenant, @owner, 'manual', @external, 'text/plain',
                    @content, CAST(@json AS jsonb), @content, ARRAY['legacy']::text[],
                    @occurred, @received, @received + interval '30 days')
                """, connection, tx);
            insert.Parameters.AddWithValue("id", fixture.Id);
            insert.Parameters.AddWithValue("tenant", fixture.Tenant);
            insert.Parameters.AddWithValue("owner", fixture.Owner);
            insert.Parameters.AddWithValue("external", fixture.External);
            insert.Parameters.AddWithValue("content", fixture.Content);
            insert.Parameters.AddWithValue("json", JsonSerializer.Serialize(
                new { text = fixture.Content }));
            insert.Parameters.AddWithValue("occurred", fixture.Received);
            insert.Parameters.AddWithValue("received", fixture.Received);
            await insert.ExecuteNonQueryAsync();
        }
        await using (var enable = new NpgsqlCommand(
            "ALTER TABLE public.memory_entries ENABLE TRIGGER trg_memory_sealed_writes",
            connection, tx))
            await enable.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }
}
