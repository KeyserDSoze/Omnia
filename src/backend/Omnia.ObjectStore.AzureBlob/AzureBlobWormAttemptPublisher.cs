using System.Globalization;
using System.Security.Cryptography;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Omnia.Infrastructure;

namespace Omnia.ObjectStore.AzureBlob;

/// <summary>
/// An Azure upload can commit before its HTTP ACK is received. An
/// uploaded custom policy may be Unlocked until an explicit version-level
/// lock operation succeeds. Neither transition is a DELETE commit proof.
/// </summary>
public sealed record AzureBlobWormWriteAck(string? VersionId);

/// <summary>
/// This boundary is injectable for failure-after-commit experiments.
/// The caller must never replay an uncertain attempt automatically.
/// </summary>
public interface IAzureBlobWormAttemptWrite
{
    Task<AzureBlobWormWriteAck> UploadAndLockOnceAsync(
        string objectKey, byte[] content, DateTimeOffset retention,
        CancellationToken ct);
}

/// <summary>
/// Azure SDK v12 one-shot conditional PUT + explicit exact-version lock.
/// The SDK client is constructed HERE with a verified zero-retry options
/// object to prevent a hidden retry of a possibly committed upload/lock.
/// The version MUST be read from the PUT ACK; never use mutable latest.
/// </summary>
public sealed class AzureBlobWormAttemptSdkWriter : IAzureBlobWormAttemptWrite
{
    private readonly BlobContainerClient _container;
    private static readonly System.Text.RegularExpressions.Regex VersionPattern =
        new(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}Z\z",
            System.Text.RegularExpressions.RegexOptions.Compiled |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

    public AzureBlobWormAttemptSdkWriter(
        Uri containerUri, BlobClientOptions options, TokenCredential? identity = null)
    {
        ArgumentNullException.ThrowIfNull(containerUri);
        ArgumentNullException.ThrowIfNull(options);
        if (!containerUri.IsAbsoluteUri ||
            containerUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(containerUri.Host) ||
            options.Retry.MaxRetries != 0)
            throw new InvalidDataException("AZURE_WORM_ONE_SHOT_CONFIG_UNTRUSTED.");

        // Keep the same options attached to the exact SDK client in use;
        // passing separate client + options would NOT prove no hidden retries.
        _container = identity is null
            ? new BlobContainerClient(containerUri, options)
            : new BlobContainerClient(containerUri, identity, options);
    }

    public async Task<AzureBlobWormWriteAck> UploadAndLockOnceAsync(
        string objectKey, byte[] content, DateTimeOffset retention,
        CancellationToken ct)
    {
        if (!ValidKey(objectKey) ||
            content is not { Length: >= 2 and <= ImmutableAttemptEvidence.MaxBytes } ||
            retention.Offset != TimeSpan.Zero ||
            retention <= DateTimeOffset.UtcNow.AddHours(1))
            throw new InvalidDataException("AZURE_WORM_WRITE_INPUT_UNTRUSTED.");

        var blob = _container.GetBlobClient(objectKey);
        // First operation is a single If-None-Match PUT with a custom
        // Unlocked policy: per Microsoft docs, a custom upload does not
        // necessarily inherit a Locked container policy. Never claim
        // WORM yet, including if the upload returns a VersionId.
        var uploaded = (await blob.UploadAsync(
            BinaryData.FromBytes(content),
            new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions
                {
                    IfNoneMatch = ETag.All
                },
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = "application/json"
                },
                ImmutabilityPolicy = new BlobImmutabilityPolicy
                {
                    PolicyMode = BlobImmutabilityPolicyMode.Unlocked,
                    ExpiresOn = retention
                }
            }, ct)).Value;
        var version = uploaded.VersionId;
        if (!ValidVersion(version))
            throw new InvalidDataException("AZURE_WORM_UPLOAD_VERSION_UNTRUSTED.");

        // Second operation pins EXACTLY the returned version, never latest.
        // A crash, timeout or 403 leaves an uncertain, potentially
        // unprotected version. No internal retry, delete or compensation.
        await blob.WithVersion(version).SetImmutabilityPolicyAsync(
            new BlobImmutabilityPolicy
            {
                PolicyMode = BlobImmutabilityPolicyMode.Locked,
                ExpiresOn = retention
            }, cancellationToken: ct);

        return new AzureBlobWormWriteAck(version);
    }

    private static bool ValidKey(string? key) =>
        key is { Length: 57 } &&
        System.Text.RegularExpressions.Regex.IsMatch(key,
            @"\Aerasure-attempts/v1/[0-9a-f]{32}\.json\z",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

    private static bool ValidVersion(string? value) =>
        value is { Length: 28 } &&
        VersionPattern.IsMatch(value) &&
        DateTimeOffset.TryParseExact(value,
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _);
}

