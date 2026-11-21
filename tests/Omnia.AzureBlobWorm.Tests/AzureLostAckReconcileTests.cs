using System.Security.Cryptography;
using Omnia.Infrastructure;
using Omnia.ObjectStore.AzureBlob;
using Xunit;

namespace Omnia.AzureBlobWorm.Tests;

/// <summary>
/// Synthetic Azure provider history and HMAC fixtures: NOT external
/// offsite custody, once-ever proof or authority to retry/recover.
/// </summary>
public sealed class AzureLostAckReconcileTests
{
    private const string Version = "2026-11-21T15:04:02.0000000Z";
    private const string OtherVersion = "2026-11-21T15:04:03.0000000Z";
    private const string Store = "ci-azure-evidence-store";

    [Fact]
    public async Task LostAckAfterSyntheticCommitMayBeObservedButNeverAuthorizesRetryOrErasure()
    {
        var (id, scope, signing, content, now, retention) = Fixture();
        var writer = new AckLostAfterCommittedWriter();
        var remote = new FixedReader(Valid(id, content, retention));
        var publisher = new AzureBlobWormAttemptPublisher(writer, remote, Store);

        // Scripted remote PUT was committed, but its HTTP ACK was lost.
        // Real Azure service custody is NOT tested by this fake writer.
        var attempted = await publisher.SubmitOnceAsync(
            id, "ci-tenant", "ci-subject", scope, signing,
            content, retention, now, default);
        Assert.True(writer.SyntheticCommitted);
        Assert.Equal(1, writer.Calls);
        Assert.True(attempted.Uncertain);
        Assert.False(attempted.MayAutoRetry);
        Assert.False(attempted.MayReleaseRecovery);
        Assert.Null(attempted.VersionId);
        Assert.Equal(0, remote.Calls);

        var listing = new StubHistory(new AzureBlobHistoryPage(
            [Entry(id)], null));
        var reconciler = new AzureBlobLostAckHistoryReconciler(
            listing, remote, Store);
        var observation = await reconciler.ReconcileAsync(id, "ci-tenant",
            "ci-subject", scope, signing, now, default);

        Assert.True(observation.PreviouslyObserved);
        Assert.False(observation.Uncertain);
        Assert.Equal(Version, observation.VersionId);
        Assert.False(observation.MayAutoRetry);
        Assert.False(observation.MayReleaseRecovery);
        Assert.False(observation.ProvesSourceDeleteCommit);
        Assert.False(observation.ProvesCompleteHistory);
        Assert.False(observation.ProvesMonotonicHistory);
        Assert.Equal(1, writer.Calls); // No second PUT, ever
        Assert.Equal(1, listing.Calls);
        Assert.Equal(1, remote.Calls);
        Assert.Equal((ImmutableAttemptEvidence.ObjectKey(id), (string?)null, 2),
            Assert.Single(listing.Captured));
        Assert.Equal(Version, remote.LastVersionId);
    }

    [Fact]
    public async Task ListingEmptyForeignDeletedSnapshotDuplicateOrSecondVersionNeverCallsReader()
    {
        var (id, scope, signing, content, now, retention) = Fixture();
        var good = Entry(id);
        var key = ImmutableAttemptEvidence.ObjectKey(id);
        var badPages = new[]
        {
            new AzureBlobHistoryPage([], null),
            new AzureBlobHistoryPage([good, Entry(id, OtherVersion, false)], null),
            new AzureBlobHistoryPage([good, good], null),
            new AzureBlobHistoryPage([good with { ObjectKey = key + "foreign" }], null),
            new AzureBlobHistoryPage([good with { VersionId = null }], null),
            new AzureBlobHistoryPage([good with { VersionId = "latest" }], null),
            new AzureBlobHistoryPage([good with { VersionId = "null" }], null),
            new AzureBlobHistoryPage([good with { VersionId =
                "2026-11-21T15:04:02Z" }], null),
            new AzureBlobHistoryPage([good with { Deleted = true }], null),
            new AzureBlobHistoryPage([good with { HasSnapshot = true }], null),
            new AzureBlobHistoryPage([good with { IsCurrentVersion = false }], null),
            new AzureBlobHistoryPage([good, good, good], null),
            new AzureBlobHistoryPage([good], ""),
            new AzureBlobHistoryPage([good], "\n"),
            new AzureBlobHistoryPage([], "forward")
        };

        foreach (var page in badPages)
        {
            var history = new StubHistory(page);
            var reader = new FixedReader(Valid(id, content, retention));
            var actual = await new AzureBlobLostAckHistoryReconciler(
                history, reader, Store).ReconcileAsync(
                    id, "ci-tenant", "ci-subject", scope,
                    signing, now, default);
            Assert.True(actual.Uncertain);
            Assert.Null(actual.VersionId);
            Assert.False(actual.MayAutoRetry);
            Assert.False(actual.MayReleaseRecovery);
            Assert.Equal(0, reader.Calls);
        }
    }

