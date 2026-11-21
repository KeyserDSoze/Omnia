using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Omnia.Infrastructure;
using Omnia.ObjectStore.S3;
using System.Security.Cryptography;
using Xunit;

namespace Omnia.S3ObjectLock.Tests;

/// <summary>
/// S3-compatible REAL Object Lock and versioning against an ephemeral
/// SeaweedFS instance. It shares the runner/admin with the databases:
/// this is provider protocol rehearsal, NOT offsite authority or WORM
/// isolation, never source DELETE / recovery READY.
/// </summary>
public sealed class S3ObjectLockAttemptReaderTests
{
    [Fact]
    public async Task RealLockedObjectVersionIsPinnedEvenAfterLaterVersionAndDeletionIsDenied()
    {
        using var client = CreateCiClient();
        var bucket = "omnia-ci-locked-" + Guid.NewGuid().ToString("N");
        await client.PutBucketAsync(new PutBucketRequest
        {
            BucketName = bucket,
            ObjectLockEnabledForBucket = true
        });

        var requestId = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var evidence = RandomNumberGenerator.GetBytes(32);
        try
        {
            var content = ImmutableAttemptEvidence.CreateSynthetic(
                requestId, "ci-s3-tenant", "ci-s3-actor", scope, evidence);
            var key = ImmutableAttemptEvidence.ObjectKey(requestId);
            var firstVersion = await PutAsync(client, bucket, key, content,
                ObjectLockMode.Compliance);
            Assert.False(string.IsNullOrWhiteSpace(firstVersion));
            Assert.NotEqual("null", firstVersion);
            Assert.NotEqual("latest", firstVersion);

            var reader = new S3ObjectLockAttemptReader(
                client, bucket, "ci-independent-s3-store");
            var observed = await ImmutableAttemptEvidence.VerifyAsync(reader,
                requestId, "ci-s3-tenant", "ci-s3-actor", scope, evidence,
                "ci-independent-s3-store", firstVersion,
                DateTimeOffset.UtcNow, default);
            Assert.True(observed.AttemptPreviouslyObserved);
            Assert.False(observed.ProvesSourceDeleteCommit);
            Assert.False(observed.MayAutoReplay);
            Assert.False(observed.MayReleaseRecovery);

            // A later unprotected version is allowed, but MUST NOT replace
            // the old pinned, retained version in a verified observation.
            var later = await PutAsync(client, bucket, key, content,
                ObjectLockMode.Governance);
            Assert.NotEqual(firstVersion, later);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ImmutableAttemptEvidence.VerifyAsync(reader,
                    requestId, "ci-s3-tenant", "ci-s3-actor", scope, evidence,
                    "ci-independent-s3-store", later,
                    DateTimeOffset.UtcNow, default));
            Assert.Equal(firstVersion, (await ImmutableAttemptEvidence.VerifyAsync(
                reader, requestId, "ci-s3-tenant", "ci-s3-actor",
                scope, evidence, "ci-independent-s3-store", firstVersion,
                DateTimeOffset.UtcNow, default)).VersionId);

            // Even admin credentials should NOT be allowed to DELETE the
            // exact COMPLIANCE version before retention expiry through S3.
            // This does not protect the runner host from privileged deletion.
            var refused = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
                client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = bucket, Key = key, VersionId = firstVersion
                }));
            Assert.Equal(System.Net.HttpStatusCode.Forbidden,
                refused.StatusCode);
            Assert.Equal(firstVersion, (await ImmutableAttemptEvidence.VerifyAsync(
                reader, requestId, "ci-s3-tenant", "ci-s3-actor",
                scope, evidence, "ci-independent-s3-store", firstVersion,
                DateTimeOffset.UtcNow, default)).VersionId);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ImmutableAttemptEvidence.VerifyAsync(reader,
                    requestId, "ci-s3-tenant", "ci-s3-actor", scope, evidence,
                    "ci-independent-s3-store", "null",
                    DateTimeOffset.UtcNow, default));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ImmutableAttemptEvidence.VerifyAsync(reader,
                    requestId, "ci-s3-tenant", "ci-s3-actor", scope, evidence,
                    "ci-independent-s3-store", "latest",
                    DateTimeOffset.UtcNow, default));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ImmutableAttemptEvidence.VerifyAsync(reader,
                    requestId, "ci-s3-tenant", "ci-foreign-actor", scope, evidence,
                    "ci-independent-s3-store", firstVersion,
                    DateTimeOffset.UtcNow, default));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ImmutableAttemptEvidence.VerifyAsync(reader,
                    requestId, "ci-s3-tenant", "ci-s3-actor", scope, evidence,
                    "ci-foreign-store", firstVersion,
                    DateTimeOffset.UtcNow, default));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ImmutableAttemptEvidence.VerifyAsync(reader,
                    requestId, "ci-s3-tenant", "ci-s3-actor", scope, evidence,
                    "ci-independent-s3-store", Guid.NewGuid().ToString("N"),
                    DateTimeOffset.UtcNow, default));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scope);
            CryptographicOperations.ZeroMemory(evidence);
        }
    }

    [Fact]
    public async Task RealUnlockedObjectAndUnsafeVersionAliasesAreDenied()
    {
        using var client = CreateCiClient();
        var bucket = "omnia-ci-unlocked-" + Guid.NewGuid().ToString("N");
        await client.PutBucketAsync(new PutBucketRequest
        {
            BucketName = bucket, ObjectLockEnabledForBucket = true
        });
        var requestId = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var evidence = RandomNumberGenerator.GetBytes(32);
        try
        {
            var data = ImmutableAttemptEvidence.CreateSynthetic(
                requestId, "ci-tenant", "ci-actor", scope, evidence);
            var key = ImmutableAttemptEvidence.ObjectKey(requestId);
            var unprotected = await PutAsync(client, bucket, key, data, null);
            var reader = new S3ObjectLockAttemptReader(
                client, bucket, "ci-independent-s3-store");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ImmutableAttemptEvidence.VerifyAsync(reader,
                    requestId, "ci-tenant", "ci-actor", scope, evidence,
                    "ci-independent-s3-store", unprotected,
                    DateTimeOffset.UtcNow, default));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                reader.ReadVersionAsync(key, "latest", default));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                reader.ReadVersionAsync(key, "null", default));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                reader.ReadVersionAsync("../foreign.json", unprotected, default));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scope);
            CryptographicOperations.ZeroMemory(evidence);
        }
    }

    private static AmazonS3Client CreateCiClient()
    {
        if (Environment.GetEnvironmentVariable("CI") != "true" ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("OMNIA_S3_CI_ONLY") != "1" ||
            Environment.GetEnvironmentVariable("OMNIA_TEST_S3_ENDPOINT") !=
                "http://127.0.0.1:18433")
            throw new InvalidOperationException("S3 object-lock test requires isolated CI.");
        var access = Environment.GetEnvironmentVariable("OMNIA_TEST_S3_ACCESS") ??
            throw new InvalidOperationException("Missing ephemeral S3 test identity.");
        var secret = Environment.GetEnvironmentVariable("OMNIA_TEST_S3_SECRET") ??
            throw new InvalidOperationException("Missing ephemeral S3 test identity.");
        return new AmazonS3Client(new BasicAWSCredentials(access, secret),
            new AmazonS3Config
            {
                ServiceURL = "http://127.0.0.1:18433",
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
                MaxErrorRetry = 0
            });
    }

    private static async Task<string> PutAsync(IAmazonS3 client, string bucket,
        string key, byte[] data, ObjectLockMode? mode)
    {
        using var body = new MemoryStream(data, writable: false);
        var put = new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = body,
            ContentType = "application/json"
        };
        if (mode is not null)
        {
            put.ObjectLockMode = mode;
            put.ObjectLockRetainUntilDate = DateTime.UtcNow.AddDays(2);
        }
        var response = await client.PutObjectAsync(put);
        return response.VersionId ?? throw new InvalidDataException(
            "S3_VERSION_ID_REQUIRED.");
    }
}
