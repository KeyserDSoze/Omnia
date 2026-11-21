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
/// Exercises REAL Azure.Storage.Blobs HTTP pipeline, with a SCRIPTED
/// responder. This does NOT exercise real Azure WORM/identity/retention.
/// </summary>
public sealed class AzureBlobVersionReaderTests
{
    [Fact]
    public async Task LockedVersionHasPinnedHeadAndGetWithNoRecoveryPrivilege()
    {
        var id = Guid.NewGuid();
        var key = RandomNumberGenerator.GetBytes(32);
        var mac = RandomNumberGenerator.GetBytes(32);
        var bytes = ImmutableAttemptEvidence.CreateSynthetic(
            id, "ci-tenant", "ci-owner", key, mac);
        var version = "2026-11-21T08:12:32.0000000Z";
        using var http = new FakeAzureBlobHttp(version, bytes);
        var reader = MakeReader(http);
        var now = new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);
        var result = await ImmutableAttemptEvidence.VerifyAsync(
            reader, id, "ci-tenant", "ci-owner", key, mac,
            "ci-azure-external-store", version, now, default);

        Assert.True(result.AttemptPreviouslyObserved);
        Assert.False(result.MayReleaseRecovery);
        Assert.False(result.MayAutoReplay);
        Assert.False(result.ProvesSourceDeleteCommit);
        Assert.Equal(version, result.VersionId);
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal(["HEAD", "GET"], http.Requests.Select(r => r.Method));
        Assert.All(http.Requests, req =>
        {
            Assert.Equal(ImmutableAttemptEvidence.ObjectKey(id),
                Uri.UnescapeDataString(req.Path).Split("/omnia-private/")[1]);
            Assert.Contains("versionid=", req.Query, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(version, Uri.UnescapeDataString(
                req.Query.Split('=', 2)[1]));
        });
    }

    [Fact]
    public async Task UnlockedMissingExpiredOrForeignVersionsFailClosed()
    {
        var id = Guid.NewGuid();
        var key = RandomNumberGenerator.GetBytes(32);
        var mac = RandomNumberGenerator.GetBytes(32);
        var bytes = ImmutableAttemptEvidence.CreateSynthetic(
            id, "ci-tenant", "ci-owner", key, mac);
        var version = "2026-11-21T08:12:32.0000000Z";
        var now = new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

        async Task Denied(string? mode = "locked", string? observedVersion = null,
            DateTimeOffset? retainUntil = null, byte[]? data = null,
            HttpStatusCode status = HttpStatusCode.OK)
        {
            using var http = new FakeAzureBlobHttp(
                observedVersion ?? version, data ?? bytes)
            {
                PolicyMode = mode,
                RetainUntil = retainUntil ?? now.AddHours(2),
                Status = status
            };
            var reader = MakeReader(http);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ImmutableAttemptEvidence.VerifyAsync(reader, id,
                    "ci-tenant", "ci-owner", key, mac,
                    "ci-azure-external-store", version, now, default));
        }

        await Denied(mode: "unlocked");
        await Denied(mode: null);
        await Denied(mode: "unknown");
        await Denied(retainUntil: now.AddHours(-1));
        await Denied(observedVersion: "2026-11-21T08:12:33.0000000Z");
        await Denied(data: [.. bytes, (byte)' ']);
        await Denied(data: new byte[ImmutableAttemptEvidence.MaxBytes + 1]);
        await Denied(status: HttpStatusCode.Forbidden);
        await Denied(status: HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ResponseVersionOrEtagSwitchBetweenHeadAndGetIsDenied()
    {
        var id = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var signing = RandomNumberGenerator.GetBytes(32);
        var data = ImmutableAttemptEvidence.CreateSynthetic(
            id, "ci-tenant", "ci-owner", scope, signing);
        const string version = "2026-11-21T08:12:32.0000000Z";
        var now = new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

        foreach (var mismatch in new[] { "version", "etag", "length" })
        {
            using var http = new FakeAzureBlobHttp(version, data)
            {
                DownloadVersion = mismatch == "version"
                    ? "2026-11-21T08:12:33.0000000Z" : null,
                DownloadEtag = mismatch == "etag" ? "\"foreign-etag\"" : null,
                DeclaredDownloadLength = mismatch == "length"
                    ? data.LongLength + 1 : null
            };
            var reader = MakeReader(http);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ImmutableAttemptEvidence.VerifyAsync(
                    reader, id, "ci-tenant", "ci-owner",
                    scope, signing, "ci-azure-external-store",
                    version, now, default));
            Assert.Equal(2, http.Requests.Count);
        }
    }

