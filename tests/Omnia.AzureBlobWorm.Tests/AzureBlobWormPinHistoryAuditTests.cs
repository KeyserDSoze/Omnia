using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Omnia.Infrastructure;
using Omnia.ObjectStore.AzureBlob;
using Xunit;

namespace Omnia.AzureBlobWorm.Tests;

/// <summary>
/// The history is provider-visible, not an independent monotonic journal.
/// Scripted Azure HTTP only: no real Azure policy, IAM, or offsite custody.
/// </summary>
public sealed class AzureBlobWormPinHistoryAuditTests
{
    private const string EvidenceVersion = "2026-11-21T15:15:00.0000000Z";
    private const string PinVersion = "2026-11-21T15:15:01.0000000Z";
    private const string OtherVersion = "2026-11-21T15:15:02.0000000Z";
    private const string EvidenceStore = "ci-azure-evidence-store";
    private const string PinStore = "ci-azure-pin-store";
    private const string ContainerUri =
        "https://ci-account.blob.core.windows.net/omnia-pin-container";

    [Fact]
    public async Task SingleVisibleSignedLockedPinMayBeObservedWithoutRetryOrRecoveryAuthority()
    {
        var f = Fixture();
        var listing = new StubHistory(new AzureBlobHistoryPage(
            [Entry(f.Id)], null));
        var original = new FixedReader(Evidence(f));
        var pin = new FixedReader(Pin(f));
        var writer = new NeverWrite();
        var service = new AzureBlobVersionPinService(
            original, pin, writer, EvidenceStore, PinStore);
        var audit = new AzureBlobWormPinHistoryAudit(listing, service);

        var result = await audit.AuditAsync(f.Id, "ci-tenant", "ci-owner",
            f.Scope, f.EvidenceKey, f.PinKey, EvidenceVersion, PinVersion,
            f.Now, default);

        Assert.True(result.SingleVisiblePinVerified);
        Assert.False(result.Uncertain);
        Assert.Equal(PinVersion, result.PinVersionId);
        Assert.False(result.MayAutoRetry);
        Assert.False(result.MayReleaseRecovery);
        Assert.False(result.ProvesCompleteHistory);
        Assert.False(result.ProvesMonotonicHistory);
        Assert.False(result.ProvesSourceDeleteCommit);
        Assert.Equal(1, listing.Calls);
        Assert.Equal((AzureBlobVersionPinService.PinKey(f.Id), (string?)null, 2),
            Assert.Single(listing.Requested));
        Assert.Equal(1, pin.Calls);
        Assert.Equal(PinVersion, pin.LastVersion);
        Assert.Equal(1, original.Calls);
        Assert.Equal(EvidenceVersion, original.LastVersion);
        Assert.Equal(0, writer.Calls);
    }

    [Fact]
    public async Task CompetingVersionDeletedSnapshotForeignDuplicateOrUnversionedPinDeniesBeforeReadback()
    {
        var f = Fixture();
        var good = Entry(f.Id);
        var key = AzureBlobVersionPinService.PinKey(f.Id);
        var cases = new[]
        {
            new AzureBlobHistoryPage([], null),
            new AzureBlobHistoryPage(
                [good, good with { VersionId = OtherVersion,
                    IsCurrentVersion = false }], null),
            new AzureBlobHistoryPage([good, good], null),
            new AzureBlobHistoryPage([good with { Deleted = true }], null),
            new AzureBlobHistoryPage([good with { HasSnapshot = true }], null),
            new AzureBlobHistoryPage([good with { IsCurrentVersion = false }], null),
            new AzureBlobHistoryPage([good with { VersionId = null }], null),
            new AzureBlobHistoryPage([good with { VersionId = "null" }], null),
            new AzureBlobHistoryPage([good with { VersionId = "latest" }], null),
            new AzureBlobHistoryPage([good with { VersionId =
                "2026-11-21T15:15:01Z" }], null),
            new AzureBlobHistoryPage([good with { ObjectKey = key + "suffix" }], null),
            new AzureBlobHistoryPage([good with { ObjectKey =
                ImmutableAttemptEvidence.ObjectKey(f.Id) }], null),
            new AzureBlobHistoryPage([good, good, good], null),
            new AzureBlobHistoryPage([good], ""),
            new AzureBlobHistoryPage([good], "\n")
        };
        foreach (var page in cases)
        {
            var history = new StubHistory(page);
            var original = new FixedReader(Evidence(f));
            var pin = new FixedReader(Pin(f));
            var audit = Audit(history, original, pin);
            var observed = await audit.AuditAsync(f.Id, "ci-tenant",
                "ci-owner", f.Scope, f.EvidenceKey, f.PinKey,
                EvidenceVersion, PinVersion, f.Now, default);

            Assert.True(observed.Uncertain);
            Assert.False(observed.SingleVisiblePinVerified);
            Assert.Null(observed.PinVersionId);
            Assert.False(observed.MayAutoRetry);
            Assert.False(observed.MayReleaseRecovery);
            Assert.Equal(0, pin.Calls);
            Assert.Equal(0, original.Calls);
        }
    }

