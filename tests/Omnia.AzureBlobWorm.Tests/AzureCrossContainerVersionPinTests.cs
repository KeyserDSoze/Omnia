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
/// Test-only scoped HMAC + scripted real SDK HTTP. Two container names
/// on the same CI account are NOT independently administered custody.
/// </summary>
public sealed class AzureCrossContainerVersionPinTests
{
    private const string EvidenceVersion = "2026-11-21T16:00:00.0000000Z";
    private const string PinVersion = "2026-11-21T16:00:01.0000000Z";
    private const string EvidenceStore = "ci-evidence-account";
    private const string PinStore = "ci-pin-account";
    private const string Uri = "https://ci.blob.core.windows.net/omnia-pins";

    [Fact]
    public async Task PinIsActorBoundAndVersionPinnedAcrossTwoLockedReaders()
    {
        var f = Fixture();
        var pinBytes = Signed(f);
        Assert.InRange(pinBytes.Length, 2, 1024);
        Assert.DoesNotContain("ci-tenant", Convert.ToBase64String(pinBytes));
        Assert.DoesNotContain("ci-owner", System.Text.Encoding.UTF8.GetString(pinBytes));
        var evidence = new FixedReader(Evidence(f));
        var pin = new FixedReader(Pin(f, pinBytes));
        var writer = new RecordingWriter(PinVersion);
        var service = new AzureBlobVersionPinService(evidence, pin, writer,
            EvidenceStore, PinStore);

        var result = await service.PublishOnceAsync(f.Id, "ci-tenant",
            "ci-owner", f.Scope, f.EvidenceKey, f.PinKey,
            EvidenceVersion, f.Now, default);

        Assert.True(result.PinObserved);
        Assert.False(result.Uncertain);
        Assert.Equal(EvidenceVersion, result.EvidenceVersionId);
        Assert.Equal(PinVersion, result.PinVersionId);
        Assert.False(result.MayAutoRetry);
        Assert.False(result.MayReleaseRecovery);
        Assert.False(result.ProvesMonotonicHistory);
        Assert.False(result.ProvesSourceDeleteCommit);
        Assert.Equal(1, writer.Calls);
        Assert.Equal(AzureBlobVersionPinService.PinKey(f.Id), writer.LastKey);
        Assert.Equal(f.Expiry, writer.LastRetention);
        Assert.Equal(pinBytes, writer.LastContent);
        Assert.Equal(2, evidence.Calls);
        Assert.Equal(1, pin.Calls);

        var checkedAgain = await service.VerifyPinnedAsync(f.Id,
            "ci-tenant", "ci-owner", f.Scope, f.EvidenceKey, f.PinKey,
            EvidenceVersion, PinVersion, f.Now, default);
        Assert.True(checkedAgain.PinObserved);
        Assert.False(checkedAgain.MayReleaseRecovery);
    }

