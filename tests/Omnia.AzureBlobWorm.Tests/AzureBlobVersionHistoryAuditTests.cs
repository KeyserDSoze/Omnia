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
/// These are read-only *visible catalogue* observations. They are not
/// Azure WORM staging, offsite custody or source DELETE COMMIT proof.
/// </summary>
public sealed class AzureBlobVersionHistoryAuditTests
{
    private const string Version = "2026-11-21T13:04:02.0000000Z";
    private const string OtherVersion = "2026-11-21T13:04:03.0000000Z";
    private const string ContainerUri =
        "https://ci-account.blob.core.windows.net/omnia-private";

    [Fact]
    public async Task SingleVisibleLockedVersionCanBeAuthenticatedButNeverAuthorizesRetryOrRecovery()
    {
        var (id, scope, signing, evidence, now) = Fixture();
        var key = ImmutableAttemptEvidence.ObjectKey(id);
        var history = new FakeHistory(new AzureBlobHistoryPage(
            [new AzureBlobHistoryEntry(key, Version, false, false, true)],
            null));
        var reader = new StaticReader(new ImmutableAttemptObjectVersion(
            "ci-azure-store", key, Version,
            ImmutableAttemptImmutabilityMode.AzureLocked,
            now.AddDays(2), evidence));
        var audit = new AzureBlobWormHistoryAudit(
            history, reader, "ci-azure-store");

        var result = await audit.AuditAsync(id, "ci-tenant", "ci-owner",
            scope, signing, Version, now, default);

        Assert.True(result.SingleVisibleVersionVerified);
        Assert.False(result.Uncertain);
        Assert.False(result.MayAutoRetry);
        Assert.False(result.MayReleaseRecovery);
        Assert.False(result.ProvesCompleteHistory);
        Assert.False(result.ProvesSourceDeleteCommit);
        Assert.False(result.ProvesMonotonicHistory);
        Assert.Equal(1, history.Calls);
        Assert.Equal(1, reader.Calls);
        Assert.All(history.Requested, p =>
            Assert.Equal((key, (string?)null, 2), p));
    }

    [Fact]
    public async Task AmbiguityMissingDeletionSnapshotDuplicateOrPaginationDeniesBeforeReadback()
    {
        var (id, scope, signing, evidence, now) = Fixture();
        var key = ImmutableAttemptEvidence.ObjectKey(id);
        var good = new AzureBlobHistoryEntry(key, Version,
            false, false, true);
        var scenarios = new[]
        {
            new AzureBlobHistoryPage([], null),
            new AzureBlobHistoryPage(
                [good, new AzureBlobHistoryEntry(key,
                    OtherVersion, false, false, false)], null),
            new AzureBlobHistoryPage(
                [good, new AzureBlobHistoryEntry(key,
                    Version, false, false, true)], null),
            new AzureBlobHistoryPage(
                [new AzureBlobHistoryEntry(key, Version,
                    true, false, true)], null),
            new AzureBlobHistoryPage(
                [new AzureBlobHistoryEntry(key, Version,
                    false, true, true)], null),
            new AzureBlobHistoryPage(
                [new AzureBlobHistoryEntry(key + "-foreign", Version,
                    false, false, true)], null),
            new AzureBlobHistoryPage(
                [new AzureBlobHistoryEntry(key, null,
                    false, false, true)], null),
            new AzureBlobHistoryPage(
                [new AzureBlobHistoryEntry(key, "latest",
                    false, false, true)], null),
            new AzureBlobHistoryPage([good, good, good], null),
            new AzureBlobHistoryPage([good], ""),
            new AzureBlobHistoryPage([good], "\n")
        };
        foreach (var page in scenarios)
        {
            var history = new FakeHistory(page);
            var reader = new StaticReader(new ImmutableAttemptObjectVersion(
                "ci-azure-store", key, Version,
                ImmutableAttemptImmutabilityMode.AzureLocked,
                now.AddDays(2), evidence));
            var audit = new AzureBlobWormHistoryAudit(
                history, reader, "ci-azure-store");
            var result = await audit.AuditAsync(id, "ci-tenant", "ci-owner",
                scope, signing, Version, now, default);
            Assert.True(result.Uncertain);
            Assert.False(result.SingleVisibleVersionVerified);
            Assert.False(result.MayAutoRetry);
            Assert.False(result.MayReleaseRecovery);
            Assert.Equal(0, reader.Calls);
        }
    }