    [Fact]
    public async Task MissingPageCursorCycleProvider403AndScanCapAreAlwaysUncertain()
    {
        var f = Fixture();
        var good = Entry(f.Id);
        var scenarios = new[]
        {
            new StubHistory(new AzureBlobHistoryPage([good], "repeat"),
                new AzureBlobHistoryPage([good], "repeat")),
            new StubHistory(new AzureBlobHistoryPage([good], "missing")),
            new StubHistory(new AzureBlobHistoryPage([good], "next"),
                new AzureBlobHistoryPage([], null)),
            new StubHistory(new AzureBlobHistoryPage([good], "next"),
                new AzureBlobHistoryPage(
                    [Entry(f.Id) with { VersionId = OtherVersion,
                        IsCurrentVersion = false }], null)),
            new StubHistory(Enumerable.Range(0, 9)
                .Select(n => new AzureBlobHistoryPage(
                    n == 0 ? [good] : [], "page-" + n)).ToArray())
        };
        foreach (var history in scenarios)
        {
            var original = new FixedReader(Evidence(f));
            var pin = new FixedReader(Pin(f));
            var result = await Audit(history, original, pin).AuditAsync(
                f.Id, "ci-tenant", "ci-owner", f.Scope,
                f.EvidenceKey, f.PinKey, EvidenceVersion, PinVersion,
                f.Now, default);
            Assert.True(result.Uncertain);
            Assert.False(result.MayAutoRetry);
            Assert.InRange(history.Calls, 1, 8);
            Assert.Equal(0, pin.Calls);
            Assert.Equal(0, original.Calls);
        }

        var deniedHistory = new StubHistory { Denied = true };
        var deniedPin = new FixedReader(Pin(f));
        var deniedOriginal = new FixedReader(Evidence(f));
        var denied = await Audit(deniedHistory, deniedOriginal, deniedPin)
            .AuditAsync(f.Id, "ci-tenant", "ci-owner", f.Scope,
                f.EvidenceKey, f.PinKey, EvidenceVersion, PinVersion,
                f.Now, default);
        Assert.True(denied.Uncertain);
        Assert.Equal(0, deniedPin.Calls);
        Assert.Equal(0, deniedOriginal.Calls);
    }

    [Fact]
    public async Task InvalidExternallyPinnedVersionsKeysAndClockCannotReachProvider()
    {
        var f = Fixture();
        var invalid = new (Guid Id, string Tenant, string Owner,
            byte[] Scope, byte[] EvidenceKey, byte[] PinKey,
            string EvidenceVersion, string PinVersion, DateTimeOffset Now)[]
        {
            (Guid.Empty, "ci-tenant", "ci-owner", f.Scope,
                f.EvidenceKey, f.PinKey, EvidenceVersion, PinVersion, f.Now),
            (f.Id, "", "ci-owner", f.Scope,
                f.EvidenceKey, f.PinKey, EvidenceVersion, PinVersion, f.Now),
            (f.Id, "ci-tenant", "", f.Scope,
                f.EvidenceKey, f.PinKey, EvidenceVersion, PinVersion, f.Now),
            (f.Id, "ci-tenant", "ci-owner", new byte[31],
                f.EvidenceKey, f.PinKey, EvidenceVersion, PinVersion, f.Now),
            (f.Id, "ci-tenant", "ci-owner", f.Scope,
                f.EvidenceKey, f.Scope, EvidenceVersion, PinVersion, f.Now),
            (f.Id, "ci-tenant", "ci-owner", f.Scope,
                f.EvidenceKey, f.PinKey, "latest", PinVersion, f.Now),
            (f.Id, "ci-tenant", "ci-owner", f.Scope,
                f.EvidenceKey, f.PinKey, EvidenceVersion, "null", f.Now),
            (f.Id, "ci-tenant", "ci-owner", f.Scope,
                f.EvidenceKey, f.PinKey, EvidenceVersion,
                "2026-11-21T15:15:01Z", f.Now),
            (f.Id, "ci-tenant", "ci-owner", f.Scope,
                f.EvidenceKey, f.PinKey, EvidenceVersion, PinVersion,
                f.Now.ToOffset(TimeSpan.FromHours(1)))
        };
        foreach (var args in invalid)
        {
            var listing = new StubHistory(new AzureBlobHistoryPage(
                [Entry(f.Id)], null));
            var original = new FixedReader(Evidence(f));
            var pin = new FixedReader(Pin(f));
            var result = await Audit(listing, original, pin)
                .AuditAsync(args.Id, args.Tenant, args.Owner, args.Scope,
                    args.EvidenceKey, args.PinKey, args.EvidenceVersion,
                    args.PinVersion, args.Now, default);
            Assert.True(result.Uncertain);
            Assert.Equal(0, listing.Calls);
            Assert.Equal(0, pin.Calls);
            Assert.Equal(0, original.Calls);
        }
    }

