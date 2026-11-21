using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Omnia.Infrastructure;

namespace Omnia.ObjectStore.S3;

/// <summary>
/// READ-ONLY, version-pinned S3 Object Lock adapter (ADR 0044, #145).
/// It queries the actual S3 GetObjectRetention API for the same exact
/// VersionId used by GetObject and only maps COMPLIANCE mode to the
/// provider-neutral positive result. Governance/unlocked/missing/unsupported
/// metadata FAIL CLOSED. No Put/Delete/List methods or credentials are owned
/// by this adapter; the client is injected by a separate trust domain.
/// The supplied StoreIdentity is CONFIGURED, not independently attested.
/// </summary>
public sealed class S3ObjectLockAttemptReader : IImmutableAttemptObjectReader
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _storeIdentity;
    private static readonly Regex ExpectedKey = new(
        @"\Aerasure-attempts/v1/[0-9a-f]{32}\.json\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public S3ObjectLockAttemptReader(
        IAmazonS3 client, string bucket, string storeIdentity)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(bucket) || bucket.Length > 63 ||
            !Regex.IsMatch(bucket,
                @"\A[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]\z",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ||
            string.IsNullOrWhiteSpace(storeIdentity) ||
            storeIdentity.Length > 256 ||
            storeIdentity.Any(char.IsControl))
            throw new InvalidDataException("S3_OBJECT_STORE_CONFIG_INVALID.");
        _bucket = bucket;
        _storeIdentity = storeIdentity;
    }

    public async Task<ImmutableAttemptObjectVersion?> ReadVersionAsync(
        string objectKey, string versionId, CancellationToken ct)
    {
        if (objectKey is null || !ExpectedKey.IsMatch(objectKey) ||
            !ValidVersion(versionId))
            throw Invalid();

        try
        {
            // This always supplies VersionId. NEVER silently get the latest
            // version, and never trust a bucket default policy as a
            // substitute for per-version retention metadata.
            var retention = await _client.GetObjectRetentionAsync(
                new GetObjectRetentionRequest
                {
                    BucketName = _bucket,
                    Key = objectKey,
                    VersionId = versionId
                }, ct);
            if (retention.HttpStatusCode != HttpStatusCode.OK ||
                !string.Equals(retention.Retention?.Mode?.Value,
                    "COMPLIANCE", StringComparison.Ordinal) ||
                retention.Retention?.RetainUntilDate is not DateTime until ||
                until.Kind != DateTimeKind.Utc)
                throw Invalid();

            // The same exact pinned version must be returned in the body.
            // Reject a provider that ignores VersionId, returns "null",
            // responds with a delete marker or misreports its content.
            using var response = await _client.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = _bucket,
                    Key = objectKey,
                    VersionId = versionId
                }, ct);
            if (response.HttpStatusCode != HttpStatusCode.OK ||
                !string.Equals(response.VersionId, versionId,
                    StringComparison.Ordinal) ||
                response.ContentLength is <= 0 or > ImmutableAttemptEvidence.MaxBytes)
                throw Invalid();

            // Bound read by 512+1 even if the response lies about its
            // ContentLength or sends an unbounded stream. No temporary file.
            var buffer = new byte[ImmutableAttemptEvidence.MaxBytes + 1];
            try
            {
                int count = 0;
                while (count < buffer.Length)
                {
                    int n = await response.ResponseStream.ReadAsync(
                        buffer.AsMemory(count), ct);
                    if (n == 0) break;
                    count += n;
                }
                if (count <= 0 || count > ImmutableAttemptEvidence.MaxBytes ||
                    count != response.ContentLength)
                    throw Invalid();

                return new ImmutableAttemptObjectVersion(
                    _storeIdentity, objectKey, versionId,
                    ImmutableAttemptImmutabilityMode.S3Compliance,
                    new DateTimeOffset(until),
                    buffer.AsSpan(0, count).ToArray());
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
        catch (AmazonServiceException)
        {
            // Includes 403 without ListBucket, 404, no GetObjectRetention,
            // wrong identity and backend error. All are UNTRUSTED, never
            // treated as "no prior attempt", recovery-ready or retryable.
            throw Invalid();
        }
        catch (IOException)
        {
            throw Invalid();
        }
    }

    private static bool ValidVersion(string? version) =>
        version is { Length: > 0 and <= 256 } &&
        !string.IsNullOrWhiteSpace(version) &&
        !version.Any(char.IsControl) &&
        !version.Equals("null", StringComparison.OrdinalIgnoreCase) &&
        !version.Equals("latest", StringComparison.OrdinalIgnoreCase);

    private static InvalidDataException Invalid() =>
        new("S3_OBJECT_VERSION_UNTRUSTED.");
}
