using System.Security.Cryptography;
using System.Text.Json;
using Omnia.Infrastructure;

namespace Omnia.ObjectStore.S3;

/// <summary>
/// A positive observation of TWO explicitly version-pinned S3 COMPLIANCE
/// objects. It is not a global monotonic checkpoint or an erasure commit.
/// The two expected version IDs MUST be held by an independently managed
/// control plane; the implementation cannot create that authority.
/// </summary>
public sealed record S3AttemptVersionPinOutcome(
    Guid RequestId, bool PinObserved, string? EvidenceVersionId,
    string? PinVersionId)
{
    public bool Uncertain => !PinObserved;
    public bool MayAutoRetry => false;
    public bool MayReleaseRecovery => false;
    public bool ProvesSourceDeleteCommit => false;
    public bool ProvesMonotonicHistory => false;
}

/// <summary>
/// ADR 0048: HMAC-authenticated exact evidence VersionId in a separate
/// S3 COMPLIANCE object, written with conditional If-None-Match and
/// confirmed through a read-only version-pinned provider adapter.
/// Production needs *independent* storage administration and an
/// independent source of BOTH version pins; CI has neither.
/// </summary>
public sealed class S3ObjectLockVersionPinService
{
    public const int MaxPinBytes = 1024;
    private const string Purpose = "omnia-s3-attempt-version-pin-v1";
    private readonly IImmutableAttemptObjectReader _evidenceReader;
    private readonly IImmutableAttemptObjectReader _pinReader;
    private readonly IS3ObjectLockAttemptPut _pinWriter;
    private readonly string _evidenceStoreIdentity;
    private readonly string _pinStoreIdentity;

    public S3ObjectLockVersionPinService(
        IImmutableAttemptObjectReader evidenceReader,
        IImmutableAttemptObjectReader pinReader,
        IS3ObjectLockAttemptPut pinWriter,
        string evidenceStoreIdentity, string pinStoreIdentity)
    {
        _evidenceReader = evidenceReader ??
            throw new ArgumentNullException(nameof(evidenceReader));
        _pinReader = pinReader ??
            throw new ArgumentNullException(nameof(pinReader));
        _pinWriter = pinWriter ??
            throw new ArgumentNullException(nameof(pinWriter));
        if (!ValidToken(evidenceStoreIdentity, 256) ||
            !ValidToken(pinStoreIdentity, 256) ||
            string.Equals(evidenceStoreIdentity, pinStoreIdentity,
                StringComparison.Ordinal))
            throw Invalid();
        _evidenceStoreIdentity = evidenceStoreIdentity;
        _pinStoreIdentity = pinStoreIdentity;
    }

    public static string PinKey(Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw Invalid();
        return $"erasure-pins/v1/{requestId:N}.json";
    }

    /// <summary>
    /// Deterministic synthetic fixture helper ONLY. It does not upload
    /// evidence or certify a trusted external version authority.
    /// The same canonical serializer is used by the one-shot service.
    /// </summary>
    public static byte[] CreateSignedSynthetic(Guid requestId,
        string tenantId, string ownerSubject, byte[] scopeKey,
        byte[] evidenceKey, byte[] pinKey,
        string evidenceStoreIdentity, string evidenceVersionId) =>
        BuildSigned(requestId, tenantId, ownerSubject, scopeKey,
            evidenceKey, pinKey, evidenceStoreIdentity, evidenceVersionId);

