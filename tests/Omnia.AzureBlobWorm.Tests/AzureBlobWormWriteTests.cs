using System.Globalization;
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
/// Scripted HTTPS Azure SDK protocol, NOT Azure WORM service or offsite proof.
/// </summary>
public sealed class AzureBlobWormWriteTests
{
    private const string Version = "2026-11-21T14:00:00.0000000Z";
    private const string ContainerUri =
        "https://ci-account.blob.core.windows.net/omnia-private";

    [Fact]
    public async Task ConditionalPutThenExactVersionLockedPolicyAndPinnedReadback()
    {
        var id = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var signing = RandomNumberGenerator.GetBytes(32);
        var content = ImmutableAttemptEvidence.CreateSynthetic(
            id, "ci-tenant", "ci-owner", scope, signing);
        var now = new DateTimeOffset(2026, 11, 21, 13, 0, 0, TimeSpan.Zero);
        var retention = now.AddDays(3);
        using var handler = new AzureWormScript(Version, content, retention);
        var options = MakeOptions(handler);
        var writer = new AzureBlobWormAttemptSdkWriter(
            new Uri(ContainerUri), options);
        var reader = new AzureBlobWormAttemptReader(
            new BlobContainerClient(new Uri(ContainerUri), options),
            "ci-azure-worm-store");
        var publisher = new AzureBlobWormAttemptPublisher(
            writer, reader, "ci-azure-worm-store");

        var result = await publisher.SubmitOnceAsync(id, "ci-tenant",
            "ci-owner", scope, signing, content, retention, now, default);

        Assert.True(result.Observed);
        Assert.Equal(Version, result.VersionId);
        Assert.False(result.MayAutoRetry);
        Assert.False(result.MayReleaseRecovery);
        Assert.False(result.ProvesSourceDeleteCommit);
        Assert.Equal(["PUT", "PUT", "HEAD", "GET"],
            handler.Requests.Select(r => r.Method).ToArray());
        Assert.All(handler.Requests, r =>
            Assert.EndsWith(ImmutableAttemptEvidence.ObjectKey(id),
                r.Path, StringComparison.Ordinal));
        Assert.Equal("*", handler.Requests[0].IfNoneMatch);
        Assert.DoesNotContain("versionid=", handler.Requests[0].Query,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("comp=immutabilityPolicies",
            handler.Requests[1].Query, StringComparison.OrdinalIgnoreCase);
        Assert.All(handler.Requests.Skip(1), req =>
            Assert.Contains("versionid=", req.Query,
                StringComparison.OrdinalIgnoreCase));
        Assert.All(handler.Requests.Skip(1), req =>
            Assert.Contains(Version, Uri.UnescapeDataString(req.Query)));
        Assert.Equal("locked", handler.Requests[1].PolicyMode,
            ignoreCase: true);
    }

    [Fact]
    public async Task LostUploadAckOrLockFailureIsUncertainWithoutRetry()
    {
        var id = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var signing = RandomNumberGenerator.GetBytes(32);
        var content = ImmutableAttemptEvidence.CreateSynthetic(
            id, "ci-tenant", "ci-owner", scope, signing);
        var now = new DateTimeOffset(2026, 11, 21, 13, 0, 0, TimeSpan.Zero);
        foreach (var failure in new[] { "lost-upload-ack", "deny-lock", "missing-version", "unlocked-readback", "short-retention", "different-version" })
        {
            using var handler = new AzureWormScript(
                Version, content, now.AddDays(2)) { Failure = failure };
            var options = MakeOptions(handler);
            var writer = new AzureBlobWormAttemptSdkWriter(
                new Uri(ContainerUri), options);
            var reader = new AzureBlobWormAttemptReader(
                new BlobContainerClient(new Uri(ContainerUri), options),
                "ci-azure-worm-store");
            var publisher = new AzureBlobWormAttemptPublisher(
                writer, reader, "ci-azure-worm-store");

            var result = await publisher.SubmitOnceAsync(id, "ci-tenant",
                "ci-owner", scope, signing, content, now.AddDays(2),
                now, default);
            Assert.True(result.Uncertain);
            Assert.False(result.Observed);
            Assert.False(result.MayAutoRetry);
            Assert.False(result.MayReleaseRecovery);
            Assert.Null(result.VersionId);
            Assert.Equal(1, handler.Requests.Count(r =>
                r.Method == "PUT" && !r.Query.Contains("immutabilityPolicies")));
            Assert.InRange(handler.Requests.Count, 1, 4);
        }
    }

    [Fact]
    public async Task WrongActorInvalidPayloadAndShortRetentionDoNotCallWriter()
    {
        var id = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var signing = RandomNumberGenerator.GetBytes(32);
        var content = ImmutableAttemptEvidence.CreateSynthetic(
            id, "ci-tenant", "ci-owner", scope, signing);
        var writer = new NeverWrite();
        var publisher = new AzureBlobWormAttemptPublisher(
            writer, new NeverRead(), "ci-azure-worm-store");
        var now = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            publisher.SubmitOnceAsync(id, "ci-tenant", "foreign",
                scope, signing, content, now.AddDays(2), now, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            publisher.SubmitOnceAsync(id, "ci-tenant", "ci-owner",
                scope, signing, content, now.AddMinutes(15), now, default));
        var altered = content.ToArray();
        altered[^2] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            publisher.SubmitOnceAsync(id, "ci-tenant", "ci-owner",
                scope, signing, altered, now.AddDays(2), now, default));
        Assert.Equal(0, writer.Calls);
    }