    [Fact]
    public async Task CursorLoopMissingContinuationAndEightPageCapAreUncertain()
    {
        var (id, scope, signing, content, now, retention) = Fixture();
        var good = Entry(id);
        var histories = new[]
        {
            new StubHistory(new AzureBlobHistoryPage([good], "loop"),
                new AzureBlobHistoryPage([], "loop")),
            new StubHistory(new AzureBlobHistoryPage([good], "missing")),
            new StubHistory(Enumerable.Range(0, 9)
                .Select(i => new AzureBlobHistoryPage(
                    i == 0 ? [good] : [], "next-" + i)).ToArray()),
            new StubHistory(new AzureBlobHistoryPage([good], "next"),
                new AzureBlobHistoryPage([Entry(id, OtherVersion, false)], null)),
            new StubHistory(new AzureBlobHistoryPage([good], "next"),
                new AzureBlobHistoryPage([], null))
        };

        foreach (var history in histories)
        {
            var reader = new FixedReader(Valid(id, content, retention));
            var actual = await new AzureBlobLostAckHistoryReconciler(
                history, reader, Store).ReconcileAsync(
                    id, "ci-tenant", "ci-subject", scope,
                    signing, now, default);
            Assert.True(actual.Uncertain);
            Assert.False(actual.MayAutoRetry);
            Assert.InRange(history.Calls, 1, 8);
            Assert.Equal(0, reader.Calls);
        }
    }

    [Fact]
    public async Task ProviderOfflineAndReadbackUnlockedExpiredForeignOrTamperedFailClosed()
    {
        var (id, scope, signing, content, now, retention) = Fixture();
        var key = ImmutableAttemptEvidence.ObjectKey(id);
        var original = Valid(id, content, retention);
        var mutations = new[]
        {
            original with { ImmutabilityMode = ImmutableAttemptImmutabilityMode.Unlocked },
            original with { ImmutabilityMode = ImmutableAttemptImmutabilityMode.S3Compliance },
            original with { RetainUntilUtc = now.AddMinutes(30) },
            original with { StoreIdentity = "other-account" },
            original with { ObjectKey = key + "-other" },
            original with { VersionId = OtherVersion },
            original with { Content = content.Concat(new byte[] { 42 }).ToArray() }
        };
        foreach (var changed in mutations)
        {
            var reader = new FixedReader(changed);
            var observed = await new AzureBlobLostAckHistoryReconciler(
                new StubHistory(new AzureBlobHistoryPage([Entry(id)], null)),
                reader, Store).ReconcileAsync(id, "ci-tenant",
                    "ci-subject", scope, signing, now, default);
            Assert.True(observed.Uncertain);
            Assert.False(observed.MayAutoRetry);
            Assert.False(observed.MayReleaseRecovery);
            Assert.Null(observed.VersionId);
            Assert.Equal(1, reader.Calls);
        }

        var denied = new StubHistory { Denied = true };
        var neverRead = new FixedReader(original);
        var deniedResult = await new AzureBlobLostAckHistoryReconciler(
            denied, neverRead, Store).ReconcileAsync(
                id, "ci-tenant", "ci-subject", scope, signing, now, default);
        Assert.True(deniedResult.Uncertain);
        Assert.Null(deniedResult.VersionId);
        Assert.Equal(0, neverRead.Calls);

        var ioReader = new FixedReader(original) { Denied = true };
        var wrongActor = await new AzureBlobLostAckHistoryReconciler(
            new StubHistory(new AzureBlobHistoryPage([Entry(id)], null)),
            new FixedReader(original), Store).ReconcileAsync(
                id, "ci-tenant", "foreign", scope, signing, now, default);
        Assert.True(wrongActor.Uncertain);

        var ioResult = await new AzureBlobLostAckHistoryReconciler(
            new StubHistory(new AzureBlobHistoryPage([Entry(id)], null)),
            ioReader, Store).ReconcileAsync(
                id, "ci-tenant", "ci-subject", scope, signing, now, default);
        Assert.True(ioResult.Uncertain);
        Assert.Null(ioResult.VersionId);
    }

