using System.Security.Cryptography;
using Omnia.Infrastructure;

namespace Omnia.ObjectStore.AzureBlob;

/// <summary>
/// A bounded positive forensic observation of exactly one Azure-visible
/// signed PIN version, authenticated together with its evidence version.
/// Not an out-of-band monotonic ledger or source DELETE COMMIT proof.
/// </summary>
public sealed record AzureBlobPinHistoryAuditOutcome(
    Guid RequestId, bool SingleVisiblePinVerified, string? PinVersionId)
{
    public bool Uncertain => !SingleVisiblePinVerified;
    public bool MayAutoRetry => false;
    public bool MayReleaseRecovery => false;
    public bool ProvesCompleteHistory => false;
    public bool ProvesMonotonicHistory => false;
    public bool ProvesSourceDeleteCommit => false;
}

/// <summary>
/// ADR 0054: READ ONLY. Audit the provider-visible history of a SIGNED PIN,
/// not the original evidence key audited in ADR 0051.
///
/// The history source is under provider control, may omit versions, and
/// is not durable across failure/admin compromise. Two exact externally
/// retained VersionIds are required to verify the pin and evidence.
/// This class has NO writer/DELETE/retry/ready API.
/// </summary>
public sealed class AzureBlobWormPinHistoryAudit
{
    private const int PageSize = 2;
    private const int MaxPages = 8;
    private readonly IAzureBlobVersionHistory _history;
    private readonly AzureBlobVersionPinService _pinService;

    public AzureBlobWormPinHistoryAudit(
        IAzureBlobVersionHistory history,
        AzureBlobVersionPinService pinService)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _pinService = pinService ?? throw new ArgumentNullException(nameof(pinService));
    }

    public async Task<AzureBlobPinHistoryAuditOutcome> AuditAsync(
        Guid requestId, string tenantId, string ownerSubject,
        byte[] scopeKey, byte[] evidenceKey, byte[] pinKey,
        string expectedEvidenceVersionId, string expectedPinVersionId,
        DateTimeOffset nowUtc, CancellationToken ct)
    {
        // Invalid caller data may NOT trigger network requests or cause
        // a fallback to mutable latest after database rollback.
        if (requestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(ownerSubject) ||
            scopeKey is not { Length: 32 } ||
            evidenceKey is not { Length: 32 } ||
            pinKey is not { Length: 32 } ||
            CryptographicOperations.FixedTimeEquals(scopeKey, evidenceKey) ||
            CryptographicOperations.FixedTimeEquals(scopeKey, pinKey) ||
            CryptographicOperations.FixedTimeEquals(evidenceKey, pinKey) ||
            !AzureBlobVersionPinService.ValidAzureVersion(
                expectedEvidenceVersionId) ||
            !AzureBlobVersionPinService.ValidAzureVersion(
                expectedPinVersionId) ||
            nowUtc.Offset != TimeSpan.Zero)
            return Uncertain(requestId);

        try
        {
            var key = AzureBlobVersionPinService.PinKey(requestId);
            string? cursor = null;
            var knownCursors = new HashSet<string>(StringComparer.Ordinal);
            var found = false;
            var finished = false;

            for (var i = 0; i < MaxPages; i++)
            {
                ct.ThrowIfCancellationRequested();
                var page = await _history.ListAsync(key, cursor, PageSize, ct);
                if (page?.Entries is not { Count: >= 1 and <= PageSize })
                    return Uncertain(requestId);

                foreach (var entry in page.Entries)
                {
                    // A different version, snapshot, tombstone, foreign
                    // prefix, duplicate, null/unversioned, or a non-current
                    // version is an ambiguity, not a safe retry or absence.
                    if (entry is null ||
                        !string.Equals(entry.ObjectKey, key,
                            StringComparison.Ordinal) ||
                        !AzureBlobVersionPinService.ValidAzureVersion(
                            entry.VersionId) ||
                        !string.Equals(entry.VersionId, expectedPinVersionId,
                            StringComparison.Ordinal) ||
                        entry.Deleted || entry.HasSnapshot ||
                        !entry.IsCurrentVersion || found)
                        return Uncertain(requestId);
                    found = true;
                }

                if (page.NextCursor is null)
                {
                    finished = true;
                    break;
                }
                if (!AzureBlobWormHistoryAudit.ValidCursor(page.NextCursor) ||
                    !knownCursors.Add(page.NextCursor))
                    return Uncertain(requestId);
                cursor = page.NextCursor;
            }

            if (!finished || !found)
                return Uncertain(requestId);

            // Verify the signed reference and original evidence AFTER
            // scanning, using BOTH exact caller-provided VersionIds.
            // Even a positive readback cannot prove catalog completeness.
            var authenticated = await _pinService.VerifyPinnedAsync(
                requestId, tenantId, ownerSubject,
                scopeKey, evidenceKey, pinKey,
                expectedEvidenceVersionId, expectedPinVersionId, nowUtc, ct);
            if (!authenticated.PinObserved ||
                !string.Equals(authenticated.PinVersionId,
                    expectedPinVersionId, StringComparison.Ordinal))
                return Uncertain(requestId);

            return new AzureBlobPinHistoryAuditOutcome(
                requestId, true, expectedPinVersionId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Includes 403, 404, timeout, invalid provider metadata,
            // HMAC/actor mismatch, cancellation, and corrupt history.
            return Uncertain(requestId);
        }
    }

    private static AzureBlobPinHistoryAuditOutcome Uncertain(Guid id) =>
        new(id, false, null);
}
