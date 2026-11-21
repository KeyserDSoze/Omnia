using System.Security.Cryptography;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// ADR 0043: immutable evidence is a pinned, authenticated observation,
/// NEVER a grant to replay a DELETE or reopen recovery. Mocked object-store
/// metadata does not establish that a real provider enforces WORM.
/// </summary>
public sealed class ImmutableAttemptEvidenceContractTests
{
    [Fact]
    public async Task PinnedLockedObservationNeverGrantsRetryReleaseOrCommit()
    {
        var request = Guid.NewGuid();
        var scopeKey = RandomNumberGenerator.GetBytes(32);
        var evidenceKey = RandomNumberGenerator.GetBytes(32);
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var bytes = ImmutableAttemptEvidence.CreateSynthetic(
            request, "synthetic-tenant", "synthetic-sub", scopeKey, evidenceKey);
        // An external control plane MUST pin both the store identity and
        // exact immutable object version, not blindly trust a latest pointer.
        var version = new ImmutableAttemptObjectVersion(
            "vault-external-1",
            ImmutableAttemptEvidence.ObjectKey(request),
            "immutable-version-99",
            ImmutableAttemptImmutabilityMode.S3Compliance,
            now.AddDays(5), bytes);
        var observed = await ImmutableAttemptEvidence.VerifyAsync(
            new MemoryVersionReader(version), request,
            "synthetic-tenant", "synthetic-sub", scopeKey, evidenceKey,
            "vault-external-1", "immutable-version-99", now, default);
        Assert.Equal(request, observed.RequestId);
        Assert.Equal("immutable-version-99", observed.VersionId);
        Assert.False(observed.MayReleaseRecovery);
        Assert.False(observed.MayAutoReplay);
        Assert.False(observed.ProvesSourceDeleteCommit);
        Assert.True(observed.AttemptPreviouslyObserved);

        Assert.Throws<ArgumentException>(() =>
            ImmutableAttemptEvidence.ObjectKey(Guid.Empty));
    }

    [Fact]
    public async Task MissingMutableExpiredOrUnpinnedEvidenceFailsClosed()
    {
        var request = Guid.NewGuid();
        var scopeKey = RandomNumberGenerator.GetBytes(32);
        var evidenceKey = RandomNumberGenerator.GetBytes(32);
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var body = ImmutableAttemptEvidence.CreateSynthetic(
            request, "synthetic-tenant", "synthetic-sub", scopeKey, evidenceKey);
        var version = new ImmutableAttemptObjectVersion(
            "vault-external-1", ImmutableAttemptEvidence.ObjectKey(request),
            "immutable-version-99", ImmutableAttemptImmutabilityMode.S3Compliance,
            now.AddDays(5), body);
        async Task Deny(ImmutableAttemptObjectVersion? v,
            string store = "vault-external-1", string pinned = "immutable-version-99")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ImmutableAttemptEvidence.VerifyAsync(new MemoryVersionReader(v),
                    request, "synthetic-tenant", "synthetic-sub",
                    scopeKey, evidenceKey, store, pinned, now, default));
        }

        await Deny(null);
        await Deny(version with { StoreIdentity = "wrong-vault" });
        await Deny(version with { ObjectKey = "other-object" });
        await Deny(version with { VersionId = "other-version" });
        await Deny(version, pinned: "");
        await Deny(version, store: "");
        await Deny(version with { VersionId = "" });
        await Deny(version with { ImmutabilityMode =
            ImmutableAttemptImmutabilityMode.Governance });
        await Deny(version with { ImmutabilityMode =
            ImmutableAttemptImmutabilityMode.Unlocked });
        await Deny(version with { RetainUntilUtc = now });
        await Deny(version with { RetainUntilUtc = now.AddMinutes(-1) });
        await Deny(version with { RetainUntilUtc =
            new DateTimeOffset(2026, 11, 22, 13, 0, 0,
                TimeSpan.FromHours(1)) });
        // A locked Azure blob *version* is a supported independent
        // provider mode, but never an automatic recovery release.
        var azure = version with {
            ImmutabilityMode = ImmutableAttemptImmutabilityMode.AzureLocked };
        Assert.False((await ImmutableAttemptEvidence.VerifyAsync(
            new MemoryVersionReader(azure), request, "synthetic-tenant",
            "synthetic-sub", scopeKey, evidenceKey, "vault-external-1",
            "immutable-version-99", now, default)).MayReleaseRecovery);
    }

    [Fact]
    public async Task WrongActorKeyCorruptedMacOrUnexpectedJsonFailsClosed()
    {
        var request = Guid.NewGuid();
        var scopeKey = RandomNumberGenerator.GetBytes(32);
        var evidenceKey = RandomNumberGenerator.GetBytes(32);
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var body = ImmutableAttemptEvidence.CreateSynthetic(
            request, "synthetic-tenant", "synthetic-sub", scopeKey, evidenceKey);
        var version = new ImmutableAttemptObjectVersion(
            "vault-external-1", ImmutableAttemptEvidence.ObjectKey(request),
            "v-1", ImmutableAttemptImmutabilityMode.S3Compliance,
            now.AddDays(2), body);
        Task<ImmutableAttemptObservation> Inspect(
            byte[] data, string tenant = "synthetic-tenant",
            string subject = "synthetic-sub", byte[]? key = null,
            byte[]? signing = null, Guid? id = null) =>
            ImmutableAttemptEvidence.VerifyAsync(
                new MemoryVersionReader(version with { Content = data }),
                id ?? request, tenant, subject, key ?? scopeKey,
                signing ?? evidenceKey, "vault-external-1",
                "v-1", now, default);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Inspect(body, tenant: "other-tenant"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Inspect(body, subject: "other-subject"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Inspect(body, key: RandomNumberGenerator.GetBytes(32)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Inspect(body, signing: RandomNumberGenerator.GetBytes(32)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Inspect(body, signing: scopeKey));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Inspect(body, id: Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Inspect([]));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Inspect([.. body, (byte)' ']));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Inspect(new byte[ImmutableAttemptEvidence.MaxBytes + 1]));
        var corrupt = (byte[])body.Clone();
        corrupt[^2] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => Inspect(corrupt));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Inspect("not-json"u8.ToArray()));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Inspect(body, key: new byte[16]));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Inspect(body, signing: new byte[16]));
    }

    [Fact]
    public async Task BackendErrorIsNotInterpretedAsAnAbsentOrCleanAttempt()
    {
        var request = Guid.NewGuid();
        var key = RandomNumberGenerator.GetBytes(32);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ImmutableAttemptEvidence.VerifyAsync(
                new BrokenVersionReader(), request, "synthetic-tenant",
                "synthetic-sub", key, RandomNumberGenerator.GetBytes(32),
                "vault-external-1", "v-1", DateTimeOffset.UtcNow, default));
    }

    private sealed class MemoryVersionReader(
        ImmutableAttemptObjectVersion? value) : IImmutableAttemptObjectReader
    {
        public Task<ImmutableAttemptObjectVersion?> ReadVersionAsync(
            string objectKey, string versionId, CancellationToken ct) =>
            Task.FromResult(value);
    }

    private sealed class BrokenVersionReader : IImmutableAttemptObjectReader
    {
        public Task<ImmutableAttemptObjectVersion?> ReadVersionAsync(
            string objectKey, string versionId, CancellationToken ct) =>
            throw new IOException("synthetic remote storage error");
    }
}
