using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Omnia.Infrastructure;

namespace Omnia.ObjectStore.AzureBlob;

/// <summary>
/// Azure listing is not an independent monotonic authority. The metadata
/// returned by an account may be incomplete or have been rolled back.
/// Never use a successful visible-history audit to authorize replay, DELETE,
/// or the release of a recovered database.
/// </summary>
public sealed record AzureBlobHistoryEntry(
    string ObjectKey, string? VersionId, bool Deleted,
    bool HasSnapshot, bool IsCurrentVersion);

public sealed record AzureBlobHistoryPage(
    IReadOnlyList<AzureBlobHistoryEntry> Entries, string? NextCursor);

public interface IAzureBlobVersionHistory
{
    Task<AzureBlobHistoryPage> ListAsync(
        string objectKey, string? cursor, int pageSize, CancellationToken ct);
}

/// <summary>
/// READ-ONLY Azure.Storage.Blobs adapter. List versions, soft deleted and
/// deleted-with-versions states, but do not infer that the provider returned
/// its entire history (nor that all those objects are WORM protected).
/// </summary>
public sealed class AzureBlobSdkVersionHistory : IAzureBlobVersionHistory
{
    private readonly BlobContainerClient _container;

    public AzureBlobSdkVersionHistory(BlobContainerClient container)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        if (_container.Uri.Scheme != Uri.UriSchemeHttps)
            throw Invalid();
    }

    public async Task<AzureBlobHistoryPage> ListAsync(
        string objectKey, string? cursor, int pageSize, CancellationToken ct)
    {
        if (!AzureBlobWormHistoryAudit.ValidKey(objectKey) ||
            pageSize != 2 ||
            (cursor is not null && !AzureBlobWormHistoryAudit.ValidCursor(cursor)))
            throw Invalid();

        var pageable = _container.GetBlobsAsync(
            BlobTraits.None,
            BlobStates.Version | BlobStates.Deleted |
            BlobStates.DeletedWithVersions | BlobStates.Snapshots,
            prefix: objectKey,
            cancellationToken: ct);

        // Only consume ONE provider page on each call. Audit owns the hard
        // page budget and detects cursor repeats/truncation/foreign keys.
        await foreach (var page in pageable.AsPages(cursor, pageSize)
                           .WithCancellation(ct))
        {
            var entries = page.Values.Select(item =>
                new AzureBlobHistoryEntry(item.Name, item.VersionId,
                    item.Deleted || item.HasVersionsOnly == true,
                    !string.IsNullOrEmpty(item.Snapshot),
                    item.IsLatestVersion == true)).ToArray();
            return new AzureBlobHistoryPage(entries, page.ContinuationToken);
        }

        // A provider returning no pages is NOT proof of an absent attempt.
        throw Invalid();
    }

    private static InvalidDataException Invalid() =>
        new("AZURE_BLOB_HISTORY_UNTRUSTED.");
}

public sealed record AzureBlobHistoryAuditOutcome(
    Guid RequestId, bool SingleVisibleVersionVerified)
{
    public bool Uncertain => !SingleVisibleVersionVerified;
    public bool MayAutoRetry => false;
    public bool MayReleaseRecovery => false;
    public bool ProvesCompleteHistory => false;
    public bool ProvesMonotonicHistory => false;
    public bool ProvesSourceDeleteCommit => false;
}

/// <summary>
/// ADR 0051 read-only bounded scan of the Azure-visible version catalog
/// for one exact, externally provided VersionId. This rejects ambiguous
/// metadata BEFORE the exact-version HMAC/Locked verification.
/// </summary>
public sealed class AzureBlobWormHistoryAudit
{
    private const int PageSize = 2;
    private const int MaxPages = 8;
    private readonly IAzureBlobVersionHistory _history;
    private readonly IImmutableAttemptObjectReader _reader;
    private readonly string _storeIdentity;

