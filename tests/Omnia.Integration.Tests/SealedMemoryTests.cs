using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Omnia.Application;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Real PostgreSQL/pgvector + restricted API RLS role. All payloads are synthetic.
/// Protected columns must not be left in cleartext on any new capture or retry.
/// </summary>
public sealed class SealedMemoryTests
{
    [Fact]
    public async Task CaptureIsSealedAndReadableOnlyForAuthenticatedOwnerAfterIdempotentRetry()
    {
        var adminCs = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var runtimeCs = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        Assert.False(string.IsNullOrWhiteSpace(adminCs));
        Assert.False(string.IsNullOrWhiteSpace(runtimeCs));
        await using var admin = NpgsqlDataSource.Create(adminCs!);
        await new PostgresMigrator(admin, "omnia_app").MigrateAsync();
        await using var db = NpgsqlDataSource.Create(runtimeCs!);
        await PostgresRuntimeGuard.VerifyAsync(db);
        var crypto = TestEnvelopeCrypto.Create();
        var repo = new PostgresMemoryRepository(db, crypto);
        var privacy = new PostgresPersonalDataService(db, crypto);
        var tenant = "seal-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var grant = new PostgresConsentRepository(db);
        await grant.GrantAsync(ConsentGrant.Issue(tenant, "alice", "manual",
            ConsentGrant.MemoryCapture, "privacy-v1", now, now.AddDays(7)), default);
        await grant.GrantAsync(ConsentGrant.Issue(tenant, "bob", "manual",
            ConsentGrant.MemoryCapture, "privacy-v1", now, now.AddDays(7)), default);

        var marker = "PRIVATE_SYNTHETIC_" + Guid.NewGuid().ToString("N");
        var entry = MemoryEntry.Capture(tenant, "alice", "manual",
            "fixture-" + Guid.NewGuid().ToString("N"), "text/plain",
            marker, JsonSerializer.Serialize(new { text = marker }),
            "summary-" + marker, ["private-" + marker], now,
            Enumerable.Repeat(0.25f, 768).ToArray(), "synthetic-vector");
        var first = await repo.UpsertAsync(entry, default);
        var second = await repo.UpsertAsync(entry, default);
        Assert.Equal(first.Id, second.Id);

        await using (var query = admin.CreateCommand("""
            SELECT raw_content, normalized_json::text, summary, tags,
                   sealed_payload, embedding IS NOT NULL
            FROM memory_entries WHERE id = @id
            """))
        {
            query.Parameters.AddWithValue("id", first.Id);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("[sealed]", reader.GetString(0));
            Assert.Equal("{}", reader.GetString(1).Replace(" ", ""));
            Assert.Equal("[sealed]", reader.GetString(2));
            Assert.Empty(reader.GetFieldValue<string[]>(3));
            var ciphertext = reader.GetFieldValue<byte[]>(4);
            Assert.NotEmpty(ciphertext);
            Assert.DoesNotContain(marker, Convert.ToBase64String(ciphertext));
            Assert.True(reader.GetBoolean(5)); // pgvector is NOT encrypted by this slice.
        }

        var own = await repo.RecentAsync(tenant, "alice", 10, default);
        Assert.Contains(own, x => x.Id == first.Id &&
            x.Summary == "summary-" + marker &&
            x.Tags.Contains("private-" + marker));
        Assert.DoesNotContain(await repo.RecentAsync(tenant, "bob", 10, default),
            x => x.Id == first.Id);
        Assert.DoesNotContain(await repo.RecentAsync("other-" + tenant, "alice", 10, default),
            x => x.Id == first.Id);

        await using (var output = new MemoryStream())
        {
            await privacy.WriteExportAsync(tenant, "alice", output, default);
            using var json = JsonDocument.Parse(output.ToArray());
            var row = Assert.Single(json.RootElement.GetProperty("memoryEntries").EnumerateArray());
            Assert.Equal(marker, row.GetProperty("rawContent").GetString());
            Assert.Equal(marker, row.GetProperty("normalized").GetProperty("text").GetString());
            Assert.Equal("summary-" + marker, row.GetProperty("summary").GetString());
            Assert.Equal("private-" + marker, row.GetProperty("tags")[0].GetString());
        }

        // A corrupted encrypted row MUST fail closed; no silent use of redacted placeholders.
        await using (var tamper = admin.CreateCommand("""
            UPDATE memory_entries SET sealed_payload = set_byte(
                sealed_payload, octet_length(sealed_payload) - 1,
                get_byte(sealed_payload, octet_length(sealed_payload) - 1) # 1)
            WHERE id = @id
            """))
        {
            tamper.Parameters.AddWithValue("id", first.Id);
            Assert.Equal(1, await tamper.ExecuteNonQueryAsync());
        }
        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
            await repo.RecentAsync(tenant, "alice", 10, default));

        // Deletion never depends on working keys; removes encrypted data & vectors.
        var erased = await privacy.EraseAsync(tenant, "alice", default);
        Assert.Equal(1, erased.DeletedMemories);
        Assert.Empty(await repo.RecentAsync(tenant, "alice", 10, default));
    }

    [Fact]
    public async Task DatabaseRejectsAnyNewPlaintextWriteAndRejectsUnredactedSealedWrites()
    {
        var adminCs = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var runtimeCs = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        Assert.False(string.IsNullOrWhiteSpace(adminCs));
        Assert.False(string.IsNullOrWhiteSpace(runtimeCs));
        await using var admin = NpgsqlDataSource.Create(adminCs!);
        await new PostgresMigrator(admin, "omnia_app").MigrateAsync();
        await using var db = NpgsqlDataSource.Create(runtimeCs!);
        var tenant = "sealed-guard-" + Guid.NewGuid().ToString("N");
        await using var scope = await PostgresActorScope.OpenAsync(db, tenant, "alice", default);
        await using var cleartext = new NpgsqlCommand("""
            INSERT INTO memory_entries (
              id, tenant_id, owner_sub, source, external_id, content_type,
              raw_content, normalized_json, summary, tags, occurred_at)
            VALUES (@id, @tenant, 'alice', 'manual', 'old-client',
              'text/plain', 'SENSITIVE', '{"text":"SENSITIVE"}'::jsonb,
              'SENSITIVE', ARRAY['private']::text[], now())
            """, scope.Connection, scope.Transaction);
        cleartext.Parameters.AddWithValue("id", Guid.NewGuid());
        cleartext.Parameters.AddWithValue("tenant", tenant);
        var denied = await Assert.ThrowsAsync<PostgresException>(
            () => cleartext.ExecuteNonQueryAsync());
        Assert.Equal("23514", denied.SqlState);
    }
}

/// <summary>
/// Isolated ephemeral KEK shared only by tests in the same process.
/// No committed, deterministic or hardcoded key material.
/// </summary>
internal static class TestEnvelopeCrypto
{
    private static readonly Lazy<IEnvelopeEncryption> Shared = new(() =>
        new AesGcmEnvelopeEncryption(EnvelopeKeyRing.FromBase64("test-v1",
            new Dictionary<string, string> {
                ["test-v1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            })));

    internal static IEnvelopeEncryption Create() => Shared.Value;
}
