using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Omnia.Infrastructure;

/// <summary>
/// These are *claims made by the remote object-store adapter*. Neither an
/// enum nor a mock is proof of real object immutability or offsite custody.
/// Provider implementations must inspect the exact pinned object version,
/// its effective lock, account identity and retention deadline, fail closed
/// on delete markers, and never silently fall back to latest-version reads.
/// </summary>
public enum ImmutableAttemptImmutabilityMode
{
    Unlocked,
    Governance,
    S3Compliance,
    AzureLocked
}

public sealed record ImmutableAttemptObjectVersion(
    string StoreIdentity,
    string ObjectKey,
    string VersionId,
    ImmutableAttemptImmutabilityMode ImmutabilityMode,
    DateTimeOffset RetainUntilUtc,
    byte[] Content);

/// <summary>
/// Read one explicitly version-pinned object. NOT a mutable "latest" GET.
/// The caller must obtain expected identity/version from a separately
/// administered trusted control plane, not from the source PostgreSQL.
/// No real adapter is wired until Azure and S3 compliance policies are
/// provisioned, audited and integration tested independently.
/// </summary>
public interface IImmutableAttemptObjectReader
{
    Task<ImmutableAttemptObjectVersion?> ReadVersionAsync(
        string objectKey, string versionId, CancellationToken ct);
}

/// <summary>
/// A previous attempt has been OBSERVED with an authenticated scope.
/// This is NOT evidence of primary DELETE COMMIT, exactly-once semantics
/// after multi-cluster rollback, legal completion or traffic readiness.
/// </summary>
public sealed record ImmutableAttemptObservation(
    Guid RequestId, string VersionId)
{
    public bool AttemptPreviouslyObserved => true;
    public bool MayAutoReplay => false;
    public bool MayReleaseRecovery => false;
    public bool ProvesSourceDeleteCommit => false;
}

/// <summary>
/// ADR 0043: provider-neutral, fail-closed, READ-ONLY verification of a
/// version-pinned object with a domain-separated HMAC scope and attempt
/// evidence. Byte-exact canonical JSON and two distinct keys, with no
/// plaintext tenant/owner in the evidence object. Storage metadata is
/// necessary but NEVER sufficient proof of real WORM or independence.
/// There is intentionally NO "authorize new claim", DELETE, or "ready" API.
/// </summary>
public static class ImmutableAttemptEvidence
{
    public const int MaxBytes = 512;
    private const string Purpose = "omnia-immutable-attempt-evidence-v1";

    public static string ObjectKey(Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw new ArgumentException("An exact request UUID is required.",
                nameof(requestId));
        return $"erasure-attempts/v1/{requestId:N}.json";
    }