    private static readonly Regex KeyPattern = new(
        @"\Aerasure-attempts/v1/[0-9a-f]{32}\.json\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));
    private static readonly Regex VersionPattern = new(
        @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}Z\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public AzureBlobWormHistoryAudit(IAzureBlobVersionHistory history,
        IImmutableAttemptObjectReader reader, string storeIdentity)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        if (string.IsNullOrWhiteSpace(storeIdentity) ||
            storeIdentity.Length > 256 ||
            storeIdentity.Any(char.IsControl))
            throw new InvalidDataException("AZURE_BLOB_HISTORY_STORE_UNTRUSTED.");
        _storeIdentity = storeIdentity;
    }

    public async Task<AzureBlobHistoryAuditOutcome> AuditAsync(
        Guid requestId, string tenantId, string ownerSubject,
        byte[] scopeKey, byte[] evidenceKey, string expectedVersionId,
        DateTimeOffset nowUtc, CancellationToken ct)
    {
        try
        {
            // Authenticated caller must provide the exact version, keys
            // and UTC clock; never discover latest from restorable SQL.
            if (requestId == Guid.Empty ||
                string.IsNullOrWhiteSpace(tenantId) ||
                string.IsNullOrWhiteSpace(ownerSubject) ||
                scopeKey is not { Length: 32 } ||
                evidenceKey is not { Length: 32 } ||
                CryptographicOperations.FixedTimeEquals(scopeKey, evidenceKey) ||
                !ValidVersion(expectedVersionId) ||
                nowUtc.Offset != TimeSpan.Zero)
                return Uncertain(requestId);

            var key = ImmutableAttemptEvidence.ObjectKey(requestId);
            string? cursor = null;
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            var found = false;
            var completed = false;
            for (var pageNumber = 0; pageNumber < MaxPages; pageNumber++)
            {
                var page = await _history.ListAsync(key, cursor, PageSize, ct);
                if (page?.Entries is null ||
                    page.Entries.Count > PageSize)
                    return Uncertain(requestId);

                foreach (var entry in page.Entries)
                {
                    // A root tombstone, foreign prefix or duplicate
                    // version makes historical uniqueness unprovable.
                    if (entry is null ||
                        !string.Equals(entry.ObjectKey, key,
                            StringComparison.Ordinal) ||
                        !ValidVersion(entry.VersionId) ||
                        entry.Deleted || entry.HasSnapshot ||
                        !string.Equals(entry.VersionId, expectedVersionId,
                            StringComparison.Ordinal) ||
                        !entry.IsCurrentVersion || found)
                        return Uncertain(requestId);
                    found = true;
                }

                if (page.NextCursor is null)
                {
                    completed = true;
                    break;
                }

                if (!ValidCursor(page.NextCursor) ||
                    !seenCursors.Add(page.NextCursor))
                    return Uncertain(requestId);
                cursor = page.NextCursor;
            }

            if (!completed || !found)
                return Uncertain(requestId);

            // A positive observation means ONLY one version was visible
            // in this bounded, provider-supplied catalogue. The source
            // may lie, truncate or roll back; no monotonicity follows.
            var result = await ImmutableAttemptEvidence.VerifyAsync(
                _reader, requestId, tenantId, ownerSubject, scopeKey,
                evidenceKey, _storeIdentity, expectedVersionId, nowUtc, ct);
            if (result.VerifiedImmutabilityMode !=
                ImmutableAttemptImmutabilityMode.AzureLocked ||
                result.VerifiedRetainUntilUtc <= nowUtc.AddHours(1))
                return Uncertain(requestId);

            return new AzureBlobHistoryAuditOutcome(requestId, true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Includes provider 403/404, offline, malformed metadata,
            // wrong MAC, cancellation, snapshot/clock validation errors.
            return Uncertain(requestId);
        }
    }

    internal static bool ValidKey(string? key) =>
        key is { Length: 57 } &&
        KeyPattern.IsMatch(key);

    internal static bool ValidCursor(string? cursor) =>
        cursor is { Length: > 0 and <= 1024 } &&
        !cursor.Any(char.IsControl) && !string.IsNullOrWhiteSpace(cursor);

    internal static bool ValidVersion(string? version) =>
        version is { Length: 28 } &&
        VersionPattern.IsMatch(version) &&
        DateTimeOffset.TryParseExact(version,
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _);

    private static AzureBlobHistoryAuditOutcome Uncertain(Guid id) =>
        new(id, false);
}
