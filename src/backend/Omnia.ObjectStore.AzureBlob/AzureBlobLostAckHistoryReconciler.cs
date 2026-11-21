using System.Security.Cryptography;
using Omnia.Infrastructure;

namespace Omnia.ObjectStore.AzureBlob;

/// <summary>
/// Positive evidence of one Azure-LOCKED attempt version visible from
/// a provider-supplied listing after the caller lost the upload ACK.
/// This is NOT complete history, no safe retry, no erasure/recovery grant.
/// </summary>
public sealed record AzureLostAckHistoryOutcome(
    Guid RequestId, bool PreviouslyObserved, string? VersionId)
{
    public bool Uncertain => !PreviouslyObserved;
    public bool MayAutoRetry => false;
    public bool MayReleaseRecovery => false;
    public bool ProvesSourceDeleteCommit => false;
    public bool ProvesCompleteHistory => false;
    public bool ProvesMonotonicHistory => false;
}

/// <summary>
/// Read-only forensic reconciliation of a lost Azure Blob upload ACK.
///
/// The caller supplies the authenticated actor and independent HMAC keys,
/// but deliberately DOES NOT have a VersionId from the lost ACK. A bounded
/// history scan may identify ONE visible canonical version. Only then is
/// that EXACT version authenticated via AzureLocked HEAD/GET and HMAC.
///
/// A provider can hide or roll back entries, a second version may appear
/// after scanning, and an upload can commit without completing the lock.
/// Even a positive result MUST NEVER authorize a retry, DELETE or recovery.
/// </summary>
public sealed class AzureBlobLostAckHistoryReconciler
{
    private const int PageSize = 2;
    private const int MaxPages = 8;
    private readonly IAzureBlobVersionHistory _history;
    private readonly IImmutableAttemptObjectReader _reader;
    private readonly string _storeIdentity;

    public AzureBlobLostAckHistoryReconciler(
        IAzureBlobVersionHistory history,
        IImmutableAttemptObjectReader reader,
        string storeIdentity)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        if (string.IsNullOrWhiteSpace(storeIdentity) ||
            storeIdentity.Length > 256 || storeIdentity.Any(char.IsControl))
            throw new InvalidDataException("AZURE_LOST_ACK_STORE_UNTRUSTED.");
        _storeIdentity = storeIdentity;
    }

    public async Task<AzureLostAckHistoryOutcome> ReconcileAsync(
        Guid requestId, string tenantId, string ownerSubject,
        byte[] scopeKey, byte[] evidenceKey,
        DateTimeOffset nowUtc, CancellationToken ct)
    {
        // Reject invalid call data BEFORE any provider I/O.
        if (requestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(ownerSubject) ||
            scopeKey is not { Length: 32 } ||
            evidenceKey is not { Length: 32 } ||
            CryptographicOperations.FixedTimeEquals(scopeKey, evidenceKey) ||
            nowUtc.Offset != TimeSpan.Zero)
            return Uncertain(requestId);

        try
        {
            var objectKey = ImmutableAttemptEvidence.ObjectKey(requestId);
            string? next = null;
            string? visibleVersionId = null;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var completed = false;

            for (var n = 0; n < MaxPages; n++)
            {
                ct.ThrowIfCancellationRequested();
                var page = await _history.ListAsync(
                    objectKey, next, PageSize, ct);

                // Empty/oversized pages and a missing response cannot prove
                // any absence or any complete catalogue; stop before GET.
                if (page?.Entries is not { Count: >= 1 and <= PageSize })
                    return Uncertain(requestId);

                foreach (var entry in page.Entries)
                {
                    if (entry is null ||
                        !string.Equals(entry.ObjectKey, objectKey,
                            StringComparison.Ordinal) ||
                        !AzureBlobWormHistoryAudit.ValidVersion(entry.VersionId) ||
                        entry.Deleted || entry.HasSnapshot ||
                        !entry.IsCurrentVersion ||
                        visibleVersionId is not null)
                        return Uncertain(requestId);

                    // This is a *candidate*, not a trusted version pin.
                    visibleVersionId = entry.VersionId;
                }

                if (page.NextCursor is null)
                {
                    completed = true;
                    break;
                }

                if (!AzureBlobWormHistoryAudit.ValidCursor(page.NextCursor) ||
                    !visited.Add(page.NextCursor))
                    return Uncertain(requestId);
                next = page.NextCursor;
            }

            if (!completed || visibleVersionId is null)
                return Uncertain(requestId);

            // Verify the exact VersionId discovered by the untrusted listing.
            // This does NOT create an independently durable version pin.
            var observation = await ImmutableAttemptEvidence.VerifyAsync(
                _reader, requestId, tenantId, ownerSubject,
                scopeKey, evidenceKey, _storeIdentity,
                visibleVersionId, nowUtc, ct);

            if (!string.Equals(observation.VersionId, visibleVersionId,
                    StringComparison.Ordinal) ||
                observation.VerifiedImmutabilityMode !=
                    ImmutableAttemptImmutabilityMode.AzureLocked ||
                observation.VerifiedRetainUntilUtc <= nowUtc.AddHours(1))
                return Uncertain(requestId);

            return new AzureLostAckHistoryOutcome(
                requestId, true, visibleVersionId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 403, timeout, lost response, malformed metadata/clock,
            // wrong owner/MAC, cancellation and locked policy mismatch
            // are all UNCERTAIN, NEVER evidence of "not written".
            return Uncertain(requestId);
        }
    }

    private static AzureLostAckHistoryOutcome Uncertain(Guid id) =>
        new(id, false, null);
}
