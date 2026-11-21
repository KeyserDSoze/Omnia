using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Omnia.Infrastructure;

namespace Omnia.ObjectStore.S3;

/// <summary>
/// Read-only EXACT pinned S3 COMPLIANCE version for signed pin envelopes.
/// Pin namespace is separate from attempt receipt namespace; no latest
/// GET, no LIST/PUT/DELETE and no recovery authorization.
/// The configured store identity does not attest bucket ownership.
/// </summary>
public sealed class S3ObjectLockPinReader : IImmutableAttemptObjectReader
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _storeIdentity;
    private static readonly Regex KeyPattern = new(
        @"\Aerasure-pins/v1/[0-9a-f]{32}\.json\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public S3ObjectLockPinReader(
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
            throw Invalid();
        _bucket = bucket;
        _storeIdentity = storeIdentity;
    }

    public async Task<ImmutableAttemptObjectVersion?> ReadVersionAsync(
        string objectKey, string versionId, CancellationToken ct)
    {
        if (objectKey is null || !KeyPattern.IsMatch(objectKey) ||
            !ValidVersion(versionId))
            throw Invalid();

        try
        {
            var retention = await _client.GetObjectRetentionAsync(
                new GetObjectRetentionRequest
                {
                    BucketName = _bucket, Key = objectKey,
                    VersionId = versionId
                }, ct);
            if (retention.HttpStatusCode != HttpStatusCode.OK ||
                !string.Equals(retention.Retention?.Mode?.Value,
                    "COMPLIANCE", StringComparison.Ordinal) ||
                retention.Retention?.RetainUntilDate is not DateTime until ||
                until.Kind != DateTimeKind.Utc)
                throw Invalid();

            using var response = await _client.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = _bucket, Key = objectKey,
                    VersionId = versionId
                }, ct);
            if (response.HttpStatusCode != HttpStatusCode.OK ||
                !string.Equals(response.VersionId, versionId,
                    StringComparison.Ordinal) ||
                response.ContentLength is <= 0 or >
                    S3ObjectLockVersionPinService.MaxPinBytes)
                throw Invalid();

            // Check the transfer size independently of ContentLength,
            // which could be untrusted or supplied by an erroneous proxy.
            var buffer = new byte[S3ObjectLockVersionPinService.MaxPinBytes + 1];
            try
            {
                var length = 0;
                while (length < buffer.Length)
                {
                    var read = await response.ResponseStream.ReadAsync(
                        buffer.AsMemory(length), ct);
                    if (read == 0) break;
                    length += read;
                }

                if (length is <= 0 or >
                        S3ObjectLockVersionPinService.MaxPinBytes ||
                    length != response.ContentLength)
                    throw Invalid();

                return new ImmutableAttemptObjectVersion(
                    _storeIdentity, objectKey, versionId,
                    ImmutableAttemptImmutabilityMode.S3Compliance,
                    new DateTimeOffset(until),
                    buffer.AsSpan(0, length).ToArray());
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
        catch (AmazonServiceException) { throw Invalid(); }
        catch (IOException) { throw Invalid(); }
        catch (TimeoutException) { throw Invalid(); }
    }

    private static bool ValidVersion(string? version) =>
        version is { Length: > 0 and <= 256 } &&
        !string.IsNullOrWhiteSpace(version) &&
        !version.Any(char.IsControl) &&
        !version.Equals("null", StringComparison.OrdinalIgnoreCase) &&
        !version.Equals("latest", StringComparison.OrdinalIgnoreCase);

    private static InvalidDataException Invalid() =>
        new("S3_PIN_VERSION_UNTRUSTED.");
}