    [Fact]
    public async Task InvalidActorScopeKeysOrClockDoNotContactProvider()
    {
        var (id, scope, signing, content, now, retention) = Fixture();
        var calls = new (Guid Id, string Tenant, string Actor,
            byte[] Scope, byte[] Signing, DateTimeOffset Clock)[]
        {
            (Guid.Empty, "ci-tenant", "ci-subject", scope, signing, now),
            (id, "", "ci-subject", scope, signing, now),
            (id, "ci-tenant", "", scope, signing, now),
            (id, "ci-tenant", "ci-subject", new byte[31], signing, now),
            (id, "ci-tenant", "ci-subject", scope, scope, now),
            (id, "ci-tenant", "ci-subject", scope, signing,
                now.ToOffset(TimeSpan.FromHours(1)))
        };
        foreach (var args in calls)
        {
            var history = new StubHistory(new AzureBlobHistoryPage(
                [Entry(id)], null));
            var reader = new FixedReader(Valid(id, content, retention));
            var actual = await new AzureBlobLostAckHistoryReconciler(
                history, reader, Store).ReconcileAsync(
                    args.Id, args.Tenant, args.Actor, args.Scope,
                    args.Signing, args.Clock, default);
            Assert.True(actual.Uncertain);
            Assert.Equal(0, history.Calls);
            Assert.Equal(0, reader.Calls);
        }
    }

    private static AzureBlobHistoryEntry Entry(Guid id,
        string version = Version, bool latest = true) =>
        new(ImmutableAttemptEvidence.ObjectKey(id), version,
            false, false, latest);

    private static ImmutableAttemptObjectVersion Valid(
        Guid id, byte[] evidence, DateTimeOffset retention) =>
        new(Store, ImmutableAttemptEvidence.ObjectKey(id), Version,
            ImmutableAttemptImmutabilityMode.AzureLocked,
            retention, evidence);

    private static (Guid Id, byte[] Scope, byte[] Signing, byte[] Content,
        DateTimeOffset Now, DateTimeOffset Retention) Fixture()
    {
        var id = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var signing = RandomNumberGenerator.GetBytes(32);
        var content = ImmutableAttemptEvidence.CreateSynthetic(
            id, "ci-tenant", "ci-subject", scope, signing);
        var now = DateTimeOffset.UtcNow;
        return (id, scope, signing, content, now, now.AddDays(2));
    }

    private sealed class AckLostAfterCommittedWriter : IAzureBlobWormAttemptWrite
    {
        public int Calls { get; private set; }
        public bool SyntheticCommitted { get; private set; }

        public Task<AzureBlobWormWriteAck> UploadAndLockOnceAsync(
            string objectKey, byte[] content, DateTimeOffset retention,
            CancellationToken ct)
        {
            Calls++;
            SyntheticCommitted = true;
            throw new IOException("Synthetic HTTP upload ACK lost after commit.");
        }
    }

    private sealed class StubHistory(params AzureBlobHistoryPage[] pages)
        : IAzureBlobVersionHistory
    {
        public bool Denied { get; init; }
        public int Calls { get; private set; }
        public List<(string, string?, int)> Captured { get; } = [];

        public Task<AzureBlobHistoryPage> ListAsync(
            string objectKey, string? cursor, int pageSize,
            CancellationToken ct)
        {
            Captured.Add((objectKey, cursor, pageSize));
            Calls++;
            if (Denied) throw new UnauthorizedAccessException("Synthetic 403.");
            if (Calls > pages.Length)
                throw new IOException("Missing continuation.");
            return Task.FromResult(pages[Calls - 1]);
        }
    }

    private sealed class FixedReader(
        ImmutableAttemptObjectVersion version) : IImmutableAttemptObjectReader
    {
        public bool Denied { get; init; }
        public int Calls { get; private set; }
        public string? LastVersionId { get; private set; }

        public Task<ImmutableAttemptObjectVersion?> ReadVersionAsync(
            string objectKey, string versionId, CancellationToken ct)
        {
            Calls++;
            LastVersionId = versionId;
            if (Denied) throw new IOException("Synthetic provider unavailable.");
            return Task.FromResult<ImmutableAttemptObjectVersion?>(version);
        }
    }
}