/// <summary>
/// This positively observes an Azure LOCKED previous attempt, not a
/// source PostgreSQL DELETE, monotonic pin, or independently held version.
/// All failures after the first PUT remain UNCERTAIN, never safe to retry.
/// </summary>
public sealed record AzureWormAttemptOutcome(Guid RequestId, bool Observed,
    string? VersionId)
{
    public bool Uncertain => !Observed;
    public bool MayAutoRetry => false;
    public bool MayReleaseRecovery => false;
    public bool ProvesSourceDeleteCommit => false;
    public bool ProvesMonotonicHistory => false;
}

public sealed class AzureBlobWormAttemptPublisher
{
    private readonly IAzureBlobWormAttemptWrite _writer;
    private readonly IImmutableAttemptObjectReader _reader;
    private readonly string _storeIdentity;

    public AzureBlobWormAttemptPublisher(
        IAzureBlobWormAttemptWrite writer,
        IImmutableAttemptObjectReader reader, string storeIdentity)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        if (string.IsNullOrWhiteSpace(storeIdentity) ||
            storeIdentity.Length > 256 ||
            storeIdentity.Any(char.IsControl))
            throw new InvalidDataException("AZURE_WORM_STORE_UNTRUSTED.");
        _storeIdentity = storeIdentity;
    }

    public async Task<AzureWormAttemptOutcome> SubmitOnceAsync(
        Guid requestId, string tenantId, string ownerSubject,
        byte[] scopeKey, byte[] evidenceKey, byte[] content,
        DateTimeOffset retention, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        if (nowUtc.Offset != TimeSpan.Zero ||
            retention.Offset != TimeSpan.Zero ||
            retention <= nowUtc.AddHours(1) ||
            retention <= DateTimeOffset.UtcNow.AddHours(1))
            throw new InvalidDataException("AZURE_WORM_RETENTION_UNTRUSTED.");

        // A fake caller cannot upload a receipt for another actor.
        // Reject BEFORE any SDK/network call, with domain-separated HMACs.
        ImmutableAttemptEvidence.VerifyPayload(content,
            requestId, tenantId, ownerSubject, scopeKey, evidenceKey);

        try
        {
            var ack = await _writer.UploadAndLockOnceAsync(
                ImmutableAttemptEvidence.ObjectKey(requestId),
                content, retention, ct);
            if (ack?.VersionId is not { Length: > 0 } version)
                return Uncertain(requestId);

            // This reader must check effective Locked policy, exact
            // VersionId in HEAD+GET, matching ETag, body and HMAC.
            var observed = await ImmutableAttemptEvidence.VerifyAsync(
                _reader, requestId, tenantId, ownerSubject, scopeKey,
                evidenceKey, _storeIdentity, version, nowUtc, ct);
            if (!observed.AttemptPreviouslyObserved ||
                observed.VerifiedImmutabilityMode !=
                    ImmutableAttemptImmutabilityMode.AzureLocked ||
                observed.VerifiedRetainUntilUtc < retention.AddSeconds(-1) ||
                !string.Equals(observed.VersionId, version,
                    StringComparison.Ordinal))
                return Uncertain(requestId);

            return new AzureWormAttemptOutcome(requestId, true, version);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Missing/denied lock, conflicting upload, lost ACK and
            // corrupted readbacks are NOT safe to retry automatically.
            return Uncertain(requestId);
        }
    }

    private static AzureWormAttemptOutcome Uncertain(Guid id) =>
        new(id, false, null);
}
