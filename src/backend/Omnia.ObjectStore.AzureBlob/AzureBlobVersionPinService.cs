using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Omnia.Infrastructure;

namespace Omnia.ObjectStore.AzureBlob;

/// <summary>
/// An HMAC-authenticated positive observation of TWO AzureLocked versions
/// supplied by an external caller. Two configured identities alone do NOT
/// prove independently administered accounts, monotonicity or DELETE COMMIT.
/// </summary>
public sealed record AzureBlobVersionPinOutcome(
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
/// An injectable one-shot *pin* write, separate from the original evidence
/// publisher. The real SDK implementation must have zero retries and pin
/// the exact upload ACK VersionId before asking to lock it.
/// </summary>
public interface IAzureBlobWormPinWrite
{
    Task<AzureBlobWormWriteAck> UploadAndLockOnceAsync(
        string objectKey, byte[] content, DateTimeOffset retention,
        CancellationToken ct);
}

/// <summary>
/// ADR 0053: a canonical actor-scoped, signed reference to a concrete
/// AzureLocked evidence version, stored as its own AzureLocked version.
///
/// This operation does NOT establish an independent monotonic authority,
/// global once-ever erasure claim, source DELETE COMMIT or recovery-ready.
/// Neither VersionId may originate from untrusted restored PostgreSQL.
/// </summary>
public sealed class AzureBlobVersionPinService
{
    public const int MaxPinBytes = 1024;
    private const string Purpose = "omnia-azure-attempt-version-pin-v1";
    private const string ActorPurpose = "omnia-azure-pin-actor-scope-v1";

    private readonly IImmutableAttemptObjectReader _evidenceReader;
    private readonly IImmutableAttemptObjectReader _pinReader;
    private readonly IAzureBlobWormPinWrite _writer;
    private readonly string _evidenceStore;
    private readonly string _pinStore;

    private static readonly Regex VersionPattern = new(
        @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}Z\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public AzureBlobVersionPinService(
        IImmutableAttemptObjectReader evidenceReader,
        IImmutableAttemptObjectReader pinReader,
        IAzureBlobWormPinWrite writer,
        string evidenceStoreIdentity, string pinStoreIdentity)
    {
        _evidenceReader = evidenceReader ??
            throw new ArgumentNullException(nameof(evidenceReader));
        _pinReader = pinReader ??
            throw new ArgumentNullException(nameof(pinReader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        if (!ValidToken(evidenceStoreIdentity, 256) ||
            !ValidToken(pinStoreIdentity, 256) ||
            string.Equals(evidenceStoreIdentity, pinStoreIdentity,
                StringComparison.Ordinal))
            throw Invalid();
        _evidenceStore = evidenceStoreIdentity;
        _pinStore = pinStoreIdentity;
    }

    public static string PinKey(Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw Invalid();
        return $"erasure-pins/v1/{requestId:N}.json";
    }

    /// <summary>Deterministic synthetic fixture bytes, NOT custody.</summary>
    public static byte[] CreateSignedSynthetic(
        Guid requestId, string tenantId, string ownerSubject,
        byte[] scopeKey, byte[] evidenceKey, byte[] pinKey,
        string evidenceStoreIdentity, string evidenceVersionId) =>
        BuildSigned(requestId, tenantId, ownerSubject, scopeKey,
            evidenceKey, pinKey, evidenceStoreIdentity, evidenceVersionId);

    /// <summary>
    /// EXACT one-shot conditional PIN write, after authenticating the
    /// original evidence. An ACK can be lost *after* the remote PUT.
    /// That is Uncertain; retry is never automatic.
    /// </summary>
    public async Task<AzureBlobVersionPinOutcome> PublishOnceAsync(
        Guid requestId, string tenantId, string ownerSubject,
        byte[] scopeKey, byte[] evidenceKey, byte[] pinKey,
        string expectedEvidenceVersionId, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        ValidateInputs(requestId, tenantId, ownerSubject,
            scopeKey, evidenceKey, pinKey,
            expectedEvidenceVersionId, nowUtc);

        try
        {
            var evidence = await ImmutableAttemptEvidence.VerifyAsync(
                _evidenceReader, requestId, tenantId, ownerSubject,
                scopeKey, evidenceKey, _evidenceStore,
                expectedEvidenceVersionId, nowUtc, ct);

            if (evidence.VerifiedImmutabilityMode !=
                    ImmutableAttemptImmutabilityMode.AzureLocked ||
                evidence.VerifiedRetainUntilUtc <= nowUtc.AddHours(1) ||
                evidence.VerifiedRetainUntilUtc <= DateTimeOffset.UtcNow.AddHours(1))
                return Uncertain(requestId);

            var signed = BuildSigned(requestId, tenantId, ownerSubject,
                scopeKey, evidenceKey, pinKey, _evidenceStore,
                expectedEvidenceVersionId);
            try
            {
                // Never write before a successful exact-version evidence
                // readback. No external monotonic commit is inferred.
                var ack = await _writer.UploadAndLockOnceAsync(
                    PinKey(requestId), signed,
                    evidence.VerifiedRetainUntilUtc, ct);
                if (!ValidAzureVersion(ack?.VersionId))
                    return Uncertain(requestId);

                return await VerifyPinnedAsync(
                    requestId, tenantId, ownerSubject,
                    scopeKey, evidenceKey, pinKey,
                    expectedEvidenceVersionId, ack!.VersionId!,
                    nowUtc, ct);
            }
            finally { CryptographicOperations.ZeroMemory(signed); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A remote commit may have succeeded without its response.
            return Uncertain(requestId);
        }
    }

    /// <summary>
    /// Both exact VersionIds MUST be externally retained by the caller;
    /// never discover mutable latest or trust restorable SQL.
    /// </summary>
    public async Task<AzureBlobVersionPinOutcome> VerifyPinnedAsync(
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
            if (!ValidAzureVersion(expectedPinVersionId))
                return Uncertain(requestId);

            var pin = await _pinReader.ReadVersionAsync(
                PinKey(requestId), expectedPinVersionId, ct);
            if (pin is null ||
                !string.Equals(pin.StoreIdentity, _pinStore, StringComparison.Ordinal) ||
                !string.Equals(pin.ObjectKey, PinKey(requestId), StringComparison.Ordinal) ||
                !string.Equals(pin.VersionId, expectedPinVersionId, StringComparison.Ordinal) ||
                pin.ImmutabilityMode != ImmutableAttemptImmutabilityMode.AzureLocked ||
                pin.RetainUntilUtc.Offset != TimeSpan.Zero ||
                pin.RetainUntilUtc <= nowUtc ||
                pin.Content is not { Length: >= 2 and <= MaxPinBytes })
                return Uncertain(requestId);

            var expected = BuildSigned(requestId, tenantId,
                ownerSubject, scopeKey, evidenceKey, pinKey,
                _evidenceStore, expectedEvidenceVersionId);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(
                    pin.Content, expected))
                    return Uncertain(requestId);
            }
            finally { CryptographicOperations.ZeroMemory(expected); }

            var evidence = await ImmutableAttemptEvidence.VerifyAsync(
                _evidenceReader, requestId, tenantId, ownerSubject,
                scopeKey, evidenceKey, _evidenceStore,
                expectedEvidenceVersionId, nowUtc, ct);

            if (evidence.VerifiedImmutabilityMode !=
                    ImmutableAttemptImmutabilityMode.AzureLocked ||
                // Timestamp resolution of provider may lose fractions of 1s.
                pin.RetainUntilUtc <
                    evidence.VerifiedRetainUntilUtc.AddSeconds(-1))
                return Uncertain(requestId);

            return new AzureBlobVersionPinOutcome(requestId, true,
                expectedEvidenceVersionId, expectedPinVersionId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Uncertain(requestId);
        }
    }

    private static byte[] BuildSigned(
        Guid requestId, string tenantId, string ownerSubject,
        byte[] scopeKey, byte[] evidenceKey, byte[] pinKey,
        string evidenceStoreIdentity, string evidenceVersionId)
    {
        ValidateKeys(scopeKey, evidenceKey, pinKey);
        PinKey(requestId);
        if (!ValidToken(evidenceStoreIdentity, 256) ||
            !ValidAzureVersion(evidenceVersionId) ||
            string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(ownerSubject))
            throw Invalid();

        // Actor scope is never stored in plaintext. Three independent
        // 32-byte secrets with separate purposes prevent key reuse.
        var actor = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            purpose = ActorPurpose,
            requestId = requestId.ToString("N"),
            tenantId, ownerSubject
        });
        byte[] scope;
        try { scope = HMACSHA256.HashData(scopeKey, actor); }
        finally { CryptographicOperations.ZeroMemory(actor); }

        try
        {
            var scopeMac = Convert.ToBase64String(scope);
            var unsigned = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                purpose = Purpose,
                requestId = requestId.ToString("N"),
                evidenceStoreIdentity, evidenceVersionId, scopeMac
            });
            try
            {
                var tag = HMACSHA256.HashData(pinKey, unsigned);
                try
                {
                    var payload = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        schemaVersion = 1,
                        purpose = Purpose,
                        requestId = requestId.ToString("N"),
                        evidenceStoreIdentity, evidenceVersionId, scopeMac,
                        mac = Convert.ToBase64String(tag)
                    });
                    if (payload.Length is < 2 or > MaxPinBytes)
                    {
                        CryptographicOperations.ZeroMemory(payload);
                        throw Invalid();
                    }
                    return payload;
                }
                finally { CryptographicOperations.ZeroMemory(tag); }
            }
            finally { CryptographicOperations.ZeroMemory(unsigned); }
        }
        finally { CryptographicOperations.ZeroMemory(scope); }
    }

    private static void ValidateInputs(
        Guid requestId, string tenantId, string ownerSubject,
        byte[] scopeKey, byte[] evidenceKey, byte[] pinKey,
        string version, DateTimeOffset nowUtc)
    {
        PinKey(requestId);
        ValidateKeys(scopeKey, evidenceKey, pinKey);
        if (!ValidAzureVersion(version) || nowUtc.Offset != TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(ownerSubject))
            throw Invalid();
    }

    private static void ValidateKeys(
        byte[]? scopeKey, byte[]? evidenceKey, byte[]? pinKey)
    {
        if (scopeKey is not { Length: 32 } ||
            evidenceKey is not { Length: 32 } ||
            pinKey is not { Length: 32 } ||
            CryptographicOperations.FixedTimeEquals(scopeKey, evidenceKey) ||
            CryptographicOperations.FixedTimeEquals(scopeKey, pinKey) ||
            CryptographicOperations.FixedTimeEquals(evidenceKey, pinKey))
            throw Invalid();
    }

    internal static bool ValidAzureVersion(string? value) =>
        value is { Length: 28 } &&
        VersionPattern.IsMatch(value) &&
        DateTimeOffset.TryParseExact(value,
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _);

    internal static bool ValidPinKey(string? key) =>
        key is { Length: 53 } &&
        Regex.IsMatch(key,
            @"\Aerasure-pins/v1/[0-9a-f]{32}\.json\z",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static bool ValidToken(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length <= maxLength &&
        !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl);

    private static InvalidDataException Invalid() =>
        new("AZURE_WORM_PIN_UNTRUSTED.");

    private static AzureBlobVersionPinOutcome Uncertain(Guid id) =>
        new(id, false, null, null);
}
