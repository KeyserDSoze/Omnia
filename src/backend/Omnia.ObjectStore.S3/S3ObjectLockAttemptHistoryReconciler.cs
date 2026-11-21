using System.Net;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Omnia.Infrastructure;

namespace Omnia.ObjectStore.S3;

/// <summary>
/// Metadata from ListObjectVersions. Neither this list nor an empty list
/// is a tamper-proof, independently pinned monotonic history.
/// </summary>
public sealed record S3AttemptHistoryEntry(
    string Key, string? VersionId, bool IsDeleteMarker);

public sealed record S3AttemptHistoryPage(
    IReadOnlyList<S3AttemptHistoryEntry> Versions,
    bool IsTruncated, string? NextKeyMarker, string? NextVersionIdMarker);

public interface IS3AttemptVersionHistory
{
    Task<S3AttemptHistoryPage> PageAsync(string objectKey,
        string? keyMarker, string? versionMarker, int maxKeys,
        CancellationToken ct);
}

/// <summary>
/// REAL S3 ListObjectVersions adapter (AWS SDK .NET v4). Version listing
/// requires s3:ListBucketVersions. No latest GET, write or DELETE methods.
/// Bucket credentials and physical custody are provided independently.
/// </summary>
public sealed class AwsS3AttemptVersionHistory : IS3AttemptVersionHistory
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;

    public AwsS3AttemptVersionHistory(IAmazonS3 client, string bucket)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(bucket) || bucket.Length > 63 ||
            !Regex.IsMatch(bucket,
                @"\A[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]\z",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new InvalidDataException(
                "S3_ATTEMPT_HISTORY_CONFIG_INVALID.");
        _bucket = bucket;
    }

    public async Task<S3AttemptHistoryPage> PageAsync(string objectKey,
        string? keyMarker, string? versionMarker, int maxKeys,
        CancellationToken ct)
    {
        if (maxKeys is < 1 or > 16)
            throw new InvalidDataException("S3_HISTORY_LIMIT_INVALID.");

        var response = await _client.ListVersionsAsync(
            new ListVersionsRequest
            {
                BucketName = _bucket,
                Prefix = objectKey,
                MaxKeys = maxKeys,
                KeyMarker = keyMarker,
                VersionIdMarker = versionMarker
            }, ct);

        if (response.HttpStatusCode != HttpStatusCode.OK ||
            response.IsTruncated is not bool truncated ||
            !string.Equals(response.Prefix, objectKey,
                StringComparison.Ordinal) ||
            !string.Equals(response.Name, _bucket,
                StringComparison.Ordinal))
            throw new InvalidDataException("S3_HISTORY_PAGE_UNTRUSTED.");

        return new S3AttemptHistoryPage(
            (response.Versions ?? [])
                .Select(v => new S3AttemptHistoryEntry(
                    v.Key, v.VersionId, v.IsDeleteMarker == true))
                .ToArray(),
            truncated, response.NextKeyMarker,
            response.NextVersionIdMarker);
    }
}

public enum S3AttemptHistoryStatus { PreviouslyObserved, Uncertain }

/// <summary>
/// Read-only *positive* observation of an existing authenticated attempt.
/// Never proof that an attempt is absent or that any primary DELETE was
/// committed. This cannot authorize retry or recovery readiness.
/// </summary>
public sealed record S3AttemptHistoryObservation(
    Guid RequestId, S3AttemptHistoryStatus Status, string? VersionId)
{
    public bool PreviouslyObserved =>
        Status == S3AttemptHistoryStatus.PreviouslyObserved;
    public bool Uncertain =>
        Status == S3AttemptHistoryStatus.Uncertain;
    public bool MayAutoRetry => false;
    public bool MayReleaseRecovery => false;
    public bool ProvesSourceDeleteCommit => false;
}

/// <summary>
/// ADR 0047: bounded read-only reconciliation of a potentially committed
/// PUT for which the version ACK was lost. List *all* pages (not latest),
/// reject impossible/ambiguous catalogues, then verify the ONE candidate
/// by exact VersionId, S3 COMPLIANCE retention, HMAC and actor scope.
/// A recovered VersionId is an observation, NOT an externally pinned
/// high-watermark. This class MUST NOT be attached to erasure/replay API.
/// </summary>
public sealed class S3ObjectLockAttemptHistoryReconciler
{
    private const int PageSize = 2;
    private const int MaxPages = 8;
    private const int MaxVersions = PageSize * MaxPages;

