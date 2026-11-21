using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Omnia.Infrastructure;

namespace Omnia.ObjectStore.AzureBlob;

/// <summary>
/// Azure SDK read-only adapter for exactly one externally pinned blob version.
/// Requires the Blob VERSION's own Locked immutability policy, not merely
/// versioning or container eligibility. Does not create policies, versions,
/// storage accounts, vault keys or release tokens. Azure REAL WORM/RBAC,
/// independent trust/clock and source DELETE commit are NOT proven by
/// synthetic HTTP contract tests.
/// </summary>
public sealed class AzureBlobWormAttemptReader : IImmutableAttemptObjectReader
{
    private readonly BlobContainerClient _container;
    private readonly string _storeIdentity;
    private static readonly Regex KeyPattern = new(
        @"\Aerasure-attempts/v1/[0-9a-f]{32}\.json\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));
    private static readonly Regex VersionPattern = new(
        @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}Z\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public AzureBlobWormAttemptReader(
        BlobContainerClient container, string storeIdentity)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        if (string.IsNullOrWhiteSpace(storeIdentity) ||
            storeIdentity.Length > 256 ||
            storeIdentity.Any(char.IsControl) ||
            !string.Equals(_container.Uri.Scheme, Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
            throw Invalid();

        _storeIdentity = storeIdentity;
    }

    public async Task<ImmutableAttemptObjectVersion?> ReadVersionAsync(
        string objectKey, string versionId, CancellationToken ct)
    {
        if (objectKey is null || !KeyPattern.IsMatch(objectKey) ||
            !ValidVersion(versionId))
            throw Invalid();

        // Crucial: both HTTP HEAD and GET use the SAME version-scoped client.
        // WithVersion(null/empty) would silently select mutable latest.
        var versioned = _container.GetBlobClient(objectKey)
            .WithVersion(versionId);

        try
        {
            var properties = (await versioned.GetPropertiesAsync(
                cancellationToken: ct)).Value;
            var policy = properties.ImmutabilityPolicy;
            if (!string.Equals(properties.VersionId, versionId,
                    StringComparison.Ordinal) ||
                policy?.PolicyMode != BlobImmutabilityPolicyMode.Locked ||
                policy.ExpiresOn is not DateTimeOffset retainUntil ||
                retainUntil.Offset != TimeSpan.Zero ||
                properties.ContentLength is < 2 or > ImmutableAttemptEvidence.MaxBytes ||
                properties.ETag.ToString().Length == 0)
                throw Invalid();

            // Same exact immutable version and same ETag must be observed
            // in both responses. Do not accept a proxy that omits version
            // metadata, silently downgrades to latest or swaps content.
            var downloaded = (await versioned.DownloadStreamingAsync(
                cancellationToken: ct)).Value;
            using (downloaded)
            {
                if (!string.Equals(downloaded.Details.VersionId, versionId,
                        StringComparison.Ordinal) ||
                    downloaded.Details.ContentLength != properties.ContentLength ||
                    !downloaded.Details.ETag.Equals(properties.ETag))
                    throw Invalid();

                var bytes = new byte[ImmutableAttemptEvidence.MaxBytes + 1];
                try
                {
                    int count = 0;
                    while (count < bytes.Length)
                    {
                        var read = await downloaded.Content.ReadAsync(
                            bytes.AsMemory(count), ct);
                        if (read == 0) break;
                        count += read;
                    }

                    if (count != properties.ContentLength ||
                        count > ImmutableAttemptEvidence.MaxBytes)
                        throw Invalid();

                    return new ImmutableAttemptObjectVersion(
                        _storeIdentity, objectKey, versionId,
                        ImmutableAttemptImmutabilityMode.AzureLocked,
                        retainUntil, bytes.AsSpan(0, count).ToArray());
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        }
        // The Azure SDK throws for unknown serialized enum values before
        // it can return BlobProperties. Never leak a deserialization error
        // as a trustworthy observation or downgrade to an unlocked policy.
        catch (ArgumentOutOfRangeException) { throw Invalid(); }
        catch (FormatException) { throw Invalid(); }
        catch (InvalidOperationException) { throw Invalid(); }
        catch (RequestFailedException)
        {
            // Includes 403 (insufficient RBAC), 404 (missing/deleted
            // version), invalid policy and backend failures. Never
            // downgrade to latest or interpret it as 'no attempt'.
            throw Invalid();
        }
        catch (IOException) { throw Invalid(); }
        catch (HttpRequestException) { throw Invalid(); }
        catch (TimeoutException) { throw Invalid(); }
    }

    private static bool ValidVersion(string? version) =>
        version is { Length: 28 } &&
        VersionPattern.IsMatch(version) &&
        DateTimeOffset.TryParseExact(
            version, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal |
            DateTimeStyles.AdjustToUniversal, out _);

    private static InvalidDataException Invalid() =>
        new("AZURE_BLOB_VERSION_UNTRUSTED.");
}
