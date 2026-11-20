using Npgsql;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// ADR 0034: opt-in synthetic request ID is identical on remote intent
/// and committed primary outbox, ONLY after a scope-checked reader preflight.
/// A pre-intent still does not attest the source DELETE committed.
/// </summary>
public sealed class CorrelatedErasureIntentTests
{
    [Fact]
    public async Task RecordedIntentAuthorizesOnlyMatchingSourceTransactionAndWitnessEventId()
    {
        await using var admin = NpgsqlDataSource.Create(Required("OMNIA_TEST_DB"));
        await new PostgresMigrator(admin, "omnia_app", "omnia_retention",
            "omnia_backfill", "omnia_backfill_audit",
            dispatchRole: "omnia_dispatcher").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(Required("OMNIA_TEST_APP_DB"));
        await using var dispatch = NpgsqlDataSource.Create(Required("OMNIA_TEST_DISPATCH_DB"));
        await using var intentWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_WRITER_DB"));
        await using var intentReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_READER_DB"));
        await using var wrongReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_WITNESS_READER_DB"));
        await using var erasureWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_WITNESS_WRITER_DB"));
        await using var erasureReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_WITNESS_READER_DB"));

        var tenant = "linked-intent-" + Guid.NewGuid().ToString("N");
        const string owner = "synthetic-linked-actor";
        var crypto = TestEnvelopeCrypto.Create();
        var data = new PostgresPersonalDataService(app, crypto);
        var now = DateTimeOffset.UtcNow;
        var consents = new PostgresConsentRepository(app);
        var memories = new PostgresMemoryRepository(app, crypto);
        await consents.GrantAsync(ConsentGrant.Issue(tenant, owner, "manual",
            ConsentGrant.MemoryCapture, "privacy-v1", now,
            now.AddDays(1)), default);
        await memories.UpsertAsync(MemoryEntry.Capture(
            tenant, owner, "manual", Guid.NewGuid().ToString("N"),
            "text/plain", "synthetic", "{\"sample\":true}",
            "synthetic", [], now), default);

        var requestId = Guid.NewGuid();
        var writer = new PostgresPreEraseIntentWriter(intentWriter);
        // Absent intent or wrong remote capability cannot delete any actor data.
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            data.EraseWithWitnessedIntentAsync(
                requestId, tenant, owner, intentReader, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            data.EraseWithWitnessedIntentAsync(
                requestId, tenant, owner, wrongReader, default));
        Assert.Single(await memories.RecentAsync(tenant, owner, 10, default));

        Assert.True(await writer.RecordAsync(requestId, tenant, owner, default) > 0);
        // Scope conflicts against a valid remote request MUST fail closed.
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            data.EraseWithWitnessedIntentAsync(
                requestId, tenant, owner + "-forged", intentReader, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            data.EraseWithWitnessedIntentAsync(
                requestId, tenant + "-forged", owner, intentReader, default));
        Assert.Single(await memories.RecentAsync(tenant, owner, 10, default));

        var result = await data.EraseWithWitnessedIntentAsync(
            requestId, tenant, owner, intentReader, default);
        Assert.Equal(1, result.DeletedMemories);
        Assert.Equal(1, result.DeletedConsents);
        Assert.Empty(await memories.RecentAsync(tenant, owner, 10, default));

        await using (var query = admin.CreateCommand("""
            SELECT count(*) FROM public.erasure_outbox
             WHERE id=@id AND tenant_id=@tenant AND owner_sub=@owner
               AND event_type='erase-actor'
            """))
        {
            query.Parameters.AddWithValue("id", requestId);
            query.Parameters.AddWithValue("tenant", tenant);
            query.Parameters.AddWithValue("owner", owner);
            Assert.Equal(1L, (long)(await query.ExecuteScalarAsync()
                ?? throw new InvalidDataException("Source event missing.")));
        }

        // Existing publisher now preserves the EXACT remote-intent UUID
        // in an independently persisted completed witness event.
        await new PostgresErasureOutboxWitnessPublisher(dispatch, erasureWriter)
            .PublishBatchAsync(1000, default);
        var receipt = await new PostgresErasureWitnessReader(erasureReader)
            .FindAsync(requestId, default);
        Assert.NotNull(receipt);
        Assert.Equal(tenant, receipt!.TenantId);
        Assert.Equal(owner, receipt.OwnerSubject);
        Assert.Equal("erase-actor", receipt.EventType);

        // A stale request ID MUST NOT erase a later fresh capture even if
        // the old remote intent remains readable. PK uniqueness rolls
        // the entire second source transaction back.
        var later = DateTimeOffset.UtcNow;
        await consents.GrantAsync(ConsentGrant.Issue(tenant, owner, "manual",
            ConsentGrant.MemoryCapture, "privacy-v2", later,
            later.AddDays(1)), default);
        await memories.UpsertAsync(MemoryEntry.Capture(
            tenant, owner, "manual", Guid.NewGuid().ToString("N"),
            "text/plain", "new synthetic content",
            "{\"version\":2}", "synthetic renewed",
            [], later), default);
        var err = await Assert.ThrowsAsync<PostgresException>(() =>
            data.EraseWithWitnessedIntentAsync(
                requestId, tenant, owner, intentReader, default));
        Assert.Equal("23505", err.SqlState);
        Assert.Single(await memories.RecentAsync(tenant, owner, 10, default));
        Assert.Single((await consents.ListAsync(tenant, owner, default))
            .Where(grant => grant.RevokedAt is null));
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Synthetic PostgreSQL CI missing.");
}