    [Fact]
    public async Task WrongActorInvalidPinMacAndShortRetentionAreUncertainAfterList()
    {
        var f = Fixture();
        var proper = Pin(f);
        var tampered = proper.Content.ToArray();
        tampered[^2] ^= 1;
        var invalidPins = new[]
        {
            proper with { Content = tampered },
            proper with { StoreIdentity = "wrong-store" },
            proper with { VersionId = OtherVersion },
            proper with { RetainUntilUtc = f.Now.AddMinutes(5) },
            proper with { RetainUntilUtc = f.Expiry.AddMinutes(-5) },
            proper with { ImmutabilityMode = ImmutableAttemptImmutabilityMode.Unlocked }
        };
        foreach (var mutated in invalidPins)
        {
            var listing = new StubHistory(new AzureBlobHistoryPage(
                [Entry(f.Id)], null));
            var pin = new FixedReader(mutated);
            var result = await Audit(listing, new FixedReader(Evidence(f)), pin)
                .AuditAsync(f.Id, "ci-tenant", "ci-owner",
                    f.Scope, f.EvidenceKey, f.PinKey, EvidenceVersion,
                    PinVersion, f.Now, default);
            Assert.True(result.Uncertain);
            Assert.False(result.MayReleaseRecovery);
            Assert.Equal(1, listing.Calls);
            Assert.Equal(1, pin.Calls);
        }
        var badActor = await Audit(
            new StubHistory(new AzureBlobHistoryPage([Entry(f.Id)], null)),
            new FixedReader(Evidence(f)), new FixedReader(proper))
            .AuditAsync(f.Id, "ci-tenant", "foreign-owner",
                f.Scope, f.EvidenceKey, f.PinKey, EvidenceVersion,
                PinVersion, f.Now, default);
        Assert.True(badActor.Uncertain);
    }

