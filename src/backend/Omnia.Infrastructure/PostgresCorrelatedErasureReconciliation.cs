using Npgsql;

namespace Omnia.Infrastructure;

/// <summary>
/// Only the visibility of three INDEPENDENT records is observed.
/// It is not a source commit certificate and must never authorize DR.
/// </summary>
public enum CorrelatedErasureVisibility
{
    /// <summary>
    /// Remote request exists; source COMMIT is not visible, which cannot
    /// distinguish 'never committed' from 'previously committed then PITR'.
    /// </summary>
    IntentOnlyCommitUnknown,

    /// <summary>
    /// Source's outbox event is visible in one consistent primary snapshot;
    /// the independent completed witness receipt is not yet visible.
    /// </summary>
    SourceEventPendingWitness,

    /// <summary>
    /// Matching intent, source outbox and independent completed receipt are
    /// presently visible. This does not prove global store erasure or auth.
    /// </summary>
    SourceAndWitnessVisible,

    /// <summary>
    /// Witness receipt survived but source outbox is not visible, e.g. after
    /// primary PITR. Cannot prove the recovered primary data is still erased.
    /// </summary>
    WitnessVisibleWithoutSource
}

/// <summary>
/// A non-authoritative observation with NO release/replay capability.
/// Not a permission token, a state machine write or signed evidence.
/// </summary>
public sealed record CorrelatedErasureObservation(
    CorrelatedErasureVisibility Visibility)
{
    public bool MayReleaseRecovery => false;
    public bool MayAutoReplay => false;
}

/// <summary>
/// CI-only, independent three-way read-only cross-check: restricted remote
/// pre-erase intent, bounded PRIMARY source snapshot, and remote completed
/// witness receipt. There is NO distributed consistent snapshot across
/// clusters, so states are observational, not historical commit proofs.
/// </summary>
public sealed class PostgresCorrelatedErasureReconciliation(
    NpgsqlDataSource sourceDispatch,
    NpgsqlDataSource intentReader,
    NpgsqlDataSource witnessReader)
{
    public async Task<CorrelatedErasureObservation> InspectAsync(
        Guid requestId, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("CI") != "true" ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new InvalidOperationException(
                "Correlated recovery observation is synthetic CI only.");
        if (requestId == Guid.Empty)
            throw new ArgumentException("A request UUID is required.", nameof(requestId));

        // No role fallback to owner/admin/runtime/writer. Validate EACH
        // independent SQL identity before making any observation.
        await PostgresPreEraseIntentReader.VerifyReaderAsync(intentReader, ct);
        await PostgresErasureDispatchReader.VerifyRoleAsync(sourceDispatch, ct);
        await PostgresErasureWitnessReader.VerifyReaderAsync(witnessReader, ct);

        var intent = await new PostgresPreEraseIntentReader(intentReader)
            .FindAsync(requestId, ct)
            ?? throw Invalid();
        var source = await new PostgresErasureLiveDeliveryAudit(
                sourceDispatch, witnessReader)
            .FindVisibleSourceEventAsync(requestId, ct);
        var witness = await new PostgresErasureWitnessReader(witnessReader)
            .FindAsync(requestId, ct);

        return Classify(intent, source, witness);
    }

    /// <summary>
    /// Pure fail-closed classifier for the three observed facts.
    /// Never treat an unmatched UUID, actor, tenant or event type as
    /// an erased actor. Even all 3 records matching DOES NOT release traffic.
    /// </summary>
    public static CorrelatedErasureObservation Classify(
        PreEraseIntentReceipt intent,
        ErasureDispatchEvent? source,
        ErasureWitnessReceipt? witness)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Sequence <= 0 || intent.RequestId == Guid.Empty ||
            intent.IntentType != "erase-actor-intent" ||
            !ValidScope(intent.TenantId, 128) ||
            !ValidScope(intent.OwnerSubject, 255))
            throw Invalid();

        if (source is not null &&
            (source.Id != intent.RequestId ||
             source.EventType != "erase-actor" ||
             !string.Equals(source.TenantId, intent.TenantId,
                 StringComparison.Ordinal) ||
             !string.Equals(source.OwnerSubject, intent.OwnerSubject,
                 StringComparison.Ordinal)))
            throw Invalid();

        if (witness is not null &&
            (witness.EventId != intent.RequestId ||
             witness.Sequence <= 0 ||
             witness.EventType != "erase-actor" ||
             !string.Equals(witness.TenantId, intent.TenantId,
                 StringComparison.Ordinal) ||
             !string.Equals(witness.OwnerSubject, intent.OwnerSubject,
                 StringComparison.Ordinal)))
            throw Invalid();

        // Enforce same immutable metadata between primary and completed
        // witness event, using existing fail-closed comparison as well.
        if (source is not null && witness is not null)
            PostgresErasureLiveDeliveryAudit.RequireMatchingReceipt(
                source, witness);

        return new CorrelatedErasureObservation(
            source is null
                ? witness is null
                    ? CorrelatedErasureVisibility.IntentOnlyCommitUnknown
                    : CorrelatedErasureVisibility.WitnessVisibleWithoutSource
                : witness is null
                    ? CorrelatedErasureVisibility.SourceEventPendingWitness
                    : CorrelatedErasureVisibility.SourceAndWitnessVisible);
    }

    private static bool ValidScope(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= max &&
        value.All(x => !char.IsControl(x));

    private static InvalidDataException Invalid() =>
        new("ERASURE_RECONCILIATION_EVIDENCE_INVALID.");
}