    [Fact]
    public void SdkWriterRejectsHiddenRetriesAndInsecureUri()
    {
        var options = new BlobClientOptions();
        options.Retry.MaxRetries = 2;
        Assert.Throws<InvalidDataException>(() =>
            new AzureBlobWormAttemptSdkWriter(new Uri(ContainerUri), options));
        options.Retry.MaxRetries = 0;
        Assert.Throws<InvalidDataException>(() =>
            new AzureBlobWormAttemptSdkWriter(
                new Uri("http://ci-account.blob.core.windows.net/omnia-private"),
                options));
    }

    private static BlobClientOptions MakeOptions(HttpMessageHandler handler)
    {
        var options = new BlobClientOptions
        {
            Transport = new HttpClientTransport(new HttpClient(handler))
        };
        options.Retry.MaxRetries = 0;
        return options;
    }

    private sealed class NeverRead : IImmutableAttemptObjectReader
    {
        public Task<ImmutableAttemptObjectVersion?> ReadVersionAsync(
            string key, string version, CancellationToken ct) =>
            throw new InvalidOperationException("No valid receipt to read.");
    }

    private sealed class NeverWrite : IAzureBlobWormAttemptWrite
    {
        public int Calls { get; private set; }
        public Task<AzureBlobWormWriteAck> UploadAndLockOnceAsync(
            string objectKey, byte[] content, DateTimeOffset retention,
            CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Invalid input reached writer.");
        }
    }

    private sealed class AzureWormScript(
        string version, byte[] content, DateTimeOffset expiry)
        : HttpMessageHandler
    {
        public string? Failure { get; init; }
        public List<(string Method, string Path, string Query,
            string? IfNoneMatch, string? PolicyMode)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var query = Uri.UnescapeDataString(
                request.RequestUri!.Query.TrimStart('?'));
            var isLock = query.Contains("immutabilityPolicies",
                StringComparison.OrdinalIgnoreCase);
            Requests.Add((request.Method.Method,
                Uri.UnescapeDataString(request.RequestUri.AbsolutePath), query,
                request.Headers.TryGetValues("If-None-Match", out var matches)
                    ? matches.FirstOrDefault() : null,
                request.Headers.TryGetValues("x-ms-immutability-policy-mode",
                    out var modes) ? modes.FirstOrDefault() : null));

            if (Failure == "lost-upload-ack" &&
                request.Method == HttpMethod.Put && !isLock)
                throw new IOException("Synthetic PUT ack lost after commit.");

            var status = isLock && Failure == "deny-lock"
                ? HttpStatusCode.Forbidden : HttpStatusCode.OK;
            var response = new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(
                    request.Method == HttpMethod.Get ? content : [])
            };
            response.Headers.TryAddWithoutValidation("x-ms-request-id", "ci-worm");
            if (request.Method != HttpMethod.Put || !isLock)
                response.Headers.TryAddWithoutValidation("x-ms-version-id",
                    Failure == "missing-version" &&
                    request.Method == HttpMethod.Put ? null :
                    Failure == "different-version" &&
                    request.Method == HttpMethod.Head
                        ? "2026-11-21T15:00:00.0000000Z" : version);
            response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
            response.Headers.ETag =
                new System.Net.Http.Headers.EntityTagHeaderValue("\"ci-etag\"");
            response.Content.Headers.ContentLength =
                request.Method == HttpMethod.Get || request.Method == HttpMethod.Head
                    ? content.LongLength : 0;
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            response.Content.Headers.LastModified =
                new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
            if (isLock || request.Method != HttpMethod.Put)
            {
                response.Headers.TryAddWithoutValidation(
                    "x-ms-immutability-policy-mode",
                    Failure == "unlocked-readback" &&
                    request.Method != HttpMethod.Put ? "unlocked" : "locked");
                response.Headers.TryAddWithoutValidation(
                    "x-ms-immutability-policy-until-date",
                    (Failure == "short-retention" &&
                     request.Method != HttpMethod.Put
                        ? expiry.AddDays(-1) : expiry)
                    .ToUniversalTime().ToString("R", CultureInfo.InvariantCulture));
            }
            return Task.FromResult(response);
        }
    }
}