    [Fact]
    public async Task WrongActorAndUntrustedEvidenceNeverReachThePinWriter()
    {
        var f = Fixture();
        var writer = new RecordingWriter(PinVersion);
        var expected = Signed(f);
        var evidence = new FixedReader(Evidence(f));
        var service = new AzureBlobVersionPinService(evidence,
            new FixedReader(Pin(f, expected)), writer, EvidenceStore, PinStore);

        var wrongActor = await service.PublishOnceAsync(f.Id, "ci-tenant",
            "foreign-owner", f.Scope, f.EvidenceKey, f.PinKey,
            EvidenceVersion, f.Now, default);
        Assert.True(wrongActor.Uncertain);
        Assert.Equal(0, writer.Calls);

        var wrongMode = new AzureBlobVersionPinService(
            new FixedReader(Evidence(f) with {
                ImmutabilityMode = ImmutableAttemptImmutabilityMode.S3Compliance }),
            new FixedReader(Pin(f, expected)), writer, EvidenceStore, PinStore);
        Assert.True((await wrongMode.PublishOnceAsync(f.Id,
            "ci-tenant", "ci-owner", f.Scope, f.EvidenceKey, f.PinKey,
            EvidenceVersion, f.Now, default)).Uncertain);

        var shortRetention = new AzureBlobVersionPinService(
            new FixedReader(Evidence(f) with { RetainUntilUtc = f.Now.AddMinutes(12) }),
            new FixedReader(Pin(f, expected)), writer, EvidenceStore, PinStore);
        Assert.True((await shortRetention.PublishOnceAsync(f.Id,
            "ci-tenant", "ci-owner", f.Scope, f.EvidenceKey, f.PinKey,
            EvidenceVersion, f.Now, default)).Uncertain);
        Assert.Equal(0, writer.Calls);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.PublishOnceAsync(f.Id, "ci-tenant", "ci-owner",
                f.Scope, f.EvidenceKey, f.EvidenceKey, EvidenceVersion,
                f.Now, default));
        Assert.Equal(0, writer.Calls);
        Assert.Throws<InvalidDataException>(() =>
            new AzureBlobVersionPinService(evidence,
                new FixedReader(Pin(f, expected)), writer,
                EvidenceStore, EvidenceStore));
    }

    [Fact]
    public async Task TamperedPinScopeVersionStoreAndRetentionFailClosed()
    {
        var f = Fixture();
        var signed = Signed(f);
        var bad = signed.ToArray();
        bad[^2] ^= 1;
        var variants = new[]
        {
            Pin(f, bad),
            Pin(f, signed) with { StoreIdentity = "other-store" },
            Pin(f, signed) with { VersionId = EvidenceVersion },
            Pin(f, signed) with { ObjectKey = "foreign.json" },
            Pin(f, signed) with { ImmutabilityMode = ImmutableAttemptImmutabilityMode.Unlocked },
            Pin(f, signed) with { RetainUntilUtc = f.Expiry.AddMinutes(-3) },
            Pin(f, signed) with { RetainUntilUtc = f.Now.AddMinutes(-1) },
            Pin(f, signed) with { Content = new byte[1025] },
        };
        foreach (var variant in variants)
        {
            var writer = new RecordingWriter(PinVersion);
            var service = new AzureBlobVersionPinService(
                new FixedReader(Evidence(f)), new FixedReader(variant),
                writer, EvidenceStore, PinStore);
            var result = await service.VerifyPinnedAsync(
                f.Id, "ci-tenant", "ci-owner", f.Scope, f.EvidenceKey,
                f.PinKey, EvidenceVersion, PinVersion, f.Now, default);
            Assert.True(result.Uncertain);
            Assert.False(result.MayAutoRetry);
            Assert.False(result.MayReleaseRecovery);
            Assert.Null(result.PinVersionId);
            Assert.Equal(0, writer.Calls);
        }

        var reader = new FixedReader(Pin(f, signed));
        var valid = new AzureBlobVersionPinService(
            new FixedReader(Evidence(f)), reader,
            new RecordingWriter(PinVersion), EvidenceStore, PinStore);
        foreach (var untrusted in new[] { "", "latest", "null",
            "2026-11-21T16:00:01Z", "2026-11-21T16:00:01.0000000Z\n" })
        {
            var result = await valid.VerifyPinnedAsync(f.Id,
                "ci-tenant", "ci-owner", f.Scope, f.EvidenceKey, f.PinKey,
                EvidenceVersion, untrusted, f.Now, default);
            Assert.True(result.Uncertain);
            Assert.False(result.MayAutoRetry);
        }
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task LostAckAfterConditionalPinCommitNeverTriggersSecondWrite()
    {
        var f = Fixture();
        var writer = new RecordingWriter(PinVersion) { LoseAckAfterCommit = true };
        var service = new AzureBlobVersionPinService(
            new FixedReader(Evidence(f)),
            new FixedReader(Pin(f, Signed(f))),
            writer, EvidenceStore, PinStore);
        var result = await service.PublishOnceAsync(f.Id, "ci-tenant",
            "ci-owner", f.Scope, f.EvidenceKey, f.PinKey,
            EvidenceVersion, f.Now, default);
        Assert.True(writer.SyntheticCommitted);
        Assert.True(result.Uncertain);
        Assert.Null(result.PinVersionId);
        Assert.False(result.MayAutoRetry);
        Assert.False(result.MayReleaseRecovery);
        Assert.Equal(1, writer.Calls);

        // A separate, externally supplied PIN VersionId can be checked
        // later. It is NOT a safe replay instruction.
        var checkedAgain = await service.VerifyPinnedAsync(f.Id,
            "ci-tenant", "ci-owner", f.Scope, f.EvidenceKey, f.PinKey,
            EvidenceVersion, PinVersion, f.Now, default);
        Assert.True(checkedAgain.PinObserved);
        Assert.False(checkedAgain.MayAutoRetry);
    }

    [Fact]
    public async Task SdkPinWriterUploadsConditionallyLocksExactVersionAndReadsBack()
    {
        var f = Fixture();
        using var script = new PinSdkScript(PinVersion, f.Expiry);
        var options = Options(script);
        var writer = new AzureBlobWormPinSdkWriter(new Uri(Uri), options);
        var reader = new AzureBlobWormPinReader(
            new BlobContainerClient(new Uri(Uri), options), PinStore);
        var payload = Signed(f);

        var ack = await writer.UploadAndLockOnceAsync(
            AzureBlobVersionPinService.PinKey(f.Id), payload,
            f.Expiry, default);
        Assert.Equal(PinVersion, ack.VersionId);
        var read = await reader.ReadVersionAsync(
            AzureBlobVersionPinService.PinKey(f.Id), PinVersion, default);
        Assert.NotNull(read);
        Assert.Equal(payload, read.Content);
        Assert.Equal(PinVersion, read.VersionId);
        Assert.Equal(ImmutableAttemptImmutabilityMode.AzureLocked,
            read.ImmutabilityMode);
        Assert.InRange(read.RetainUntilUtc,
            f.Expiry.AddSeconds(-1), f.Expiry.AddSeconds(1));
        Assert.Equal(["PUT", "PUT", "HEAD", "GET"],
            script.Calls.Select(x => x.Method).ToArray());
        Assert.Equal("*", script.Calls[0].IfNoneMatch);
        Assert.DoesNotContain("versionid=", script.Calls[0].Query,
            StringComparison.OrdinalIgnoreCase);
        Assert.All(script.Calls.Skip(1), c =>
            Assert.Contains(PinVersion, c.Query, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("locked", script.Calls[1].Mode, ignoreCase: true);
    }

    [Fact]
    public async Task SdkPinLock403OrLostAckIsUncertainAndNeverRetries()
    {
        var f = Fixture();
        foreach (var failure in new[] { "deny-lock", "lost-ack" })
        {
            using var script = new PinSdkScript(PinVersion, f.Expiry) {
                Failure = failure
            };
            var options = Options(script);
            var writer = new AzureBlobWormPinSdkWriter(new Uri(Uri), options);
            var publisher = new AzureBlobVersionPinService(
                new FixedReader(Evidence(f)),
                new AzureBlobWormPinReader(
                    new BlobContainerClient(new Uri(Uri), options), PinStore),
                writer, EvidenceStore, PinStore);
            var result = await publisher.PublishOnceAsync(f.Id,
                "ci-tenant", "ci-owner", f.Scope, f.EvidenceKey, f.PinKey,
                EvidenceVersion, f.Now, default);
            Assert.True(result.Uncertain);
            Assert.False(result.MayAutoRetry);
            Assert.False(result.MayReleaseRecovery);
            Assert.Null(result.PinVersionId);
            Assert.Equal(1, script.Calls.Count(c =>
                c.Method == "PUT" && !c.Query.Contains("immutabilityPolicies")));
        }
    }

    [Fact]
    public void PinSdkRefusesInsecureTransportAndHiddenRetries()
    {
        var options = new BlobClientOptions();
        options.Retry.MaxRetries = 2;
        Assert.Throws<InvalidDataException>(() =>
            new AzureBlobWormPinSdkWriter(new Uri(Uri), options));
        options.Retry.MaxRetries = 0;
        Assert.Throws<InvalidDataException>(() =>
            new AzureBlobWormPinSdkWriter(new Uri(
                "http://ci.blob.core.windows.net/omnia-pins"), options));
        Assert.Throws<InvalidDataException>(() =>
            new AzureBlobWormPinReader(
                new BlobContainerClient(new Uri(
                    "http://ci.blob.core.windows.net/omnia-pins"), options),
                PinStore));
    }

    private static FixtureData Fixture()
    {
        var id = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var evidence = RandomNumberGenerator.GetBytes(32);
        var pin = RandomNumberGenerator.GetBytes(32);
        var now = DateTimeOffset.UtcNow;
        var payload = ImmutableAttemptEvidence.CreateSynthetic(
            id, "ci-tenant", "ci-owner", scope, evidence);
        return new FixtureData(id, scope, evidence, pin, now,
            now.AddDays(3), payload);
    }

    private static byte[] Signed(FixtureData f) =>
        AzureBlobVersionPinService.CreateSignedSynthetic(f.Id,
            "ci-tenant", "ci-owner", f.Scope, f.EvidenceKey,
            f.PinKey, EvidenceStore, EvidenceVersion);

    private static ImmutableAttemptObjectVersion Evidence(FixtureData f) =>
        new(EvidenceStore, ImmutableAttemptEvidence.ObjectKey(f.Id),
            EvidenceVersion, ImmutableAttemptImmutabilityMode.AzureLocked,
            f.Expiry, f.Payload);

    private static ImmutableAttemptObjectVersion Pin(
        FixtureData f, byte[] content) =>
        new(PinStore, AzureBlobVersionPinService.PinKey(f.Id), PinVersion,
            ImmutableAttemptImmutabilityMode.AzureLocked, f.Expiry, content);

    private sealed record FixtureData(Guid Id, byte[] Scope,
        byte[] EvidenceKey, byte[] PinKey, DateTimeOffset Now,
        DateTimeOffset Expiry, byte[] Payload);

    private sealed class FixedReader(ImmutableAttemptObjectVersion value)
        : IImmutableAttemptObjectReader
    {
        public int Calls { get; private set; }
        public Task<ImmutableAttemptObjectVersion?> ReadVersionAsync(
            string key, string version, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<ImmutableAttemptObjectVersion?>(value);
        }
    }

    private sealed class RecordingWriter(string version)
        : IAzureBlobWormPinWrite
    {
        public bool LoseAckAfterCommit { get; init; }
        public bool SyntheticCommitted { get; private set; }
        public int Calls { get; private set; }
        public string? LastKey { get; private set; }
        public byte[]? LastContent { get; private set; }
        public DateTimeOffset LastRetention { get; private set; }

        public Task<AzureBlobWormWriteAck> UploadAndLockOnceAsync(
            string key, byte[] content, DateTimeOffset retention,
            CancellationToken ct)
        {
            Calls++;
            LastKey = key;
            LastContent = content.ToArray();
            LastRetention = retention;
            SyntheticCommitted = true;
            if (LoseAckAfterCommit)
                throw new IOException("Synthetic pin uploaded but its ACK was lost.");
            return Task.FromResult(new AzureBlobWormWriteAck(version));
        }
    }

    private static BlobClientOptions Options(HttpMessageHandler handler)
    {
        var options = new BlobClientOptions {
            Transport = new HttpClientTransport(new HttpClient(handler))
        };
        options.Retry.MaxRetries = 0;
        return options;
    }

    private sealed class PinSdkScript(string version, DateTimeOffset expiry)
        : HttpMessageHandler
    {
        public string? Failure { get; init; }
        public List<(string Method, string Query, string? IfNoneMatch,
            string? Mode)> Calls { get; } = [];
        private byte[] _payload = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var query = Uri.UnescapeDataString(uri.Query);
            var isPolicy = query.Contains("immutabilityPolicies",
                StringComparison.OrdinalIgnoreCase);
            Calls.Add((request.Method.Method, query,
                request.Headers.TryGetValues("If-None-Match", out var match)
                    ? match.FirstOrDefault() : null,
                request.Headers.TryGetValues("x-ms-immutability-policy-mode",
                    out var modes) ? modes.FirstOrDefault() : null));
            if (request.Method == HttpMethod.Put && !isPolicy)
            {
                _payload = await request.Content!.ReadAsByteArrayAsync(ct);
                if (Failure == "lost-ack")
                    throw new IOException("Synthetic lost PUT ACK.");
            }
            var status = isPolicy && Failure == "deny-lock"
                ? HttpStatusCode.Forbidden :
                request.Method == HttpMethod.Put && !isPolicy
                    ? HttpStatusCode.Created : HttpStatusCode.OK;
            var response = new HttpResponseMessage(status) {
                RequestMessage = request,
                Content = new ByteArrayContent(
                    request.Method == HttpMethod.Get ? _payload : [])
            };
            response.Headers.TryAddWithoutValidation("x-ms-request-id", "ci-pin");
            response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
            if (!isPolicy)
                response.Headers.TryAddWithoutValidation("x-ms-version-id", version);
            response.Headers.ETag =
                new System.Net.Http.Headers.EntityTagHeaderValue("\"ci-pin-etag\"");
            response.Content.Headers.ContentLength =
                request.Method is HttpMethod.Get or HttpMethod.Head
                    ? _payload.LongLength : 0;
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
            if (isPolicy || request.Method != HttpMethod.Put)
            {
                response.Headers.TryAddWithoutValidation(
                    "x-ms-immutability-policy-mode", "locked");
                response.Headers.TryAddWithoutValidation(
                    "x-ms-immutability-policy-until-date",
                    expiry.ToUniversalTime().ToString("R",
                        CultureInfo.InvariantCulture));
            }
            return response;
        }
    }
}
