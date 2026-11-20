using Npgsql;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Security regression: requester intent, source commit visibility, and
/// completed witness receipt are distinct facts. Even when all are visible,
/// observation NEVER authorizes restoring production ingress or re-deletion.
/// </summary>
public sealed class CorrelatedErasureReconciliationTests
{
    [Fact]
    public async Task IndependentIntentTransitionsFromUnknownToPendingToWitnessVisibleWithoutRelease()
    {
        await using var admin = NpgsqlDataSource.Create(Require("OMNIA_TEST_DB"));
        await new PostgresMigrator(admin, "omnia_app", "omnia_retention",
            "omnia_backfill", "omnia_backfill_audit",
            dispatchRole: "omnia_dispatcher").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(Require("OMNIA_TEST_APP_DB"));
        await using var dispatch = NpgsqlDataSource.Create(Require("OMNIA_TEST_DISPATCH_DB"));
        await using var intentWriter = NpgsqlDataSource.Create(Require("OMNIA_TEST_INTENT_WRITER_DB"));
        await using var intentReader = NpgsqlDataSource.Create(Require("OMNIA_TEST_INTENT_READER_DB"));
        await using var witnessWriter = NpgsqlDataSource.Create(Require("OMNIA_TEST_WITNESS_WRITER_DB"));
        await using var witnessReader = NpgsqlDataSource.Create(Require("OMNIA_TEST_WITNESS_READER_DB"));

        var tenant = "reconciliation-" + Guid.NewGuid().ToString("N");
        const string actor = "synthetic-reconciliation-owner";
        var id = Guid.NewGuid();
        var crypto = TestEnvelopeCrypto.Create();
        var memory = new PostgresMemoryRepository(app, crypto);
        var now = DateTimeOffset.UtcNow;
        await new PostgresConsentRepository(app).GrantAsync(
            ConsentGrant.Issue(tenant, actor, "manual",
                ConsentGrant.MemoryCapture, "privacy-v1", now, now.AddDays(1)),
            default);
        await memory.UpsertAsync(MemoryEntry.Capture(tenant, actor, "manual",
            Guid.NewGuid().ToString("N"), "text/plain", "synthetic", "{\"test\":true}",
            "synthetic", [], now), default);

        var inspector = new PostgresCorrelatedErasureReconciliation(
            dispatch, intentReader, witnessReader);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            inspector.InspectAsync(id, default));
        Assert.Single(await memory.RecentAsync(tenant, actor, 10, default));

        await new PostgresPreEraseIntentWriter(intentWriter).RecordAsync(
            id, tenant, actor, default);

        var intentOnly = await inspector.InspectAsync(id, default);
        Assert.Equal(CorrelatedErasureVisibility.IntentOnlyCommitUnknown,
            intentOnly.Visibility);
        Assert.False(intentOnly.MayReleaseRecovery);
        Assert.False(intentOnly.MayAutoReplay);
        // An intent alone is NOT evidence source DELETE succeeded.
        Assert.Single(await memory.RecentAsync(tenant, actor, 10, default));

        await new PostgresPersonalDataService(app, crypto)
            .EraseWithWitnessedIntentAsync(id, tenant, actor, intentReader, default);
        var pending = await inspector.InspectAsync(id, default);
        Assert.Equal(CorrelatedErasureVisibility.SourceEventPendingWitness,
            pending.Visibility);
        Assert.False(pending.MayReleaseRecovery);
        Assert.False(pending.MayAutoReplay);

        await new PostgresErasureOutboxWitnessPublisher(dispatch, witnessWriter)
            .PublishBatchAsync(1000, default);
        var delivered = await inspector.InspectAsync(id, default);
        Assert.Equal(CorrelatedErasureVisibility.SourceAndWitnessVisible,
            delivered.Visibility);
        Assert.False(delivered.MayReleaseRecovery);
        Assert.False(delivered.MayAutoReplay);

        // SQL capability boundaries are enforced before ANY observation.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresCorrelatedErasureReconciliation(
                app, intentReader, witnessReader).InspectAsync(id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresCorrelatedErasureReconciliation(
                dispatch, witnessReader, witnessReader).InspectAsync(id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresCorrelatedErasureReconciliation(
                dispatch, intentReader, witnessWriter).InspectAsync(id, default));
    }

    [Fact]
    public void ScopeMismatchAndWitnessOnlyStateFailClosedEvenWithoutPrimaryEvent()
    {
        var id = Guid.NewGuid();
        var intent = new PreEraseIntentReceipt(5, id, "synthetic-tenant",
            "synthetic-owner", "erase-actor-intent");
        var source = new ErasureDispatchEvent(id, intent.TenantId,
            intent.OwnerSubject, "erase-actor", DateTimeOffset.UtcNow);
        var ack = new ErasureWitnessReceipt(7, id,
            intent.TenantId, intent.OwnerSubject, "erase-actor");

        var witnessOnly = PostgresCorrelatedErasureReconciliation.Classify(
            intent, null, ack);
        Assert.Equal(CorrelatedErasureVisibility.WitnessVisibleWithoutSource,
            witnessOnly.Visibility);
        Assert.False(witnessOnly.MayReleaseRecovery);
        Assert.False(witnessOnly.MayAutoReplay);

        Assert.Throws<InvalidDataException>(() =>
            PostgresCorrelatedErasureReconciliation.Classify(
                intent, source with { OwnerSubject = "forged" }, null));
        Assert.Throws<InvalidDataException>(() =>
            PostgresCorrelatedErasureReconciliation.Classify(
                intent, null, ack with { TenantId = "forged" }));
        Assert.Throws<InvalidDataException>(() =>
            PostgresCorrelatedErasureReconciliation.Classify(
                intent, source, ack with { EventId = Guid.NewGuid() }));
        Assert.Throws<InvalidDataException>(() =>
            PostgresCorrelatedErasureReconciliation.Classify(
                intent with { Sequence = 0 }, null, null));
        Assert.Throws<InvalidDataException>(() =>
            PostgresCorrelatedErasureReconciliation.Classify(
                intent with { IntentType = "erase-actor" }, null, null));
        Assert.Throws<InvalidDataException>(() =>
            PostgresCorrelatedErasureReconciliation.Classify(
                intent, source, ack with { Sequence = 0 }));
        // No remote forged receipt is appended: CI shared witness unchanged.
    }

    private static string Require(string key) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Synthetic PostgreSQL CI missing.");
}