    [Fact]
    public async Task RejectsLatestNullForeignPathsAndWrongActors()
    {
        var id = Guid.NewGuid();
        var key = RandomNumberGenerator.GetBytes(32);
        var mac = RandomNumberGenerator.GetBytes(32);
        var data = ImmutableAttemptEvidence.CreateSynthetic(
            id, "ci-tenant", "ci-owner", key, mac);
        var version = "2026-11-21T08:12:32.0000000Z";
        using var http = new FakeAzureBlobHttp(version, data);
        var reader = MakeReader(http);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadVersionAsync("../other-tenant.json", version, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadVersionAsync(ImmutableAttemptEvidence.ObjectKey(id),
                "latest", default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadVersionAsync(ImmutableAttemptEvidence.ObjectKey(id),
                "null", default));
        Assert.Empty(http.Requests);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ImmutableAttemptEvidence.VerifyAsync(reader, id,
                "ci-tenant", "foreign", key, mac,
                "ci-azure-external-store", version,
                DateTimeOffset.UtcNow, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ImmutableAttemptEvidence.VerifyAsync(reader, id,
                "ci-tenant", "ci-owner", key, mac,
                "foreign-storage", version,
                DateTimeOffset.UtcNow, default));
    }

    private static AzureBlobWormAttemptReader MakeReader(FakeAzureBlobHttp handler)
    {
        var options = new BlobClientOptions
        {
            Transport = new HttpClientTransport(new HttpClient(handler))
        };
        options.Retry.MaxRetries = 0;
        var container = new BlobContainerClient(
            new Uri("https://ci-account.blob.core.windows.net/omnia-private"),
            options);
        return new AzureBlobWormAttemptReader(
            container, "ci-azure-external-store");
    }

    private sealed class FakeAzureBlobHttp(string version, byte[] body)
        : HttpMessageHandler
    {
        public string? DownloadVersion { get; set; }
        public string? DownloadEtag { get; set; }
        public long? DeclaredDownloadLength { get; set; }
        public string? PolicyMode { get; set; } = "locked";
        public DateTimeOffset RetainUntil { get; set; } =
            new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<(string Method, string Path, string Query)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath,
                request.RequestUri.Query.TrimStart('?')));
            var response = new HttpResponseMessage(Status)
            {
                RequestMessage = request
            };
            if (Status == HttpStatusCode.OK)
            {
                response.Headers.TryAddWithoutValidation("x-ms-request-id", "ci");
                response.Headers.TryAddWithoutValidation("x-ms-version-id",
                    request.Method == HttpMethod.Head
                        ? version : DownloadVersion ?? version);
                response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(
                    request.Method == HttpMethod.Head
                        ? "\"etag-ci\"" : DownloadEtag ?? "\"etag-ci\"");
                response.Content = new ByteArrayContent(
                    request.Method == HttpMethod.Head ? [] : body);
                response.Content.Headers.ContentLength =
                    request.Method == HttpMethod.Head
                        ? body.LongLength : DeclaredDownloadLength ?? body.LongLength;
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                if (PolicyMode is not null)
                {
                    response.Headers.TryAddWithoutValidation(
                        "x-ms-immutability-policy-mode", PolicyMode);
                    response.Headers.TryAddWithoutValidation(
                        "x-ms-immutability-policy-until-date",
                        RetainUntil.ToUniversalTime().ToString(
                            "R", System.Globalization.CultureInfo.InvariantCulture));
                }
                response.Content.Headers.LastModified =
                    new DateTimeOffset(2026, 11, 21, 7, 0, 0, TimeSpan.Zero);
            }
            return Task.FromResult(response);
        }
    }
}
