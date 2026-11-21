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
/// Test-first protocol and uncertain-ACK rehearsals. Never source DELETE,
/// attempt retry, legal erasure completion or recovery-ready.
/// </summary>
public sealed class S3ObjectLockAttemptSubmissionTests
{
    [Fact]
    public async Task RealConditionalCompliancePutHasPinnedReadbackAndRejectsSecondWrite()
    {
        using var s3 = CreateCiClient();
        var bucket = "omnia-ci-publish-" + Guid.NewGuid().ToString("N");
        await s3.PutBucketAsync(new PutBucketRequest
        {
            BucketName = bucket, ObjectLockEnabledForBucket = true
        });
        var requestId = Guid.NewGuid();
        var scopeKey = RandomNumberGenerator.GetBytes(32);
        var evidenceKey = RandomNumberGenerator.GetBytes(32);
        var payload = ImmutableAttemptEvidence.CreateSynthetic(
            requestId, "ci-s3-tenant", "ci-s3-actor",
            scopeKey, evidenceKey);
        var now = DateTimeOffset.UtcNow;
        var retain = now.AddDays(2);

        var producer = new S3ObjectLockAttemptPublisher(
            new AwsS3ObjectLockAttemptPut(s3, bucket),
            new S3ObjectLockAttemptReader(s3, bucket, "ci-s3-storage"),
            "ci-s3-storage");

        var result = await producer.SubmitOnceAsync(requestId,
            "ci-s3-tenant", "ci-s3-actor", scopeKey, evidenceKey,
            payload, retain, now, default);
        Assert.True(result.Observed);
        Assert.False(result.Uncertain);
        Assert.False(result.MayAutoRetry);
        Assert.False(result.MayReleaseRecovery);
        Assert.False(result.ProvesSourceDeleteCommit);
        Assert.False(string.IsNullOrWhiteSpace(result.VersionId));
        Assert.NotEqual("null", result.VersionId);
        var key = ImmutableAttemptEvidence.ObjectKey(requestId);
        var current = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket, Key = key,
            VersionId = result.VersionId
        });
        using (current)
            Assert.Equal(result.VersionId, current.VersionId);

        var second = await producer.SubmitOnceAsync(requestId,
            "ci-s3-tenant", "ci-s3-actor", scopeKey, evidenceKey,
            payload, retain, now, default);
        Assert.True(second.Uncertain);
        Assert.False(second.Observed);
        Assert.Null(second.VersionId);
        Assert.False(second.MayAutoRetry);
        // A duplicate request must not replace the first evidence version.
        var still = await ImmutableAttemptEvidence.VerifyAsync(
            new S3ObjectLockAttemptReader(s3, bucket, "ci-s3-storage"),
            requestId, "ci-s3-tenant", "ci-s3-actor", scopeKey,
            evidenceKey, "ci-s3-storage", result.VersionId!,
            DateTimeOffset.UtcNow, default);
        Assert.True(still.AttemptPreviouslyObserved);

        var denied = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            s3.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket, Key = key,
                VersionId = result.VersionId
            }));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task LostAcknowledgementOrUntrustedReadbackIsAlwaysUncertainWithoutRetry()
    {
        var id = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var sign = RandomNumberGenerator.GetBytes(32);
        var bytes = ImmutableAttemptEvidence.CreateSynthetic(
            id, "ci-tenant", "ci-actor", scope, sign);
        var key = ImmutableAttemptEvidence.ObjectKey(id);
        var retain = DateTimeOffset.UtcNow.AddDays(2);
        var now = DateTimeOffset.UtcNow;

        foreach (var putResult in new Func<Task<S3ObjectLockPutAcknowledgment>>[]
        {
            () => throw new IOException("response lost after possible commit"),
            () => throw new TimeoutException("possibly committed"),
            () => throw new AmazonS3Exception("precondition failed")
                { StatusCode = HttpStatusCode.PreconditionFailed },
            () => Task.FromResult(new S3ObjectLockPutAcknowledgment("null")),
            () => Task.FromResult(new S3ObjectLockPutAcknowledgment("version-1"))
        })
        {
            var writer = new StubPut(putResult);
            var reader = new StubRead(new ImmutableAttemptObjectVersion(
                "ci-store", key, "version-1",
                ImmutableAttemptImmutabilityMode.Governance, retain,
                bytes));
            var pub = new S3ObjectLockAttemptPublisher(
                writer, reader, "ci-store");
            var result = await pub.SubmitOnceAsync(id,
                "ci-tenant", "ci-actor", scope, sign,
                bytes, retain, now, default);
            Assert.False(result.Observed);
            Assert.True(result.Uncertain);
            Assert.False(result.MayAutoRetry);
            Assert.False(result.MayReleaseRecovery);
            Assert.Null(result.VersionId);
            Assert.Equal(1, writer.Calls);
            Assert.True(reader.Calls <= 1);
        }
    }

    [Fact]
    public async Task InvalidPayloadAndRetentionNeverReachS3()
    {
        var id = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var sign = RandomNumberGenerator.GetBytes(32);
        var bytes = ImmutableAttemptEvidence.CreateSynthetic(
            id, "ci-tenant", "ci-actor", scope, sign);
        var now = DateTimeOffset.UtcNow;
        var writer = new StubPut(() =>
            Task.FromResult(new S3ObjectLockPutAcknowledgment("v1")));
        var reader = new StubRead(null);
        var publisher = new S3ObjectLockAttemptPublisher(
            writer, reader, "ci-store");
        var tampered = bytes.ToArray();
        tampered[^2] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            publisher.SubmitOnceAsync(id, "ci-tenant", "ci-actor",
                scope, sign, tampered, now.AddDays(2), now, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            publisher.SubmitOnceAsync(id, "ci-tenant", "foreign",
                scope, sign, bytes, now.AddDays(2), now, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            publisher.SubmitOnceAsync(id, "ci-tenant", "ci-actor",
                scope, sign, bytes, now.AddMinutes(-1), now, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            publisher.SubmitOnceAsync(id, "ci-tenant", "ci-actor",
                scope, sign, bytes, now.AddMinutes(5), now, default));
        Assert.Equal(0, writer.Calls);
        Assert.Equal(0, reader.Calls);
    }

    private static AmazonS3Client CreateCiClient()
    {
        if (Environment.GetEnvironmentVariable("CI") != "true" ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("OMNIA_S3_CI_ONLY") != "1" ||
            Environment.GetEnvironmentVariable("OMNIA_TEST_S3_ENDPOINT")
                != "http://127.0.0.1:18433")
            throw new InvalidOperationException("Ephemeral S3 CI only.");
        var access = Environment.GetEnvironmentVariable("OMNIA_TEST_S3_ACCESS")
            ?? throw new InvalidOperationException("Missing ephemeral key.");
        var secret = Environment.GetEnvironmentVariable("OMNIA_TEST_S3_SECRET")
            ?? throw new InvalidOperationException("Missing ephemeral key.");
        return new AmazonS3Client(new BasicAWSCredentials(access, secret),
            new AmazonS3Config
            {
                ServiceURL = "http://127.0.0.1:18433",
                AuthenticationRegion = "us-east-1",
                ForcePathStyle = true,
                MaxErrorRetry = 0
            });
    }

    private sealed class StubPut(
        Func<Task<S3ObjectLockPutAcknowledgment>> operation)
        : IS3ObjectLockAttemptPut
    {
        public int Calls { get; private set; }

        public Task<S3ObjectLockPutAcknowledgment> PutOnceAsync(
            string objectKey, byte[] signedContent,
            DateTimeOffset retainUntilUtc, CancellationToken ct)
        {
            Calls++;
            return operation();
        }
    }

    private sealed class StubRead(ImmutableAttemptObjectVersion? response)
        : IImmutableAttemptObjectReader
    {
        public int Calls { get; private set; }

        public Task<ImmutableAttemptObjectVersion?> ReadVersionAsync(
            string objectKey, string versionId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(response);
        }
    }
}
