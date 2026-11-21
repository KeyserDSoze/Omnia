using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using Omnia.Infrastructure;

namespace Omnia.ObjectStore.S3;

/// <summary>
/// A single S3 conditional-put response. Its VersionId is UNTRUSTED
/// until the read-only, pinned version verifier checks bytes+retention.
/// </summary>
public sealed record S3ObjectLockPutAcknowledgment(string? VersionId);

/// <summary>
/// A provider-independent single-call boundary, permitting deterministic
/// timeout/response-loss fault injection without making a second PUT.
/// The implementation is responsible for a conditional COMPLIANCE PUT.
/// </summary>
public interface IS3ObjectLockAttemptPut
{
    Task<S3ObjectLockPutAcknowledgment> PutOnceAsync(
        string objectKey, byte[] signedContent,
        DateTimeOffset retainUntilUtc, CancellationToken ct);
}

/// <summary>
/// Actual AWS SDK v4 COMPLIANCE PUT adapter (ADR 0046 / #147).
/// Requires an explicitly no-retry client. Never receives vault secrets,
/// tenant-scoped DB credentials, post-commit proof or recovery authority.
/// </summary>
public sealed class AwsS3ObjectLockAttemptPut : IS3ObjectLockAttemptPut
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;

    public AwsS3ObjectLockAttemptPut(AmazonS3Client client, string bucket)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (client.Config.MaxErrorRetry != 0 ||
            string.IsNullOrWhiteSpace(bucket) ||
            bucket.Length > 63 ||
            !Regex.IsMatch(bucket,
                @"\A[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]\z",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new InvalidDataException(
                "S3_OBJECT_LOCK_ONE_SHOT_CONFIG_UNTRUSTED.");
        _bucket = bucket;
    }

    public async Task<S3ObjectLockPutAcknowledgment> PutOnceAsync(
        string objectKey, byte[] signedContent,
        DateTimeOffset retainUntilUtc, CancellationToken ct)
    {
        using var data = new MemoryStream(signedContent, writable: false);
        // MD5 is only the S3 Content-MD5 transfer integrity header,
        // NOT the HMAC signature (which is SHA256/domain-separated).
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = objectKey,
            InputStream = data,
            ContentType = "application/json",
            MD5Digest = Convert.ToBase64String(MD5.HashData(signedContent)),
            IfNoneMatch = "*",
            ObjectLockMode = ObjectLockMode.Compliance,
            ObjectLockRetainUntilDate = retainUntilUtc.UtcDateTime
        };

        // Exactly ONE SDK invocation. The injected client must have
        // MaxErrorRetry=0 or SDK-hidden retries could duplicate a PUT.
        var response = await _client.PutObjectAsync(request, ct);
        if (response.HttpStatusCode != HttpStatusCode.OK)
            throw new InvalidDataException("S3_PUT_ACK_UNTRUSTED.");
        return new S3ObjectLockPutAcknowledgment(response.VersionId);
    }
}

/// <summary>
/// This answer to a write is NON-AUTHORIZING even when the version was
/// observed. Unknown ACK, conflict, no VersionId or failed readback is
/// UNCERTAIN: NEVER auto-retry, re-delete, or issue a recovery-ready token.
/// </summary>
public enum S3AttemptSubmissionStatus { Observed, Uncertain }

public sealed record S3AttemptSubmissionOutcome(
    Guid RequestId, S3AttemptSubmissionStatus Status, string? VersionId)
{
    public bool Observed => Status == S3AttemptSubmissionStatus.Observed;
    public bool Uncertain => Status == S3AttemptSubmissionStatus.Uncertain;
    public bool MayAutoRetry => false;
    public bool MayReleaseRecovery => false;
    public bool ProvesSourceDeleteCommit => false;
}

/// <summary>
/// Strict one-pass (NO READ-LATEST, no resubmission) attempt evidence
/// publisher. Validates signed payload before PUT; checks returned
/// version with pinned S3 COMPLIANCE reader after ACK. The returned
/// evidence is *only a previous attempt observation*, not proof of
/// primary DELETE commit or a durable offsite version pin.
/// </summary>
public sealed class S3ObjectLockAttemptPublisher
{
    private readonly IS3ObjectLockAttemptPut _writer;
    private readonly IImmutableAttemptObjectReader _reader;
    private readonly string _storeIdentity;

    public S3ObjectLockAttemptPublisher(
        IS3ObjectLockAttemptPut writer,
        IImmutableAttemptObjectReader reader,
        string storeIdentity)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        if (string.IsNullOrWhiteSpace(storeIdentity) ||
            storeIdentity.Length > 256 ||
            storeIdentity.Any(char.IsControl))
            throw new InvalidDataException("S3_SUBMISSION_STORE_UNTRUSTED.");
        _storeIdentity = storeIdentity;
    }

    public async Task<S3AttemptSubmissionOutcome> SubmitOnceAsync(
        Guid requestId, string tenantId, string ownerSubject,
        byte[] scopeKey, byte[] evidenceKey, byte[] content,
        DateTimeOffset retainUntilUtc, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        // All preconditions happen BEFORE any possible PUT. Do not
        // confuse content validity with authority to delete an actor.
        if (nowUtc.Offset != TimeSpan.Zero ||
            retainUntilUtc.Offset != TimeSpan.Zero ||
            retainUntilUtc <= nowUtc.AddHours(1))
            throw new InvalidDataException("S3_RETENTION_UNTRUSTED.");
        ImmutableAttemptEvidence.VerifyPayload(content, requestId,
            tenantId, ownerSubject, scopeKey, evidenceKey);

        var key = ImmutableAttemptEvidence.ObjectKey(requestId);
        try
        {
            var ack = await _writer.PutOnceAsync(
                key, content, retainUntilUtc, ct);
            if (ack?.VersionId is not { Length: > 0 and <= 256 } version ||
                string.Equals(version, "null",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(version, "latest",
                    StringComparison.OrdinalIgnoreCase))
                return Uncertain(requestId);

            var seen = await ImmutableAttemptEvidence.VerifyAsync(
                _reader, requestId, tenantId, ownerSubject, scopeKey,
                evidenceKey, _storeIdentity, version, nowUtc, ct);

            // A provider returning a believable, still-locked object
            // with a DIFFERENT WORM mode or materially shorter retention
            // did not honor our requested S3 COMPLIANCE PUT. S3 timestamps
            // can be rounded to whole seconds in some compatible servers;
            // allow <1s truncation, never a shortened retention period.
            if (!seen.AttemptPreviouslyObserved ||
                seen.VerifiedImmutabilityMode !=
                    ImmutableAttemptImmutabilityMode.S3Compliance ||
                seen.VerifiedRetainUntilUtc <
                    retainUntilUtc.AddSeconds(-1) ||
                !string.Equals(seen.VersionId, version,
                    StringComparison.Ordinal))
                return Uncertain(requestId);

            return new S3AttemptSubmissionOutcome(requestId,
                S3AttemptSubmissionStatus.Observed, version);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Network/SDK/403/409/412/cancellation/readback/HMAC all
            // fail closed. A timed-out PUT MAY HAVE COMMITTED, even if
            // no ACK was delivered. NEVER RETRY AUTOMATICALLY.
            return Uncertain(requestId);
        }
    }

    private static S3AttemptSubmissionOutcome Uncertain(Guid requestId) =>
        new(requestId, S3AttemptSubmissionStatus.Uncertain, null);
}