    [Fact]
    public async Task PaginationLoopTruncationProvider403AndWrongActorRemainUncertain()
    {
        var (id, scope, signing, evidence, now) = Fixture();
        var key = ImmutableAttemptEvidence.ObjectKey(id);
        var good = new AzureBlobHistoryEntry(key, Version, false, false, true);
        var reader = new StaticReader(new ImmutableAttemptObjectVersion(
            "ci-azure-store", key, Version,
            ImmutableAttemptImmutabilityMode.AzureLocked,
            now.AddDays(2), evidence));
        var scenarios = new[]
        {
            new FakeHistory(new AzureBlobHistoryPage([good], "repeat"),
                new AzureBlobHistoryPage([], "repeat")),
            new FakeHistory(Enumerable.Range(1, 9)
                .Select(n => new AzureBlobHistoryPage(
                    n == 1 ? [good] : [], "next-" + n)).ToArray()),
            new FakeHistory(new AzureBlobHistoryPage([good], "next"),
                new AzureBlobHistoryPage([new AzureBlobHistoryEntry(
                    key, OtherVersion, false, false, false)], null)),
            new FakeHistory(new AzureBlobHistoryPage([good], "next"))
        };
        foreach (var history in scenarios)
        {
            var audit = new AzureBlobWormHistoryAudit(history,
                reader, "ci-azure-store");
            var result = await audit.AuditAsync(id, "ci-tenant", "ci-owner",
                scope, signing, Version, now, default);
            Assert.True(result.Uncertain);
            Assert.False(result.MayAutoRetry);
            Assert.False(result.MayReleaseRecovery);
        }
        Assert.Equal(0, reader.Calls);

        var deniedHistory = new FakeHistory { Denied = true };
        var denied = await new AzureBlobWormHistoryAudit(
            deniedHistory, reader, "ci-azure-store")
            .AuditAsync(id, "ci-tenant", "ci-owner", scope,
                signing, Version, now, default);
        Assert.True(denied.Uncertain);
        Assert.Equal(0, reader.Calls);

        var validHistory = new FakeHistory(new AzureBlobHistoryPage(
            [good], null));
        var foreign = await new AzureBlobWormHistoryAudit(
            validHistory, reader, "ci-azure-store").AuditAsync(
                id, "ci-tenant", "foreign", scope,
                signing, Version, now, default);
        Assert.True(foreign.Uncertain);
        Assert.False(foreign.MayAutoRetry);
    }

    [Fact]
    public async Task UntrustedExternalVersionDoesNotContactListingOrReadback()
    {
        var (id, scope, signing, evidence, now) = Fixture();
        var key = ImmutableAttemptEvidence.ObjectKey(id);
        foreach (var version in new[] { "", "latest", "null",
            "../2026", "2026-11-21T13:04:02Z",
            "2026-11-21T13:04:02.0000000Z\n" })
        {
            var history = new FakeHistory();
            var reader = new StaticReader(new ImmutableAttemptObjectVersion(
                "ci-azure-store", key, Version,
                ImmutableAttemptImmutabilityMode.AzureLocked,
                now.AddDays(2), evidence));
            var outcome = await new AzureBlobWormHistoryAudit(
                history, reader, "ci-azure-store").AuditAsync(
                    id, "ci-tenant", "ci-owner", scope,
                    signing, version, now, default);
            Assert.True(outcome.Uncertain);
            Assert.Equal(0, history.Calls);
            Assert.Equal(0, reader.Calls);
        }
    }