    [Fact]
    public async Task ActualAzureSdkListBlobsAcceptsOnlyCanonicalPinKeyAndIncludesAllStates()
    {
        var f = Fixture();
        var key = AzureBlobVersionPinService.PinKey(f.Id);
        using var handler = new AzureListScript(key);
        var options = new BlobClientOptions {
            Transport = new HttpClientTransport(new HttpClient(handler))
        };
        options.Retry.MaxRetries = 0;
        var container = new BlobContainerClient(new Uri(ContainerUri), options);
        var history = new AzureBlobSdkVersionHistory(container);

        var result = await history.ListAsync(key, null, 2, default);

        Assert.Single(result.Entries);
        Assert.Equal(key, result.Entries[0].ObjectKey);
        Assert.Equal(PinVersion, result.Entries[0].VersionId);
        Assert.True(result.Entries[0].IsCurrentVersion);
        Assert.Null(result.NextCursor);
        Assert.Single(handler.Calls);
        var request = handler.Calls[0];
        Assert.Equal("GET", request.Method);
        Assert.Contains("comp=list", request.Query,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("maxresults=2", request.Query,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prefix=" + key, request.Query,
            StringComparison.OrdinalIgnoreCase);
        foreach (var state in new[] { "versions", "deleted",
            "deletedwithversions", "snapshots" })
            Assert.Contains(state, request.Query,
                StringComparison.OrdinalIgnoreCase);
    }

    private static AzureBlobWormPinHistoryAudit Audit(
        IAzureBlobVersionHistory history, IImmutableAttemptObjectReader evidence,
        IImmutableAttemptObjectReader pin) =>
        new(history, new AzureBlobVersionPinService(
            evidence, pin, new NeverWrite(), EvidenceStore, PinStore));

    private static AzureBlobHistoryEntry Entry(Guid id) =>
        new(AzureBlobVersionPinService.PinKey(id), PinVersion,
            false, false, true);

    private static ImmutableAttemptObjectVersion Evidence(FixtureData f) =>
        new(EvidenceStore, ImmutableAttemptEvidence.ObjectKey(f.Id),
            EvidenceVersion, ImmutableAttemptImmutabilityMode.AzureLocked,
            f.Expiry, f.SignedEvidence);

    private static ImmutableAttemptObjectVersion Pin(FixtureData f) =>
        new(PinStore, AzureBlobVersionPinService.PinKey(f.Id),
            PinVersion, ImmutableAttemptImmutabilityMode.AzureLocked,
            f.Expiry, AzureBlobVersionPinService.CreateSignedSynthetic(
                f.Id, "ci-tenant", "ci-owner", f.Scope, f.EvidenceKey,
                f.PinKey, EvidenceStore, EvidenceVersion));

    private static FixtureData Fixture()
    {
        var id = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var evidenceKey = RandomNumberGenerator.GetBytes(32);
        var pinKey = RandomNumberGenerator.GetBytes(32);
        var now = DateTimeOffset.UtcNow;
        return new FixtureData(id, scope, evidenceKey, pinKey, now,
            now.AddDays(3), ImmutableAttemptEvidence.CreateSynthetic(
                id, "ci-tenant", "ci-owner", scope, evidenceKey));
    }

    private sealed record FixtureData(
        Guid Id, byte[] Scope, byte[] EvidenceKey, byte[] PinKey,
        DateTimeOffset Now, DateTimeOffset Expiry, byte[] SignedEvidence);

    private sealed class NeverWrite : IAzureBlobWormPinWrite
    {
        public int Calls { get; private set; }
        public Task<AzureBlobWormWriteAck> UploadAndLockOnceAsync(
            string objectKey, byte[] content, DateTimeOffset retention,
            CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("READ ONLY audit must never PUT.");
        }
    }

    private sealed class FixedReader(ImmutableAttemptObjectVersion result)
        : IImmutableAttemptObjectReader
    {
        public int Calls { get; private set; }
        public string? LastVersion { get; private set; }
        public Task<ImmutableAttemptObjectVersion?> ReadVersionAsync(
            string key, string versionId, CancellationToken ct)
        {
            Calls++;
            LastVersion = versionId;
            return Task.FromResult<ImmutableAttemptObjectVersion?>(result);
        }
    }

    private sealed class StubHistory(params AzureBlobHistoryPage[] pages)
        : IAzureBlobVersionHistory
    {
        public bool Denied { get; set; }
        public int Calls { get; private set; }
        public List<(string, string?, int)> Requested { get; } = [];

        public Task<AzureBlobHistoryPage> ListAsync(
            string objectKey, string? cursor, int pageSize, CancellationToken ct)
        {
            Requested.Add((objectKey, cursor, pageSize));
            Calls++;
            if (Denied) throw new UnauthorizedAccessException("Synthetic 403.");
            return Calls <= pages.Length
                ? Task.FromResult(pages[Calls - 1])
                : throw new IOException("Missing next page.");
        }
    }

    private sealed class AzureListScript(string key) : HttpMessageHandler
    {
        public List<(string Method, string Query)> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.Method.Method,
                Uri.UnescapeDataString(request.RequestUri!.Query)));
            var xml = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <EnumerationResults ServiceEndpoint="https://ci-account.blob.core.windows.net/" ContainerName="omnia-pin-container">
                  <Prefix>{key}</Prefix><MaxResults>2</MaxResults>
                  <Blobs><Blob>
                    <Name>{key}</Name>
                    <Properties>
                      <Last-Modified>Sat, 21 Nov 2026 15:15:01 GMT</Last-Modified>
                      <Etag>"ci-pin-etag"</Etag>
                      <Content-Length>512</Content-Length>
                      <BlobType>BlockBlob</BlobType>
                    </Properties>
                    <VersionId>{PinVersion}</VersionId>
                    <IsCurrentVersion>true</IsCurrentVersion>
                    <Deleted>false</Deleted>
                  </Blob></Blobs>
                  <NextMarker></NextMarker>
                </EnumerationResults>
                """;
            var response = new HttpResponseMessage(HttpStatusCode.OK) {
                RequestMessage = request, Content = new StringContent(xml)
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
            response.Headers.TryAddWithoutValidation("x-ms-request-id", "ci-pin-list");
            return Task.FromResult(response);
        }
    }
}
