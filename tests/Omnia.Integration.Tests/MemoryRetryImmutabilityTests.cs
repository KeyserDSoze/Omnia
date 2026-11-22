using System.Text.Json;
using Npgsql;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// A retry is an at-most-once write to an unexpired canonical identity,
/// not a new encrypted update. Real PostgreSQL restricted FORCE RLS role;
/// admin is used ONLY to observe ciphertext and simulate expiry in fixture.
/// </summary>
public sealed class MemoryRetryImmutabilityTests
{
    [Fact]
    public async Task LiveRetryKeepsOriginalSealedBytesAndRejectsConflictingBody()
    {
        var adminCs = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var runtimeCs = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        Assert.False(string.IsNullOrWhiteSpace(adminCs));
        Assert.False(string.IsNullOrWhiteSpace(runtimeCs));

        await using var admin = NpgsqlDataSource.Create(adminCs!);
        await new PostgresMigrator(admin, "omnia_app").MigrateAsync();
        await using var db = NpgsqlDataSource.Create(runtimeCs!);
        await PostgresRuntimeGuard.VerifyAsync(db);
        var repo = new PostgresMemoryRepository(db, TestEnvelopeCrypto.Create());
        var consents = new PostgresConsentRepository(db);
        var tenant = "immutable-" + Guid.NewGuid().ToString("N");
        const string actor = "original-owner";
        const string otherActor = "another-owner";
        var now = DateTimeOffset.UtcNow.AddMinutes(-3);
        var external = "same-id-" + Guid.NewGuid().ToString("N");
        await consents.GrantAsync(ConsentGrant.Issue(tenant, actor, "manual",
            ConsentGrant.MemoryCapture, "privacy-v1", now, now.AddDays(7)), default);
        await consents.GrantAsync(ConsentGrant.Issue(tenant, otherActor, "manual",
            ConsentGrant.MemoryCapture, "privacy-v1", now, now.AddDays(7)), default);
        await consents.GrantAsync(ConsentGrant.Issue(tenant + "-other", actor, "manual",
            ConsentGrant.MemoryCapture, "privacy-v1", now, now.AddDays(7)), default);

        MemoryEntry Capture(string owner, string tenantId, string raw) =>
            MemoryEntry.Capture(tenantId, owner, "manual", external,
                "text/plain", raw, JsonSerializer.Serialize(new { text = raw }),
                raw, ["lavoro"], now,
                Enumerable.Repeat(0.3f, 768).ToArray(), "test-vector");

        async Task<(byte[] Envelope, DateTimeOffset Received,
                    DateTimeOffset Expires, DateTimeOffset Occurred)> Snapshot(Guid id)
        {
            await using var query = admin.CreateCommand("""
                SELECT sealed_payload, received_at, expires_at, occurred_at
                FROM memory_entries WHERE id = @id
                """);
            query.Parameters.AddWithValue("id", id);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetFieldValue<byte[]>(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetFieldValue<DateTimeOffset>(3));
        }

        var initial = Capture(actor, tenant, "SYNTHETIC first original");
        var saved = await repo.UpsertAsync(initial, default);
        var original = await Snapshot(saved.Id);
        var retried = await repo.UpsertAsync(initial, default);
        Assert.Equal(saved.Id, retried.Id);
        var repeated = await Snapshot(saved.Id);
        // RED: current UPSERT always rotates ciphertext/DEK on a live retry.
        Assert.Equal(original.Envelope, repeated.Envelope);
        Assert.Equal(original.Received, repeated.Received);
        Assert.Equal(original.Expires, repeated.Expires);
        Assert.Equal(original.Occurred, repeated.Occurred);

        var modified = Capture(actor, tenant, "SYNTHETIC different collision");
        await Assert.ThrowsAnyAsync<Exception>(() => repo.UpsertAsync(modified, default));
        var unchanged = await Snapshot(saved.Id);
        Assert.Equal(original.Envelope, unchanged.Envelope);
        Assert.Equal(original.Expires, unchanged.Expires);
        Assert.Contains(await repo.RecentAsync(tenant, actor, 10, default),
            row => row.Id == saved.Id && row.Summary == "SYNTHETIC first original");

        // Same externalId is not global: actor/tenant are part of the RLS key.
        var foreignActor = await repo.UpsertAsync(
            Capture(otherActor, tenant, "SYNTHETIC another actor"), default);
        var foreignTenant = await repo.UpsertAsync(
            Capture(actor, tenant + "-other", "SYNTHETIC another tenant"), default);
        Assert.NotEqual(saved.Id, foreignActor.Id);
        Assert.NotEqual(saved.Id, foreignTenant.Id);
        Assert.DoesNotContain(await repo.RecentAsync(tenant, otherActor, 10, default),
            row => row.Id == saved.Id);
        Assert.DoesNotContain(await repo.RecentAsync(tenant + "-other", actor, 10, default),
            row => row.Id == saved.Id);

        // Fixture-only expiry. Reacquisition after expiry is allowed by the
        // established TTL contract, but requires a fresh active consent.
        await using (var age = admin.CreateCommand("""
            UPDATE memory_entries SET expires_at = now() - interval '1 minute'
            WHERE id = @id
            """))
        {
            age.Parameters.AddWithValue("id", saved.Id);
            Assert.Equal(1, await age.ExecuteNonQueryAsync());
        }
        var recaptured = await repo.UpsertAsync(modified, default);
        Assert.Equal(saved.Id, recaptured.Id);
        Assert.Contains(await repo.RecentAsync(tenant, actor, 10, default),
            row => row.Id == saved.Id && row.Summary == "SYNTHETIC different collision");
        Assert.True((await Snapshot(saved.Id)).Expires > DateTimeOffset.UtcNow);
    }
}