    [Fact]
    public async Task AzureSdkListingIncludesVersionDeletedAndDeletedWithVersionsWithExactPrefix()
    {
        var (id, _, _, _, _) = Fixture();
        var key = ImmutableAttemptEvidence.ObjectKey(id);
        using var responder = new ScriptedAzureList(key, Version);
        var options = new BlobClientOptions
        {
            Transport = new HttpClientTransport(new HttpClient(responder))
        };
        options.Retry.MaxRetries = 0;
        var container = new BlobContainerClient(
            new Uri(ContainerUri), options);
        var history = new AzureBlobSdkVersionHistory(container);
        var page = await history.ListAsync(key, null, 2, default);

        Assert.Single(responder.Requests);
        Assert.Single(page.Entries);
        Assert.Equal(key, page.Entries[0].ObjectKey);
        Assert.Equal(Version, page.Entries[0].VersionId);
        Assert.Equal("next-page", page.NextCursor);
        var uri = responder.Requests[0];
        Assert.Equal("GET", uri.Method);
        Assert.Contains("comp=list", uri.Query);
        Assert.Contains("prefix=", uri.Query);
        Assert.Contains(Uri.EscapeDataString(key), uri.RawQuery);
        Assert.True(uri.Query.Contains("versions", StringComparison.OrdinalIgnoreCase),
            "Azure List must include versions: " + uri.Query);
        Assert.True(uri.Query.Contains("deleted", StringComparison.OrdinalIgnoreCase),
            "Azure List must include deleted: " + uri.Query);
        Assert.True(uri.Query.Contains("deletedwithversions", StringComparison.OrdinalIgnoreCase),
            "Azure List must include deletedwithversions: " + uri.Query);
        Assert.True(uri.Query.Contains("maxresults=2", StringComparison.OrdinalIgnoreCase),
            "Azure List must use bounded page size: " + uri.Query);
        Assert.True(uri.Query.Contains("snapshots", StringComparison.OrdinalIgnoreCase),
            "Azure List must include snapshots: " + uri.Query);
    }

    private static (Guid Id, byte[] Scope, byte[] Signing,
        byte[] Evidence, DateTimeOffset Now) Fixture()
    {
        var id = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var signing = RandomNumberGenerator.GetBytes(32);
        var evidence = ImmutableAttemptEvidence.CreateSynthetic(
            id, "ci-tenant", "ci-owner", scope, signing);
        return (id, scope, signing, evidence, DateTimeOffset.UtcNow);
    }

    private sealed class StaticReader(ImmutableAttemptObjectVersion data)
        : IImmutableAttemptObjectReader
    {
        public int Calls { get; private set; }
        public Task<ImmutableAttemptObjectVersion?> ReadVersionAsync(
            string objectKey, string versionId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<ImmutableAttemptObjectVersion?>(data);
        }
    }

    private sealed class FakeHistory(params AzureBlobHistoryPage[] pages)
        : IAzureBlobVersionHistory
    {
        public bool Denied { get; init; }
        public int Calls { get; private set; }
        public List<(string, string?, int)> Requested { get; } = [];

        public Task<AzureBlobHistoryPage> ListAsync(
            string objectKey, string? cursor, int pageSize, CancellationToken ct)
        {
            Requested.Add((objectKey, cursor, pageSize));
            if (Denied) throw new IOException("Synthetic listing denied.");
            return Task.FromResult(Calls < pages.Length
                ? pages[Calls++] : throw new IOException("Missing next page."));
        }
    }

    private sealed class ScriptedAzureList(string key, string version)
        : HttpMessageHandler
    {
        public List<(string Method, string Query, string RawQuery)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method.Method,
                Uri.UnescapeDataString(request.RequestUri!.Query),
                request.RequestUri.Query));
            var xml = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <EnumerationResults ServiceEndpoint="https://ci-account.blob.core.windows.net/" ContainerName="omnia-private">
                  <Prefix>{key}</Prefix>
                  <MaxResults>2</MaxResults>
                  <Blobs>
                    <Blob>
                      <Name>{key}</Name>
                      <Properties>
                        <Last-Modified>Sat, 21 Nov 2026 13:04:02 GMT</Last-Modified>
                        <Etag>"ci"</Etag>
                        <Content-Length>100</Content-Length>
                        <BlobType>BlockBlob</BlobType>
                      </Properties>
                      <VersionId>{version}</VersionId>
                      <IsCurrentVersion>true</IsCurrentVersion>
                      <Deleted>false</Deleted>
                    </Blob>
                  </Blobs>
                  <NextMarker>next-page</NextMarker>
                </EnumerationResults>
                """;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(xml)
            };
            response.Headers.TryAddWithoutValidation("x-ms-request-id", "ci-list");
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
            return Task.FromResult(response);
        }
    }
}