    /// <summary>
    /// At most one conditional COMPLIANCE pin PUT. The original evidence
    /// must already pass the strict HMAC and exact-version verification.
    /// ANY failure after starting the PUT is uncertain and NEVER retried.
    /// </summary>
    public async Task<S3AttemptVersionPinOutcome> PublishOnceAsync(
        Guid requestId, string tenantId, string ownerSubject,
        byte[] scopeKey, byte[] evidenceKey, byte[] pinKey,
        string evidenceVersionId, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        // Validate all potential programmer-controlled parameters before
        // using the PUT-capable client. A malformed call must not write.
        ValidateInputs(requestId, tenantId, ownerSubject,
            scopeKey, evidenceKey, pinKey, evidenceVersionId, nowUtc);

        try
        {
            var evidence = await ImmutableAttemptEvidence.VerifyAsync(
                _evidenceReader, requestId, tenantId, ownerSubject,
                scopeKey, evidenceKey, _evidenceStoreIdentity,
                evidenceVersionId, nowUtc, ct);
            if (evidence.VerifiedImmutabilityMode !=
                    ImmutableAttemptImmutabilityMode.S3Compliance ||
                evidence.VerifiedRetainUntilUtc <= nowUtc.AddHours(1))
                return Uncertain(requestId);

            var signed = BuildSigned(requestId, tenantId, ownerSubject,
                scopeKey, evidenceKey, pinKey, _evidenceStoreIdentity,
                evidenceVersionId);
            try
            {
                // AWS SDK must be configured with MaxErrorRetry=0 by
                // AwsS3ObjectLockAttemptPut. Same-key conditional PUT
                // is NOT an independently monotonic witness.
                var ack = await _pinWriter.PutOnceAsync(PinKey(requestId),
                    signed, evidence.VerifiedRetainUntilUtc, ct);
                if (!ValidToken(ack?.VersionId, 256) ||
                    IsUnsafeVersion(ack!.VersionId!))
                    return Uncertain(requestId);
                return await VerifyPinnedAsync(requestId,
                    tenantId, ownerSubject, scopeKey, evidenceKey,
                    pinKey, evidenceVersionId, ack.VersionId!,
                    nowUtc, ct);
            }
            finally { CryptographicOperations.ZeroMemory(signed); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Lost ACK may follow a successful remote COMMIT. Never
            // map it to "nothing written" or a retry authorization.
            return Uncertain(requestId);
        }
    }

    /// <summary>
    /// Positive observation requires both externally provided exact
    /// version IDs. Neither can be discovered via mutable latest.
    /// No deletion, replay or recovery-ready action is exposed.
    /// </summary>
    public async Task<S3AttemptVersionPinOutcome> VerifyPinnedAsync(
        Guid requestId, string tenantId, string ownerSubject,
        byte[] scopeKey, byte[] evidenceKey, byte[] pinKey,
        string expectedEvidenceVersionId, string expectedPinVersionId,
        DateTimeOffset nowUtc, CancellationToken ct)
    {
        try
        {
            ValidateInputs(requestId, tenantId, ownerSubject,
                scopeKey, evidenceKey, pinKey,
                expectedEvidenceVersionId, nowUtc);
            if (!ValidToken(expectedPinVersionId, 256) ||
                IsUnsafeVersion(expectedPinVersionId))
                return Uncertain(requestId);

            // Verify the pin BEFORE using it as a trusted reference to
            // the original evidence, never a floating/latest pin.
            var pin = await _pinReader.ReadVersionAsync(
                PinKey(requestId), expectedPinVersionId, ct);
            if (pin is null ||
                !string.Equals(pin.StoreIdentity, _pinStoreIdentity,
                    StringComparison.Ordinal) ||
                !string.Equals(pin.ObjectKey, PinKey(requestId),
                    StringComparison.Ordinal) ||
                !string.Equals(pin.VersionId, expectedPinVersionId,
                    StringComparison.Ordinal) ||
                pin.ImmutabilityMode !=
                    ImmutableAttemptImmutabilityMode.S3Compliance ||
                pin.RetainUntilUtc.Offset != TimeSpan.Zero ||
                pin.RetainUntilUtc <= nowUtc ||
                pin.Content is not { Length: >= 2 and <= MaxPinBytes })
                return Uncertain(requestId);

            var expected = BuildSigned(requestId, tenantId,
                ownerSubject, scopeKey, evidenceKey, pinKey,
                _evidenceStoreIdentity, expectedEvidenceVersionId);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(
                    pin.Content, expected))
                    return Uncertain(requestId);
            }
            finally { CryptographicOperations.ZeroMemory(expected); }

            var receipt = await ImmutableAttemptEvidence.VerifyAsync(
                _evidenceReader, requestId, tenantId, ownerSubject,
                scopeKey, evidenceKey, _evidenceStoreIdentity,
                expectedEvidenceVersionId, nowUtc, ct);

            if (receipt.VerifiedImmutabilityMode !=
                    ImmutableAttemptImmutabilityMode.S3Compliance ||
                // The pin must not expire materially BEFORE the
                // evidence version to which it points.
                pin.RetainUntilUtc < receipt.VerifiedRetainUntilUtc.AddSeconds(-1))
                return Uncertain(requestId);

            return new S3AttemptVersionPinOutcome(requestId, true,
                expectedEvidenceVersionId, expectedPinVersionId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Uncertain(requestId);
        }
    }