    /// <summary>
    /// Creates deterministic synthetic contract-test fixture bytes ONLY.
    /// A real writer MUST obtain an independently provisioned immutable
    /// object version, then pin its version ID outside restored databases.
    /// This method does not upload to any storage or certify a commit.
    /// </summary>
    public static byte[] CreateSynthetic(Guid requestId,
        string tenantId, string ownerSubject, byte[] scopeKey,
        byte[] evidenceKey)
    {
        ValidateKeys(scopeKey, evidenceKey);
        var scope = PreEraseExecutionClaimScope.Compute(
            requestId, tenantId, ownerSubject, scopeKey);
        try
        {
            var scopeText = Convert.ToBase64String(scope);
            var payload = Payload(requestId, scopeText);
            try
            {
                var tag = HMACSHA256.HashData(evidenceKey, payload);
                try
                {
                    return JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        schemaVersion = 1, purpose = Purpose,
                        requestId = requestId.ToString("N"),
                        scopeMac = scopeText,
                        mac = Convert.ToBase64String(tag)
                    });
                }
                finally { CryptographicOperations.ZeroMemory(tag); }
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }
        finally { CryptographicOperations.ZeroMemory(scope); }
    }

    public static async Task<ImmutableAttemptObservation> VerifyAsync(
        IImmutableAttemptObjectReader reader, Guid requestId,
        string tenantId, string ownerSubject, byte[] scopeKey,
        byte[] evidenceKey, string expectedStoreIdentity,
        string expectedVersionId, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ValidateKeys(scopeKey, evidenceKey);
        // Do not let a restored DB point the verifier at a different store,
        // version, or at an unbounded mutable/latest object view.
        if (!ValidToken(expectedStoreIdentity, 256) ||
            !ValidVersionId(expectedVersionId) ||
            nowUtc.Offset != TimeSpan.Zero)
            throw Invalid();

        var objectKey = ObjectKey(requestId);
        ImmutableAttemptObjectVersion observed;
        try
        {
            observed = await reader.ReadVersionAsync(
                objectKey, expectedVersionId, ct) ?? throw Invalid();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or
            HttpRequestException or UnauthorizedAccessException or
            InvalidOperationException)
        {
            throw Invalid();
        }

        if (!string.Equals(observed.StoreIdentity, expectedStoreIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(observed.ObjectKey, objectKey,
                StringComparison.Ordinal) ||
            !string.Equals(observed.VersionId, expectedVersionId,
                StringComparison.Ordinal) ||
            !ValidVersionId(observed.VersionId) ||
            !ValidToken(observed.StoreIdentity, 256) ||
            observed.ImmutabilityMode is not (
                ImmutableAttemptImmutabilityMode.S3Compliance or
                ImmutableAttemptImmutabilityMode.AzureLocked) ||
            observed.RetainUntilUtc.Offset != TimeSpan.Zero ||
            observed.RetainUntilUtc <= nowUtc ||
            observed.Content is not { Length: >= 2 and <= MaxBytes })
            throw Invalid();

        VerifyEnvelope(observed.Content, requestId, tenantId,
            ownerSubject, scopeKey, evidenceKey);
        return new ImmutableAttemptObservation(
            requestId, observed.VersionId);
    }

    private static void VerifyEnvelope(
        byte[] body, Guid requestId, string tenantId, string ownerSubject,
        byte[] scopeKey, byte[] evidenceKey)
    {
        // Scope validation must run even if the JSON has a convincing MAC.
        var scope = PreEraseExecutionClaimScope.Compute(
            requestId, tenantId, ownerSubject, scopeKey);
        try
        {
            try
            {
                using var doc = JsonDocument.Parse(body,
                    new JsonDocumentOptions { MaxDepth = 4 });
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw Invalid();
                var names = root.EnumerateObject().Select(p => p.Name).ToArray();
                if (names.Length != 5 ||
                    names.Distinct(StringComparer.Ordinal).Count() != 5 ||
                    !names.ToHashSet(StringComparer.Ordinal).SetEquals(
                        ["schemaVersion", "purpose", "requestId", "scopeMac", "mac"]))
                    throw Invalid();
                if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
                    root.GetProperty("purpose").GetString() != Purpose ||
                    root.GetProperty("requestId").GetString() !=
                        requestId.ToString("N"))
                    throw Invalid();

                var observedScope = ReadCanonicalMac(
                    root.GetProperty("scopeMac").GetString());
                var observedMac = ReadCanonicalMac(
                    root.GetProperty("mac").GetString());
                try
                {
                    // Strict byte-exact canonical payload guards against
                    // field reordering, duplicate properties, trailing data,
                    // noncanonical encodings, and ambiguous JSON parsers.
                    var canonical = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        schemaVersion = 1, purpose = Purpose,
                        requestId = requestId.ToString("N"),
                        scopeMac = Convert.ToBase64String(observedScope),
                        mac = Convert.ToBase64String(observedMac)
                    });
                    try
                    {
                        if (!body.AsSpan().SequenceEqual(canonical) ||
                            !CryptographicOperations.FixedTimeEquals(
                                observedScope, scope))
                            throw Invalid();
                    }
                    finally { CryptographicOperations.ZeroMemory(canonical); }

                    var payload = Payload(
                        requestId, Convert.ToBase64String(observedScope));
                    try
                    {
                        var expectedMac = HMACSHA256.HashData(
                            evidenceKey, payload);
                        try
                        {
                            if (!CryptographicOperations.FixedTimeEquals(
                                expectedMac, observedMac))
                                throw Invalid();
                        }
                        finally { CryptographicOperations.ZeroMemory(expectedMac); }
                    }
                    finally { CryptographicOperations.ZeroMemory(payload); }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(observedScope);
                    CryptographicOperations.ZeroMemory(observedMac);
                }
            }
            catch (Exception ex) when (ex is JsonException or
                InvalidOperationException or FormatException or
                OverflowException)
            {
                throw Invalid();
            }
        }
        finally { CryptographicOperations.ZeroMemory(scope); }
    }

    private static byte[] Payload(Guid requestId, string scopeMac) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            purpose = Purpose,
            requestId = requestId.ToString("N"),
            scopeMac
        });

    private static byte[] ReadCanonicalMac(string? encoded)
    {
        if (encoded is not { Length: 44 }) throw Invalid();
        var raw = Convert.FromBase64String(encoded);
        if (raw.Length != 32 ||
            !string.Equals(Convert.ToBase64String(raw), encoded,
                StringComparison.Ordinal))
            throw Invalid();
        return raw;
    }

    private static void ValidateKeys(byte[]? scopeKey, byte[]? evidenceKey)
    {
        if (scopeKey is not { Length: 32 } ||
            evidenceKey is not { Length: 32 } ||
            CryptographicOperations.FixedTimeEquals(scopeKey, evidenceKey))
            throw Invalid();
    }

    // Refuse mutable/current-object fallbacks: S3 unversioned objects use
    // VersionId="null"; "latest" is not an immutable object version.
    private static bool ValidVersionId(string? value) =>
        ValidToken(value, 256) &&
        !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, "latest", StringComparison.OrdinalIgnoreCase);

    private static bool ValidToken(string? value, int maxLength) =>
        value is { Length: > 0 } &&
        value.Length <= maxLength &&
        !string.IsNullOrWhiteSpace(value) &&
        value.All(x => !char.IsControl(x));

    private static InvalidDataException Invalid() =>
        new("IMMUTABLE_ATTEMPT_EVIDENCE_UNTRUSTED.");
}
