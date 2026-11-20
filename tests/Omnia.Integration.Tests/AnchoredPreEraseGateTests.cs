using System.Security.Cryptography;
using Npgsql;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// CI-only security boundary: a PRE-intent must be included in a previously
/// independently persisted HMAC checkpoint BEFORE deleting synthetic actor
/// data. The checkpoint is not a primary COMMIT/ACK or recovery permission.
/// </summary>
public sealed class AnchoredPreEraseGateTests
{
    [Fact]
    public async Task SyntheticErasureRequiresSignedAnchoredMatchingIntentBeforeAnyPrimaryMutation()
    {
        await using var admin = NpgsqlDataSource.Create(Required("OMNIA_TEST_DB"));
        await new PostgresMigrator(admin, "omnia_app", "omnia_retention",
            "omnia_backfill", "omnia_backfill_audit",
            dispatchRole: "omnia_dispatcher").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(Required("OMNIA_TEST_APP_DB"));
        await using var intentWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_WRITER_DB"));
        await using var intentReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_READER_DB"));
        await using var anchorWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_PREFLIGHT_ANCHOR_WRITER_DB"));
        await using var anchorReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_PREFLIGHT_ANCHOR_READER_DB"));
        await using var witnessReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_WITNESS_READER_DB"));
        await using var anchorAdmin = NpgsqlDataSource.Create(Required("OMNIA_TEST_PREFLIGHT_ANCHOR_ADMIN_DB"));
        var now = DateTimeOffset.UtcNow;
        var tenant = "anchored-gate-" + Guid.NewGuid().ToString("N");
        const string subject = "synthetic-anchored-erase-actor";
        var crypto = TestEnvelopeCrypto.Create();
        var consents = new PostgresConsentRepository(app);
        var memory = new PostgresMemoryRepository(app, crypto);
        var eraser = new PostgresPersonalDataService(app, crypto);
        await consents.GrantAsync(ConsentGrant.Issue(tenant, subject, "manual",
            ConsentGrant.MemoryCapture, "privacy-v1", now, now.AddDays(2)), default);
        await memory.UpsertAsync(MemoryEntry.Capture(
            tenant, subject, "manual", Guid.NewGuid().ToString("N"),
            "text/plain", "synthetic", "{\"gate\":true}",
            "synthetic erase gate note", [], now), default);

        var request = Guid.NewGuid();
        var key = RandomNumberGenerator.GetBytes(32);
        var writer = new PostgresPreEraseIntentWriter(intentWriter);
        var issuer = new PostgresPreEraseIntentAnchorIssuer(
            intentReader, anchorWriter, anchorReader);
        var verifier = new PostgresPreEraseIntentAnchorVerifier(
            intentReader, anchorReader);
        async Task Erase(Guid id, string t, string s,
            NpgsqlDataSource reader, NpgsqlDataSource a,
            byte[] signingKey)
        {
            var erased = await eraser.EraseWithAnchoredIntentAsync(
                id, t, s, reader, a, signingKey, default);
            Assert.Equal(1, erased.DeletedMemories);
        }

        // No remote intent, then intent present but NOT yet anchored:
        // both MUST be denied without touching source memory or consent.
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Erase(request, tenant, subject, intentReader, anchorReader, key));
        await writer.RecordAsync(request, tenant, subject, default);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Erase(request, tenant, subject, intentReader, anchorReader, key));
        Assert.Single(await memory.RecentAsync(tenant, subject, 10, default));

        // Only explicit synthetic first-time bootstrap establishes custody.
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            issuer.IssueAsync(key, default));
        await issuer.BootstrapAsync(key, operatorApproved: true, default);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            issuer.BootstrapAsync(key, operatorApproved: true, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Erase(request, tenant, subject, intentReader, anchorReader,
                RandomNumberGenerator.GetBytes(32)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Erase(request, tenant, subject + "-wrong", intentReader, anchorReader, key));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Erase(request, tenant + "-wrong", subject, intentReader, anchorReader, key));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Erase(request, tenant, subject, witnessReader, anchorReader, key));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Erase(request, tenant, subject, intentReader, anchorAdmin, key));
        Assert.Single(await memory.RecentAsync(tenant, subject, 10, default));

        // A later requested UUID is present in the witness but strictly
        // ABOVE the last independent high-watermark: it cannot delete.
        var notAnchored = Guid.NewGuid();
        await writer.RecordAsync(notAnchored, tenant, subject, default);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Erase(notAnchored, tenant, subject, intentReader, anchorReader, key));
        Assert.Single(await memory.RecentAsync(tenant, subject, 10, default));

        // The first request was already anchored and may proceed with the
        // same request UUID recorded by the primary transactional outbox.
        await Erase(request, tenant, subject, intentReader, anchorReader, key);
        Assert.Empty(await memory.RecentAsync(tenant, subject, 10, default));
        await using (var command = admin.CreateCommand("""
            SELECT count(*) FROM public.erasure_outbox
             WHERE id = @id AND tenant_id=@tenant AND owner_sub=@owner
               AND event_type = 'erase-actor'
            """))
        {
            command.Parameters.AddWithValue("id", request);
            command.Parameters.AddWithValue("tenant", tenant);
            command.Parameters.AddWithValue("owner", subject);
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync()
                ?? throw new InvalidDataException("Outbox missing.")));
        }
        Assert.Equal((await verifier.VerifyAsync(key, default)),
            (await new PostgresWitnessAnchorReader(anchorReader)
                .ReadAsync(default))!.Count);

        // The previously unanchored second request becomes eligible only
        // after a new signed, monotonic, third-cluster checkpoint.
        await issuer.IssueAsync(key, default);
        await verifier.VerifyRequestAsync(notAnchored, tenant, subject, key, default);

        // A repeated old request is NOT permitted to remove new captures:
        // source outbox PK violation aborts the entire actor transaction.
        var later = DateTimeOffset.UtcNow;
        await consents.GrantAsync(ConsentGrant.Issue(tenant, subject, "manual",
            ConsentGrant.MemoryCapture, "privacy-v2", later,
            later.AddDays(2)), default);
        await memory.UpsertAsync(MemoryEntry.Capture(
            tenant, subject, "manual", Guid.NewGuid().ToString("N"),
            "text/plain", "synthetic renewed", "{\"gate\":\"renewed\"}",
            "synthetic renewed note", [], later), default);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            Erase(request, tenant, subject, intentReader, anchorReader, key));
        Assert.Equal("23505", error.SqlState);
        Assert.Single(await memory.RecentAsync(tenant, subject, 10, default));
        Assert.Single((await consents.ListAsync(tenant, subject, default))
            .Where(x => x.RevokedAt is null));
    }

    private static string Required(string key) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } result
            ? result : throw new InvalidOperationException("Missing synthetic CI connection.");
}