    private readonly IS3AttemptVersionHistory _history;
    private readonly IImmutableAttemptObjectReader _reader;
    private readonly string _storeIdentity;

    public S3ObjectLockAttemptHistoryReconciler(
        IS3AttemptVersionHistory history,
        IImmutableAttemptObjectReader reader, string storeIdentity)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        if (string.IsNullOrWhiteSpace(storeIdentity) ||
            storeIdentity.Length > 256 || storeIdentity.Any(char.IsControl))
            throw new InvalidDataException("S3_HISTORY_STORE_UNTRUSTED.");
        _storeIdentity = storeIdentity;
    }

    public async Task<S3AttemptHistoryObservation> ReconcileAsync(
        Guid requestId, string tenantId, string ownerSubject,
        byte[] scopeKey, byte[] evidenceKey, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        // Invalid inputs are not proof of absence. No negative result
        // from a history scan ever permits a fresh claim or retry.
        if (requestId == Guid.Empty || nowUtc.Offset != TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(ownerSubject) ||
            scopeKey is not { Length: 32 } ||
            evidenceKey is not { Length: 32 } ||
            scopeKey.AsSpan().SequenceEqual(evidenceKey))
            return Uncertain(requestId);

        var key = ImmutableAttemptEvidence.ObjectKey(requestId);
        var seenVersions = new HashSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<(string, string)>();
        var candidates = new List<string>(2);
        string? keyMarker = null;
        string? versionMarker = null;
        int total = 0;

        try
        {
            for (var pageIndex = 0; pageIndex < MaxPages; pageIndex++)
            {
                var page = await _history.PageAsync(key, keyMarker,
                    versionMarker, PageSize, ct);
                if (page?.Versions is null ||
                    page.Versions.Count > PageSize ||
                    total + page.Versions.Count > MaxVersions)
                    return Uncertain(requestId);
                total += page.Versions.Count;

                // FULL catalogue scan first. A valid receipt on page 1
                // cannot excuse a second version, corrupted page or
                // cyclic cursor on a later page.
                foreach (var entry in page.Versions)
                {
                    if (entry is null ||
                        !string.Equals(entry.Key, key,
                            StringComparison.Ordinal) ||
                        !ValidVersion(entry.VersionId) ||
                        !seenVersions.Add(entry.VersionId!))
                        return Uncertain(requestId);
                    if (!entry.IsDeleteMarker)
                    {
                        candidates.Add(entry.VersionId!);
                        if (candidates.Count > 1)
                            return Uncertain(requestId);
                    }
                }

                if (!page.IsTruncated)
                {
                    if (candidates.Count != 1)
                        return Uncertain(requestId);

                    var version = candidates[0];
                    var observed = await ImmutableAttemptEvidence.VerifyAsync(
                        _reader, requestId, tenantId, ownerSubject,
                        scopeKey, evidenceKey, _storeIdentity,
                        version, nowUtc, ct);
                    if (!observed.AttemptPreviouslyObserved ||
                        observed.VerifiedImmutabilityMode !=
                            ImmutableAttemptImmutabilityMode.S3Compliance ||
                        !string.Equals(observed.VersionId, version,
                            StringComparison.Ordinal))
                        return Uncertain(requestId);

                    return new S3AttemptHistoryObservation(
                        requestId, S3AttemptHistoryStatus.PreviouslyObserved,
                        version);
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
            // Missing/denied listing, malformed XML, timeout, cancelled
            // readback, 403/404 and policy changes all remain uncertain.
            return Uncertain(requestId);
        }

        // Catalogues larger than the bounded scan are NOT assumed empty
        // or complete. Never silently accept the first page.
        return Uncertain(requestId);
    }

    private static bool ValidVersion(string? value) =>
        value is { Length: > 0 and <= 256 } &&
        !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl) &&
        !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, "latest", StringComparison.OrdinalIgnoreCase);

    private static S3AttemptHistoryObservation Uncertain(Guid requestId) =>
        new(requestId, S3AttemptHistoryStatus.Uncertain, null);
}
