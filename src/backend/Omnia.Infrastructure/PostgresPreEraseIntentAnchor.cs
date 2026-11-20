using Npgsql;

namespace Omnia.Infrastructure;

/// <summary>
/// Synthetic CI-only intent-prefix high-watermark custody in a SEPARATE
/// database on the THIRD PostgreSQL PGDATA. Reuses its EXECUTE-only SQL
/// checkpoint protocol; the intent checkpoint has its OWN domain-separated
/// HMAC and cannot be verified as a completed-erasure checkpoint.
/// No production offsite/WORM trust is supplied.
/// </summary>
public sealed class PostgresPreEraseIntentAnchorIssuer(
    NpgsqlDataSource intentReader,
    NpgsqlDataSource anchorWriter,
    NpgsqlDataSource anchorReader)
{
    public Task<int> IssueAsync(byte[] key, CancellationToken ct) =>
        IssueCoreAsync(key, bootstrap: false, ct);

    /// <summary>
    /// Operator approval is simulated by an explicit CI test flag, never
    /// an OIDC/authenticated operator permission or DR re-bootstrap grant.
    /// </summary>
    public Task<int> BootstrapAsync(byte[] key,
        bool operatorApproved, CancellationToken ct)
    {
        if (!operatorApproved)
            throw new InvalidDataException("INTENT_ANCHOR_BOOTSTRAP_DENIED.");
        return IssueCoreAsync(key, bootstrap: true, ct);
    }

    private async Task<int> IssueCoreAsync(
        byte[] key, bool bootstrap, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
            throw new InvalidDataException("INTENT_ANCHOR_KEY_INVALID.");
        await PostgresPreEraseIntentReader.VerifyReaderAsync(intentReader, ct);
        await PostgresWitnessAnchor.VerifyWriterAsync(anchorWriter, ct);
        await PostgresWitnessAnchorReader.VerifyReaderAsync(anchorReader, ct);

        var previous = await new PostgresWitnessAnchorReader(anchorReader)
            .ReadAsync(ct);
        if (bootstrap != (previous is null))
            throw new InvalidDataException("INTENT_ANCHOR_HEAD_OR_BOOTSTRAP_INVALID.");
        var snapshot = await PostgresPreEraseIntentSnapshot.ReadAsync(
            intentReader, IntentCheckpoint.MaxIntents, ct);
        if (previous is not null &&
            IntentCheckpoint.Verify(snapshot, previous.Checkpoint, key)
                != previous.Count)
            throw new InvalidDataException("INTENT_ANCHOR_PREFIX_INVALID.");

        var checkpoint = IntentCheckpoint.Create(snapshot, key);
        if (IntentCheckpoint.Verify(snapshot, checkpoint, key) != snapshot.Count)
            throw new InvalidDataException("INTENT_ANCHOR_PROPOSAL_INVALID.");
        return await new PostgresWitnessAnchor(anchorWriter)
            .AdvanceAsync(snapshot.Count, checkpoint, ct);
    }
}

/// <summary>
/// Only observes an independently preserved intent-prefix checkpoint.
/// Missing anchor, missing intents, mismatched MAC or rollback fail closed.
/// It NEVER grants DELETE/replay or API recovery readiness.
/// </summary>
public sealed class PostgresPreEraseIntentAnchorVerifier(
    NpgsqlDataSource intentReader, NpgsqlDataSource anchorReader)
{
    /// <summary>
    /// Checks that an EXACT remote request is inside an ALREADY durable
    /// authenticated prefix on the THIRD PGDATA. A request present only
    /// in a newer unanchored witness suffix is NOT safe to execute.
    /// This remains a CI-only preflight and NEVER proves source COMMIT.
    /// </summary>
    public async Task<int> VerifyRequestAsync(Guid requestId,
        string tenantId, string ownerSubject, byte[] key, CancellationToken ct)
    {
        if (requestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(ownerSubject))
            throw new ArgumentException("An exact synthetic request scope is required.");
        var (count, snapshot) = await ReadVerifiedPrefixAsync(key, ct);
        var remote = snapshot.SingleOrDefault(x => x.RequestId == requestId);
        if (remote is null || remote.Sequence > count ||
            remote.Sequence <= 0 ||
            remote.IntentType != "erase-actor-intent" ||
            !string.Equals(remote.TenantId, tenantId, StringComparison.Ordinal) ||
            !string.Equals(remote.OwnerSubject, ownerSubject, StringComparison.Ordinal))
            throw new InvalidDataException("PREERASE_INTENT_NOT_ANCHORED_FOR_ACTOR.");
        return count;
    }

    public async Task<int> VerifyAsync(byte[] key, CancellationToken ct)
    {
        var (count, _) = await ReadVerifiedPrefixAsync(key, ct);
        return count;
    }

    private async Task<(int Count, IReadOnlyList<PreEraseIntentReceipt> Snapshot)>
        ReadVerifiedPrefixAsync(byte[] key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
            throw new InvalidDataException("INTENT_ANCHOR_KEY_INVALID.");
        await PostgresPreEraseIntentReader.VerifyReaderAsync(intentReader, ct);
        await PostgresWitnessAnchorReader.VerifyReaderAsync(anchorReader, ct);
        var anchored = await new PostgresWitnessAnchorReader(anchorReader)
            .ReadAsync(ct) ??
            throw new InvalidDataException("INTENT_ANCHOR_MISSING.");
        var intents = await PostgresPreEraseIntentSnapshot.ReadAsync(
            intentReader, IntentCheckpoint.MaxIntents, ct);
        var count = IntentCheckpoint.Verify(intents, anchored.Checkpoint, key);
        if (count != anchored.Count)
            throw new InvalidDataException("INTENT_ANCHOR_COUNT_INVALID.");
        return (count, intents);
    }
}