    private static byte[] BuildSigned(Guid requestId,
        string tenantId, string ownerSubject, byte[] scopeKey,
        byte[] evidenceKey, byte[] pinKey,
        string evidenceStoreIdentity, string evidenceVersionId)
    {
        ValidateKeys(scopeKey, evidenceKey, pinKey);
        PinKey(requestId);
        if (!ValidToken(evidenceStoreIdentity, 256) ||
            !ValidToken(evidenceVersionId, 256) ||
            IsUnsafeVersion(evidenceVersionId) ||
            string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(ownerSubject))
            throw Invalid();

        var scope = PreEraseExecutionClaimScope.Compute(requestId,
            tenantId, ownerSubject, scopeKey);
        try
        {
            var scopeMac = Convert.ToBase64String(scope);
            var unsigned = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                purpose = Purpose,
                requestId = requestId.ToString("N"),
                evidenceStoreIdentity,
                evidenceVersionId,
                scopeMac
            });
            try
            {
                var tag = HMACSHA256.HashData(pinKey, unsigned);
                try
                {
                    var signed = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        schemaVersion = 1,
                        purpose = Purpose,
                        requestId = requestId.ToString("N"),
                        evidenceStoreIdentity,
                        evidenceVersionId,
                        scopeMac,
                        mac = Convert.ToBase64String(tag)
                    });
                    if (signed.Length > MaxPinBytes)
                    {
                        CryptographicOperations.ZeroMemory(signed);
                        throw Invalid();
                    }
                    return signed;
                }
                finally { CryptographicOperations.ZeroMemory(tag); }
            }
            finally { CryptographicOperations.ZeroMemory(unsigned); }
        }
        finally { CryptographicOperations.ZeroMemory(scope); }
    }

    private static void ValidateInputs(Guid requestId,
        string tenantId, string ownerSubject, byte[] scopeKey,
        byte[] evidenceKey, byte[] pinKey,
        string evidenceVersionId, DateTimeOffset nowUtc)
    {
        PinKey(requestId);
        ValidateKeys(scopeKey, evidenceKey, pinKey);
        if (nowUtc.Offset != TimeSpan.Zero ||
            !ValidToken(evidenceVersionId, 256) ||
            IsUnsafeVersion(evidenceVersionId) ||
            string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(ownerSubject))
            throw Invalid();
    }

    private static void ValidateKeys(byte[] scopeKey,
        byte[] evidenceKey, byte[] pinKey)
    {
        if (scopeKey is not { Length: 32 } ||
            evidenceKey is not { Length: 32 } ||
            pinKey is not { Length: 32 } ||
            scopeKey.AsSpan().SequenceEqual(evidenceKey) ||
            scopeKey.AsSpan().SequenceEqual(pinKey) ||
            evidenceKey.AsSpan().SequenceEqual(pinKey))
            throw Invalid();
    }

    private static bool ValidToken(string? value, int max) =>
        value is { Length: > 0 } &&
        value.Length <= max &&
        !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl);

    private static bool IsUnsafeVersion(string value) =>
        string.Equals(value, "latest", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "null", StringComparison.OrdinalIgnoreCase);

    private static InvalidDataException Invalid() =>
        new("S3_WORM_PIN_UNTRUSTED.");

    private static S3AttemptVersionPinOutcome Uncertain(Guid id) =>
        new(id, false, null, null);
}
