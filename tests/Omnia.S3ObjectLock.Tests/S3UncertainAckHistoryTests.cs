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
/// Tests only synthetic CI data. Even a previously observed S3 version
/// cannot authorize a DELETE, retry, replay or release after PITR.
/// </summary>
public sealed class S3UncertainAckHistoryTests
{
    [Fact]
    public async Task RealServerCommittedPutWithLostAckCanBeObservedAfterLatestDeleteMarker()
    {
        using var s3 = CiS3();
        var bucket = "omnia-ci-ackhistory-" + Guid.NewGuid().ToString("N");
        await s3.PutBucketAsync(new PutBucketRequest
        {
            BucketName = bucket, ObjectLockEnabledForBucket = true
        });
        var id = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var evidence = RandomNumberGenerator.GetBytes(32);
        var content = ImmutableAttemptEvidence.CreateSynthetic(
            id, "ci-tenant", "ci-actor", scope, evidence);
        const string store = "ci-s3-evidence-store";
        var reader = new S3ObjectLockAttemptReader(s3, bucket, store);
        var pub = new S3ObjectLockAttemptPublisher(
            new LoseAckAfterActualPut(new AwsS3ObjectLockAttemptPut(
                s3, bucket)), reader, store);
        var result = await pub.SubmitOnceAsync(id, "ci-tenant", "ci-actor",
            scope, evidence, content, DateTimeOffset.UtcNow.AddDays(2),
            DateTimeOffset.UtcNow, default);
        Assert.True(result.Uncertain);
        Assert.False(result.Observed);
        Assert.False(result.MayAutoRetry);
        Assert.Null(result.VersionId);

        var reconcile = new S3ObjectLockAttemptHistoryReconciler(
            new AwsS3AttemptVersionHistory(s3, bucket), reader, store);
        var first = await reconcile.ReconcileAsync(id, "ci-tenant", "ci-actor",
            scope, evidence, DateTimeOffset.UtcNow, default);
        Assert.True(first.PreviouslyObserved);
        Assert.False(first.Uncertain);
        Assert.NotNull(first.VersionId);
        Assert.False(first.MayAutoRetry);
        Assert.False(first.MayReleaseRecovery);
        Assert.False(first.ProvesSourceDeleteCommit);

        // An unversioned DELETE inserts a mutable latest delete marker
        // but must not destroy the retained COMPLIANCE version.
        await s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = bucket, Key = ImmutableAttemptEvidence.ObjectKey(id)
        });
        var afterMarker = await reconcile.ReconcileAsync(id,
            "ci-tenant", "ci-actor", scope, evidence,
            DateTimeOffset.UtcNow, default);
        Assert.True(afterMarker.PreviouslyObserved);
        Assert.Equal(first.VersionId, afterMarker.VersionId);
        Assert.False(afterMarker.MayAutoRetry);
        Assert.False(afterMarker.MayReleaseRecovery);
    }

    [Fact]
    public async Task MissingForeignAndDuplicateSignedVersionsAreNeverProofOfAbsence()
    {
        using var s3 = CiS3();
        var bucket = "omnia-ci-history-dup-" + Guid.NewGuid().ToString("N");
        await s3.PutBucketAsync(new PutBucketRequest
        {
            BucketName = bucket, ObjectLockEnabledForBucket = true
        });
        var id = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var sign = RandomNumberGenerator.GetBytes(32);
        var bytes = ImmutableAttemptEvidence.CreateSynthetic(
            id, "ci-tenant", "ci-actor", scope, sign);
        const string store = "ci-s3-evidence-store";
        var reader = new S3ObjectLockAttemptReader(s3, bucket, store);
        var reconciler = new S3ObjectLockAttemptHistoryReconciler(
            new AwsS3AttemptVersionHistory(s3, bucket), reader, store);

        var missing = await reconciler.ReconcileAsync(id,
            "ci-tenant", "ci-actor", scope, sign,
            DateTimeOffset.UtcNow, default);
        Assert.True(missing.Uncertain);
        Assert.False(missing.PreviouslyObserved);
        Assert.False(missing.MayAutoRetry);

        var writer = new AwsS3ObjectLockAttemptPut(s3, bucket);
        var first = await writer.PutOnceAsync(
            ImmutableAttemptEvidence.ObjectKey(id), bytes,
            DateTimeOffset.UtcNow.AddDays(2), default);
        Assert.False(string.IsNullOrWhiteSpace(first.VersionId));
        var wrongActor = await reconciler.ReconcileAsync(id,
            "ci-tenant", "foreign-actor", scope, sign,
            DateTimeOffset.UtcNow, default);
        Assert.True(wrongActor.Uncertain);
        Assert.Null(wrongActor.VersionId);

        // Test admin forces a second retained version bypassing the
        // normal one-shot writer. The reconciler must reject ambiguity.
        using var stream = new MemoryStream(bytes);
        var second = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = ImmutableAttemptEvidence.ObjectKey(id),
            InputStream = stream, ContentType = "application/json",
            MD5Digest = Convert.ToBase64String(MD5.HashData(bytes)),
            ObjectLockMode = ObjectLockMode.Compliance,
            ObjectLockRetainUntilDate = DateTime.UtcNow.AddDays(2)
        });
        Assert.NotEqual(first.VersionId, second.VersionId);
        await s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = bucket, Key = ImmutableAttemptEvidence.ObjectKey(id)
        });

        var ambiguous = await reconciler.ReconcileAsync(id,
            "ci-tenant", "ci-actor", scope, sign,
            DateTimeOffset.UtcNow, default);
        Assert.True(ambiguous.Uncertain);
        Assert.False(ambiguous.PreviouslyObserved);
        Assert.False(ambiguous.MayAutoRetry);
        Assert.Null(ambiguous.VersionId);
    }

    [Fact]
    public async Task BogusPaginationAndForeignKeyFailClosedBeforeObjectRead()
    {
        var id = Guid.NewGuid();
        var key = ImmutableAttemptEvidence.ObjectKey(id);
        var scope = RandomNumberGenerator.GetBytes(32);
        var sign = RandomNumberGenerator.GetBytes(32);
        var now = DateTimeOffset.UtcNow;
        var badPages = new[]
        {
            new S3AttemptHistoryPage(
                [new S3AttemptHistoryEntry("foreign-key", "v1", false)],
                false, null, null),
            new S3AttemptHistoryPage(
                [new S3AttemptHistoryEntry(key, "latest", false)],
                false, null, null),
            new S3AttemptHistoryPage(
                [new S3AttemptHistoryEntry(key, "v1", false)],
                true, null, null),
            new S3AttemptHistoryPage(
                [new S3AttemptHistoryEntry(key, "v1", false)],
                true, key, "v1"),
        };
        foreach (var page in badPages)
        {
            var history = new RepeatingHistory(page);
            var reader = new NeverRead();
            var reconcile = new S3ObjectLockAttemptHistoryReconciler(
                history, reader, "ci-s3-evidence-store");
            var result = await reconcile.ReconcileAsync(id,
                "ci-tenant", "ci-actor", scope, sign, now, default);
            Assert.True(result.Uncertain);
            Assert.False(result.MayAutoRetry);
            Assert.Null(result.VersionId);
            Assert.InRange(history.Calls, 1, 2);
            // Foreign/alias/malformed cursors cannot lead to mutable GET.
            Assert.Equal(0, reader.Calls);
        }
    }

    private sealed class LoseAckAfterActualPut(
        IS3ObjectLockAttemptPut inner) : IS3ObjectLockAttemptPut
    {
        public async Task<S3ObjectLockPutAcknowledgment> PutOnceAsync(
            string objectKey, byte[] signedContent,
            DateTimeOffset retainUntilUtc, CancellationToken ct)
        {
            var committed = await inner.PutOnceAsync(objectKey,
                signedContent, retainUntilUtc, ct);
            Assert.NotNull(committed.VersionId);
            throw new IOException("CI synthetic ACK lost after S3 server PUT");
        }
    }

    private sealed class RepeatingHistory(S3AttemptHistoryPage page)
        : IS3AttemptVersionHistory
    {
        public int Calls { get; private set; }
        public Task<S3AttemptHistoryPage> PageAsync(string objectKey,
            string? keyMarker, string? versionMarker, int maxKeys,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(page);
        }
    }

    private sealed class NeverRead : IImmutableAttemptObjectReader
    {
        public int Calls { get; private set; }
        public Task<ImmutableAttemptObjectVersion?> ReadVersionAsync(
            string objectKey, string versionId, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Must not read ambiguous index");
        }
    }

    private static AmazonS3Client CiS3()
    {
        if (Environment.GetEnvironmentVariable("CI") != "true" ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("OMNIA_S3_CI_ONLY") != "1" ||
            Environment.GetEnvironmentVariable("OMNIA_TEST_S3_ENDPOINT")
                != "http://127.0.0.1:18433")
            throw new InvalidOperationException("Synthetic CI S3 only.");
        return new AmazonS3Client(new BasicAWSCredentials(
                Environment.GetEnvironmentVariable("OMNIA_TEST_S3_ACCESS")
                    ?? throw new InvalidOperationException("CI access unset"),
                Environment.GetEnvironmentVariable("OMNIA_TEST_S3_SECRET")
                    ?? throw new InvalidOperationException("CI secret unset")),
            new AmazonS3Config
            {
                ServiceURL = "http://127.0.0.1:18433",
                AuthenticationRegion = "us-east-1", ForcePathStyle = true,
                MaxErrorRetry = 0
            });
    }
}
