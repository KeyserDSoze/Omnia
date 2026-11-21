using Omnia.Infrastructure;

namespace Omnia.ObjectStore.S3;

/// <summary>
/// A bounded visibility check for competing S3 pin versions (ADR 0049).
/// ListVersions is mutable provider metadata, NOT a monotonic witness:
/// a clean scan proves neither completeness nor absence of hidden history.
/// </summary>
public sealed record S3PinHistoryAuditResult(
    Guid RequestId, bool SingleVisiblePinVerified)
{
    public bool Uncertain => !SingleVisiblePinVerified;
    public bool MayAutoRetry => false;
    public bool MayReleaseRecovery => false;
    public bool ProvesCompleteHistory => false;
    public bool ProvesSourceDeleteCommit => false;
}

/// <summary>
/// READ-ONLY forensic check of an externally version-pinned WORM pair.
/// A second visible version OR delete marker denies the diagnostic even if
/// the originally pinned immutable object is still verifiable. This is
/// deliberately stricter than S3ObjectLockVersionPinService.VerifyPinnedAsync.
/// No S3 listing, including an apparently complete one, authorizes replay,
/// erasure, retry or release after a multi-database rollback.
/// </summary>
public sealed class S3ObjectLockPinHistoryAudit
{
    private const int PageSize = 2;
    private const int MaxPages = 8;

    private readonly IS3AttemptVersionHistory _history;
    private readonly S3ObjectLockVersionPinService _pinService;

    public S3ObjectLockPinHistoryAudit(
        IS3AttemptVersionHistory history,
        S3ObjectLockVersionPinService pinService)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _pinService = pinService ??
            throw new ArgumentNullException(nameof(pinService));
    }

    public async Task<S3PinHistoryAuditResult> AuditAsync(
        Guid requestId, string tenantId, string ownerSubject,
        byte[] scopeKey, byte[] evidenceKey, byte[] pinKey,
        string expectedEvidenceVersionId, string expectedPinVersionId,
        DateTimeOffset nowUtc, CancellationToken ct)
    {
        // Nothing in this read-only audit can assert a missing prior pin.
        // All caller-provided identifiers must be exact and prevalidated.
        if (requestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(ownerSubject) ||
            nowUtc.Offset != TimeSpan.Zero ||
            scopeKey is not { Length: 32 } ||
            evidenceKey is not { Length: 32 } ||
            pinKey is not { Length: 32 } ||
            scopeKey.AsSpan().SequenceEqual(evidenceKey) ||
            scopeKey.AsSpan().SequenceEqual(pinKey) ||
            evidenceKey.AsSpan().SequenceEqual(pinKey) ||
            !ValidVersion(expectedEvidenceVersionId) ||
            !ValidVersion(expectedPinVersionId))
            return Uncertain(requestId);

        var key = S3ObjectLockVersionPinService.PinKey(requestId);
        var seenVersions = new HashSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<(string, string)>();
        string? keyMarker = null;
        string? versionMarker = null;
        int visibleDataVersions = 0;

        try
        {
            for (var index = 0; index < MaxPages; index++)
            {
                var page = await _history.PageAsync(key, keyMarker,
                    versionMarker, PageSize, ct);
                if (page?.Versions is null ||
                    page.Versions.Count is < 1 or > PageSize)
                    return Uncertain(requestId);

                foreach (var entry in page.Versions)
                {
                    if (entry is null ||
                        !string.Equals(entry.Key, key,
                            StringComparison.Ordinal) ||
                        !ValidVersion(entry.VersionId) ||
                        !seenVersions.Add(entry.VersionId!))
                        return Uncertain(requestId);

                    // A delete marker shows mutable-latest activity.
                    // A second data version shows the key was rewritten.
                    if (entry.IsDeleteMarker ||
                        !string.Equals(entry.VersionId,
                            expectedPinVersionId,
                            StringComparison.Ordinal) ||
                        ++visibleDataVersions > 1)
                        return Uncertain(requestId);
                }

                if (!page.IsTruncated)
                {
                    // A clean snapshot from ONE provider is still only
                    // a diagnostic; exact-version HMAC+WORM readback is
                    // mandatory before reporting a positive observation.
                    if (visibleDataVersions != 1 ||
                        !string.IsNullOrEmpty(page.NextKeyMarker) ||
                        !string.IsNullOrEmpty(page.NextVersionIdMarker))
                        return Uncertain(requestId);

                    var verified = await _pinService.VerifyPinnedAsync(
                        requestId, tenantId, ownerSubject,
                        scopeKey, evidenceKey, pinKey,
                        expectedEvidenceVersionId, expectedPinVersionId,
                        nowUtc, ct);
                    return verified.PinObserved
                        ? new S3PinHistoryAuditResult(requestId, true)
                        : Uncertain(requestId);
                }

                if (page.NextKeyMarker is not { Length: > 0 and <= 1024 } nextKey ||
                    !string.Equals(nextKey, key, StringComparison.Ordinal) ||
                    !ValidVersion(page.NextVersionIdMarker) ||
                    !seenCursors.Add((nextKey, page.NextVersionIdMarker!)))
                    return Uncertain(requestId);

                keyMarker = nextKey;
                versionMarker = page.NextVersionIdMarker;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Denied/truncated/error/cancelled responses are NEVER
            // proof that it is safe to attempt a new DELETE or PUT.
            return Uncertain(requestId);
        }
        return Uncertain(requestId);
    }

    private static bool ValidVersion(string? version) =>
        version is { Length: > 0 and <= 256 } &&
        !string.IsNullOrWhiteSpace(version) &&
        !version.Any(char.IsControl) &&
        !string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(version, "null", StringComparison.OrdinalIgnoreCase);

    private static S3PinHistoryAuditResult Uncertain(Guid requestId) =>
        new(requestId, false);
}
