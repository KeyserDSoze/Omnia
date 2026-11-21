using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Omnia.Infrastructure;
using Omnia.ObjectStore.S3;
using Xunit;

namespace Omnia.S3ObjectLock.Tests;

/// <summary>
/// Adversarial visibility audit: S3 ListVersions is NOT an independent
/// monotonic authority, even when every returned item is WORM-locked.
/// </summary>
public sealed class S3ObjectLockPinHistoryAuditTests
{
    [Fact]
    public async Task TwoBucketRealS3PinHasVisibleConflictAfterDeleteMarkerAndSecondComplianceVersion()
    {
        using var s3 = CiS3();
        var evidenceBucket = "omnia-ci-audit-evidence-" + Guid.NewGuid().ToString("N");
        var pinBucket = "omnia-ci-audit-pin-" + Guid.NewGuid().ToString("N");
        await CreateBucket(s3, evidenceBucket);
        await CreateBucket(s3, pinBucket);

        var requestId = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var evidenceKey = RandomNumberGenerator.GetBytes(32);
        var pinKey = RandomNumberGenerator.GetBytes(32);
        var now = DateTimeOffset.UtcNow;
        var evidenceReader = new S3ObjectLockAttemptReader(
            s3, evidenceBucket, "ci-audit-evidence");
        var evidence = await new S3ObjectLockAttemptPublisher(
            new AwsS3ObjectLockAttemptPut(s3, evidenceBucket),
            evidenceReader, "ci-audit-evidence")
            .SubmitOnceAsync(requestId, "ci-tenant", "ci-owner",
                scope, evidenceKey,
                ImmutableAttemptEvidence.CreateSynthetic(requestId,
                    "ci-tenant", "ci-owner", scope, evidenceKey),
                now.AddDays(2), now, default);
        Assert.True(evidence.Observed);

        var pinService = new S3ObjectLockVersionPinService(
            evidenceReader,
            new S3ObjectLockPinReader(s3, pinBucket, "ci-audit-pin"),
            new AwsS3ObjectLockAttemptPut(s3, pinBucket),
            "ci-audit-evidence", "ci-audit-pin");
        var pin = await pinService.PublishOnceAsync(
            requestId, "ci-tenant", "ci-owner", scope, evidenceKey,
            pinKey, evidence.VersionId!, DateTimeOffset.UtcNow, default);
        Assert.True(pin.PinObserved);

        var audit = new S3ObjectLockPinHistoryAudit(
            new AwsS3AttemptVersionHistory(s3, pinBucket), pinService);
        async Task<S3PinHistoryAuditResult> Inspect() =>
            await audit.AuditAsync(requestId, "ci-tenant", "ci-owner",
                scope, evidenceKey, pinKey, evidence.VersionId!,
                pin.PinVersionId!, DateTimeOffset.UtcNow, default);

        var clean = await Inspect();
        Assert.True(clean.SingleVisiblePinVerified);
        Assert.False(clean.Uncertain);
        Assert.False(clean.MayAutoRetry);
        Assert.False(clean.MayReleaseRecovery);
        Assert.False(clean.ProvesCompleteHistory);
        Assert.False(clean.ProvesSourceDeleteCommit);

        // COMPLIANCE preserves this *version*, not the current object key.
        await s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = pinBucket,
            Key = S3ObjectLockVersionPinService.PinKey(requestId)
        });
        Assert.True((await Inspect()).Uncertain);

        // A second authenticated COMPLIANCE version can follow a marker.
        // Neither one is a once-ever global fence.
        var signed = S3ObjectLockVersionPinService.CreateSignedSynthetic(
            requestId, "ci-tenant", "ci-owner", scope, evidenceKey, pinKey,
            "ci-audit-evidence", evidence.VersionId!);
        using var stream = new MemoryStream(signed);
        var second = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = pinBucket,
            Key = S3ObjectLockVersionPinService.PinKey(requestId),
            InputStream = stream,
            ContentType = "application/json",
            MD5Digest = Convert.ToBase64String(MD5.HashData(signed)),
            ObjectLockMode = ObjectLockMode.Compliance,
            ObjectLockRetainUntilDate = DateTime.UtcNow.AddDays(2)
        });
        Assert.NotEqual(pin.PinVersionId, second.VersionId);
        Assert.True((await Inspect()).Uncertain);

        // An exact old pin can remain cryptographically verifiable while
        // visible history is ambiguous. Never promote this to recovery-ready.
        Assert.True((await pinService.VerifyPinnedAsync(requestId,
            "ci-tenant", "ci-owner", scope, evidenceKey, pinKey,
            evidence.VersionId!, pin.PinVersionId!,
            DateTimeOffset.UtcNow, default)).PinObserved);
    }

    [Fact]
    public async Task ForeignMissingTruncatedAndDuplicateHistoriesFailBeforePinnedRead()
    {
        var id = Guid.NewGuid();
        var key = S3ObjectLockVersionPinService.PinKey(id);
        var scope = RandomNumberGenerator.GetBytes(32);
        var evidence = RandomNumberGenerator.GetBytes(32);
        var pin = RandomNumberGenerator.GetBytes(32);

        var pages = new[]
        {
            new S3AttemptHistoryPage([], false, null, null),
            new S3AttemptHistoryPage(
                [new S3AttemptHistoryEntry("foreign-key", "v1", false)],
                false, null, null),
            new S3AttemptHistoryPage(
                [new S3AttemptHistoryEntry(key, "latest", false)],
                false, null, null),
            new S3AttemptHistoryPage(
                [new S3AttemptHistoryEntry(key, "different", false)],
                false, null, null),
            new S3AttemptHistoryPage(
                [new S3AttemptHistoryEntry(key, "v1", true)],
                false, null, null),
            new S3AttemptHistoryPage(
                [new S3AttemptHistoryEntry(key, "v1", false)],
                true, null, null),
            new S3AttemptHistoryPage(
                [new S3AttemptHistoryEntry(key, "v1", false),
                 new S3AttemptHistoryEntry(key, "v1", false)],
                false, null, null),
        };
        foreach (var page in pages)
        {
            var reader = new NeverRead();
            var history = new RepeatPage(page);
            var pinService = new S3ObjectLockVersionPinService(
                reader, reader, new NeverWrite(), "evidence-store", "pin-store");
            var audit = new S3ObjectLockPinHistoryAudit(history, pinService);
            var result = await audit.AuditAsync(id, "ci-tenant",
                "ci-owner", scope, evidence, pin,
                "evidence-v1", "v1", DateTimeOffset.UtcNow, default);
            Assert.True(result.Uncertain);
            Assert.False(result.SingleVisiblePinVerified);
            Assert.False(result.MayAutoRetry);
            Assert.False(result.MayReleaseRecovery);
            Assert.Equal(0, reader.Calls);
        }
    }

    private sealed class RepeatPage(S3AttemptHistoryPage page)
        : IS3AttemptVersionHistory
    {
        public Task<S3AttemptHistoryPage> PageAsync(string key,
            string? keyMarker, string? versionMarker, int maxKeys,
            CancellationToken ct) => Task.FromResult(page);
    }

    private sealed class NeverRead : IImmutableAttemptObjectReader
    {
        public int Calls { get; private set; }
        public Task<ImmutableAttemptObjectVersion?> ReadVersionAsync(
            string key, string version, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Ambiguous pin must not be read.");
        }
    }

    private sealed class NeverWrite : IS3ObjectLockAttemptPut
    {
        public Task<S3ObjectLockPutAcknowledgment> PutOnceAsync(string key,
            byte[] bytes, DateTimeOffset retainUntil, CancellationToken ct) =>
            throw new InvalidOperationException("Audit must not write.");
    }

    private static async Task CreateBucket(AmazonS3Client s3, string bucket) =>
        await s3.PutBucketAsync(new PutBucketRequest
        {
            BucketName = bucket, ObjectLockEnabledForBucket = true
        });

    private static AmazonS3Client CiS3()
    {
        if (Environment.GetEnvironmentVariable("CI") != "true" ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("OMNIA_S3_CI_ONLY") != "1" ||
            Environment.GetEnvironmentVariable("OMNIA_TEST_S3_ENDPOINT")
                != "http://127.0.0.1:18433")
            throw new InvalidOperationException("Only synthetic SeaweedFS CI.");
        return new AmazonS3Client(new BasicAWSCredentials(
                Environment.GetEnvironmentVariable("OMNIA_TEST_S3_ACCESS")
                    ?? throw new InvalidOperationException("CI access missing"),
                Environment.GetEnvironmentVariable("OMNIA_TEST_S3_SECRET")
                    ?? throw new InvalidOperationException("CI secret missing")),
            new AmazonS3Config
            {
                ServiceURL = "http://127.0.0.1:18433",
                AuthenticationRegion = "us-east-1",
                ForcePathStyle = true, MaxErrorRetry = 0
            });
    }
}
