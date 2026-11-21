using System.Net.Http;
using System.Security.Cryptography;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Omnia.Infrastructure;

namespace Omnia.ObjectStore.AzureBlob;

/// <summary>
/// Write a canonical signed pin to a separate configured Azure container.
/// A configured container URI is NOT independently administered custody.
/// The two-step upload-then-version-lock window is not atomic.
/// </summary>
public sealed class AzureBlobWormPinSdkWriter : IAzureBlobWormPinWrite
{
    private readonly BlobContainerClient _container;

    public AzureBlobWormPinSdkWriter(
        Uri containerUri, BlobClientOptions options,
        TokenCredential? identity = null)
    {
        ArgumentNullException.ThrowIfNull(containerUri);
        ArgumentNullException.ThrowIfNull(options);
        if (!containerUri.IsAbsoluteUri ||
            containerUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(containerUri.Host) ||
            options.Retry.MaxRetries != 0)
            throw Invalid();
        // The exact SDK client used for PUT is built with zero retry.
        _container = identity is null
            ? new BlobContainerClient(containerUri, options)
            : new BlobContainerClient(containerUri, identity, options);
    }

    public async Task<AzureBlobWormWriteAck> UploadAndLockOnceAsync(
        string objectKey, byte[] content,
        DateTimeOffset retention, CancellationToken ct)
    {
        if (!AzureBlobVersionPinService.ValidPinKey(objectKey) ||
            content is not { Length: >= 2 and <= AzureBlobVersionPinService.MaxPinBytes } ||
            retention.Offset != TimeSpan.Zero ||
            retention <= DateTimeOffset.UtcNow.AddHours(1))
            throw Invalid();

        var blob = _container.GetBlobClient(objectKey);
        var uploaded = (await blob.UploadAsync(
            BinaryData.FromBytes(content),
            new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders {
                    ContentType = "application/json"
                },
                // A custom version-level policy on upload is Unlocked.
                // Do not claim immutability until exact version is Locked.
                ImmutabilityPolicy = new BlobImmutabilityPolicy {
                    PolicyMode = BlobImmutabilityPolicyMode.Unlocked,
                    ExpiresOn = retention
                }
            }, ct)).Value;
        var version = uploaded.VersionId;
        if (!AzureBlobVersionPinService.ValidAzureVersion(version))
            throw Invalid();

        // Only the returned ACK VersionId; never a mutable "latest".
        // Lost ACK, 403/409/412, timeout, cancellation or lock failure
        // is UNCERTAIN at the publisher boundary. No retries or deletes.
        await blob.WithVersion(version).SetImmutabilityPolicyAsync(
            new BlobImmutabilityPolicy {
                PolicyMode = BlobImmutabilityPolicyMode.Locked,
                ExpiresOn = retention
            }, cancellationToken: ct);

        return new AzureBlobWormWriteAck(version);
    }

    private static InvalidDataException Invalid() =>
        new("AZURE_WORM_PIN_WRITE_UNTRUSTED.");
}

/// <summary>
/// Strict version-pinned Azure HEAD + GET for SIGNED PIN blobs (max 1024).
/// Unlike the 512-byte evidence reader, this supports the pin envelope,
/// but NEVER permits read of mutable latest or performs any writes.
/// </summary>
public sealed class AzureBlobWormPinReader : IImmutableAttemptObjectReader
{
    private readonly BlobContainerClient _container;
    private readonly string _store;

    public AzureBlobWormPinReader(
        BlobContainerClient container, string storeIdentity)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        if (_container.Uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(storeIdentity) ||
            storeIdentity.Length > 256 || storeIdentity.Any(char.IsControl))
            throw Invalid();
        _store = storeIdentity;
    }

    public async Task<ImmutableAttemptObjectVersion?> ReadVersionAsync(
        string objectKey, string versionId, CancellationToken ct)
    {
        if (!AzureBlobVersionPinService.ValidPinKey(objectKey) ||
            !AzureBlobVersionPinService.ValidAzureVersion(versionId))
            throw Invalid();

        var versioned = _container.GetBlobClient(objectKey)
            .WithVersion(versionId);
        try
        {
            var properties = (await versioned.GetPropertiesAsync(
                cancellationToken: ct)).Value;

            if (!string.Equals(properties.VersionId, versionId,
                    StringComparison.Ordinal) ||
                properties.ImmutabilityPolicy?.PolicyMode !=
                    BlobImmutabilityPolicyMode.Locked ||
                properties.ImmutabilityPolicy.ExpiresOn is not DateTimeOffset expiry ||
                expiry.Offset != TimeSpan.Zero ||
                properties.ContentLength is < 2 or > AzureBlobVersionPinService.MaxPinBytes ||
                properties.ETag.ToString().Length == 0)
                throw Invalid();

            var downloaded = (await versioned.DownloadStreamingAsync(
                cancellationToken: ct)).Value;
            using (downloaded)
            {
                if (!string.Equals(downloaded.Details.VersionId, versionId,
                        StringComparison.Ordinal) ||
                    downloaded.Details.ContentLength != properties.ContentLength ||
                    !downloaded.Details.ETag.Equals(properties.ETag))
                    throw Invalid();

                var buffer = new byte[AzureBlobVersionPinService.MaxPinBytes + 1];
                try
                {
                    var count = 0;
                    while (count < buffer.Length)
                    {
                        var read = await downloaded.Content.ReadAsync(
                            buffer.AsMemory(count), ct);
                        if (read == 0) break;
                        count += read;
                    }
                    if (count != properties.ContentLength ||
                        count > AzureBlobVersionPinService.MaxPinBytes)
                        throw Invalid();

                    return new ImmutableAttemptObjectVersion(
                        _store, objectKey, versionId,
                        ImmutableAttemptImmutabilityMode.AzureLocked,
                        expiry, buffer.AsSpan(0, count).ToArray());
                }
                finally { CryptographicOperations.ZeroMemory(buffer); }
            }
        }
        catch (ArgumentOutOfRangeException) { throw Invalid(); }
        catch (FormatException) { throw Invalid(); }
        catch (InvalidOperationException) { throw Invalid(); }
        catch (RequestFailedException) { throw Invalid(); }
        catch (IOException) { throw Invalid(); }
        catch (HttpRequestException) { throw Invalid(); }
        catch (TimeoutException) { throw Invalid(); }
    }

    private static InvalidDataException Invalid() =>
        new("AZURE_WORM_PIN_READER_UNTRUSTED.");
}
