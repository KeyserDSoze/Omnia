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
/// Real S3 SDK + two ephemeral SeaweedFS Object Lock buckets; same CI
/// administrator/failure domain, NEVER independent offsite authority.
/// </summary>
public sealed class S3ObjectLockVersionPinTests
{
    [Fact]
    public async Task SignedPinExactVersionSurvivesDeleteMarkerAndNewGovernanceVersion()
    {
        using var client = CiS3();
        var evidenceBucket = "omnia-ci-evidence-" + Guid.NewGuid().ToString("N");
        var pinBucket = "omnia-ci-pins-" + Guid.NewGuid().ToString("N");
        await CreateLockedBucket(client, evidenceBucket);
        await CreateLockedBucket(client, pinBucket);
        var id = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var evidenceKey = RandomNumberGenerator.GetBytes(32);
        var pinKey = RandomNumberGenerator.GetBytes(32);
        var now = DateTimeOffset.UtcNow;
        var expiry = now.AddDays(2);
        var evidenceReader = new S3ObjectLockAttemptReader(
            client, evidenceBucket, "ci-evidence-store");
        var pinReader = new S3ObjectLockPinReader(
            client, pinBucket, "ci-separate-pin-store");
        var evidencePublisher = new S3ObjectLockAttemptPublisher(
            new AwsS3ObjectLockAttemptPut(client, evidenceBucket),
            evidenceReader, "ci-evidence-store");
        var signedEvidence = ImmutableAttemptEvidence.CreateSynthetic(
            id, "ci-tenant", "ci-subject", scope, evidenceKey);
        var evidence = await evidencePublisher.SubmitOnceAsync(id,
            "ci-tenant", "ci-subject", scope, evidenceKey,
            signedEvidence, expiry, now, default);
        Assert.True(evidence.Observed);
        Assert.NotNull(evidence.VersionId);

        var pin = new S3ObjectLockVersionPinService(evidenceReader,
            pinReader, new AwsS3ObjectLockAttemptPut(client, pinBucket),
            "ci-evidence-store", "ci-separate-pin-store");
        var result = await pin.PublishOnceAsync(id,
            "ci-tenant", "ci-subject", scope, evidenceKey, pinKey,
            evidence.VersionId!, DateTimeOffset.UtcNow, default);
        Assert.True(result.PinObserved);
        Assert.False(result.Uncertain);
        Assert.NotNull(result.PinVersionId);
        Assert.Equal(evidence.VersionId, result.EvidenceVersionId);
        Assert.False(result.MayAutoRetry);
        Assert.False(result.MayReleaseRecovery);
        Assert.False(result.ProvesSourceDeleteCommit);

        // Exact pin version remains verifiable even if latest is deleted.
        await client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = pinBucket, Key = S3ObjectLockVersionPinService.PinKey(id)
        });
        var retained = await pin.VerifyPinnedAsync(id, "ci-tenant",
            "ci-subject", scope, evidenceKey, pinKey,
            evidence.VersionId!, result.PinVersionId!,
            DateTimeOffset.UtcNow, default);
        Assert.True(retained.PinObserved);
        Assert.False(retained.MayReleaseRecovery);

        // A later GOVERNANCE object cannot replace the pinned COMPLIANCE
        // version or be used as an alternative trusted pin.
        var bytes = S3ObjectLockVersionPinService.CreateSignedSynthetic(
            id, "ci-tenant", "ci-subject", scope, evidenceKey, pinKey,
            "ci-evidence-store", evidence.VersionId!);
        using var stream = new MemoryStream(bytes);
        var newer = await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = pinBucket,
            Key = S3ObjectLockVersionPinService.PinKey(id),
            InputStream = stream, ContentType = "application/json",
            MD5Digest = Convert.ToBase64String(MD5.HashData(bytes)),
            ObjectLockMode = ObjectLockMode.Governance,
            ObjectLockRetainUntilDate = DateTime.UtcNow.AddDays(2)
        });
        Assert.NotEqual(result.PinVersionId, newer.VersionId);
        var forged = await pin.VerifyPinnedAsync(id, "ci-tenant",
            "ci-subject", scope, evidenceKey, pinKey,
            evidence.VersionId!, newer.VersionId,
            DateTimeOffset.UtcNow, default);
        Assert.True(forged.Uncertain);
        Assert.Null(forged.PinVersionId);
        Assert.True((await pin.VerifyPinnedAsync(id, "ci-tenant",
            "ci-subject", scope, evidenceKey, pinKey,
            evidence.VersionId!, result.PinVersionId!,
            DateTimeOffset.UtcNow, default)).PinObserved);

        var forbidden = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = pinBucket,
                Key = S3ObjectLockVersionPinService.PinKey(id),
                VersionId = result.PinVersionId
            }));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task WrongActorMacStoreOrVersionCannotVerifyPinnedReceipt()
    {
        using var client = CiS3();
        var evidenceBucket = "omnia-ci-evidence-" + Guid.NewGuid().ToString("N");
        var pinBucket = "omnia-ci-pins-" + Guid.NewGuid().ToString("N");
        await CreateLockedBucket(client, evidenceBucket);
        await CreateLockedBucket(client, pinBucket);
        var id = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var evidenceKey = RandomNumberGenerator.GetBytes(32);
        var pinKey = RandomNumberGenerator.GetBytes(32);
        var evidenceReader = new S3ObjectLockAttemptReader(
            client, evidenceBucket, "ci-evidence-store");
        var signedEvidence = ImmutableAttemptEvidence.CreateSynthetic(
            id, "ci-tenant", "ci-subject", scope, evidenceKey);
        var publisher = new S3ObjectLockAttemptPublisher(
            new AwsS3ObjectLockAttemptPut(client, evidenceBucket),
            evidenceReader, "ci-evidence-store");
        var ack = await publisher.SubmitOnceAsync(id, "ci-tenant",
            "ci-subject", scope, evidenceKey, signedEvidence,
            DateTimeOffset.UtcNow.AddDays(2),
            DateTimeOffset.UtcNow, default);
        Assert.True(ack.Observed);

        var pin = new S3ObjectLockVersionPinService(evidenceReader,
            new S3ObjectLockPinReader(client, pinBucket,
                "ci-separate-pin-store"),
            new AwsS3ObjectLockAttemptPut(client, pinBucket),
            "ci-evidence-store", "ci-separate-pin-store");
        var created = await pin.PublishOnceAsync(id, "ci-tenant",
            "ci-subject", scope, evidenceKey, pinKey,
            ack.VersionId!, DateTimeOffset.UtcNow, default);
        Assert.True(created.PinObserved);

        async Task Denied(string actor, byte[] pkey,
            string evidenceVersion, string pinVersion)
        {
            var observed = await pin.VerifyPinnedAsync(id,
                "ci-tenant", actor, scope, evidenceKey, pkey,
                evidenceVersion, pinVersion,
                DateTimeOffset.UtcNow, default);
            Assert.True(observed.Uncertain);
            Assert.Null(observed.PinVersionId);
            Assert.False(observed.MayAutoRetry);
        }
        await Denied("foreign-actor", pinKey, ack.VersionId!,
            created.PinVersionId!);
        await Denied("ci-subject", RandomNumberGenerator.GetBytes(32),
            ack.VersionId!, created.PinVersionId!);
        await Denied("ci-subject", pinKey, "latest",
            created.PinVersionId!);
        await Denied("ci-subject", pinKey, ack.VersionId!, "null");
        await Denied("ci-subject", pinKey, Guid.NewGuid().ToString("N"),
            created.PinVersionId!);
        await Denied("ci-subject", pinKey, ack.VersionId!,
            Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task LostPinAckRemainsUncertainButExactExternallyPinnedVersionCanBeRead()
    {
        using var client = CiS3();
        var evidenceBucket = "omnia-ci-evidence-" + Guid.NewGuid().ToString("N");
        var pinBucket = "omnia-ci-pins-" + Guid.NewGuid().ToString("N");
        await CreateLockedBucket(client, evidenceBucket);
        await CreateLockedBucket(client, pinBucket);
        var id = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var evidenceKey = RandomNumberGenerator.GetBytes(32);
        var pinKey = RandomNumberGenerator.GetBytes(32);
        var evidenceReader = new S3ObjectLockAttemptReader(
            client, evidenceBucket, "ci-evidence-store");
        var evidence = await new S3ObjectLockAttemptPublisher(
            new AwsS3ObjectLockAttemptPut(client, evidenceBucket),
            evidenceReader, "ci-evidence-store")
            .SubmitOnceAsync(id, "ci-tenant", "ci-subject", scope,
                evidenceKey, ImmutableAttemptEvidence.CreateSynthetic(
                    id, "ci-tenant", "ci-subject", scope, evidenceKey),
                DateTimeOffset.UtcNow.AddDays(2),
                DateTimeOffset.UtcNow, default);
        Assert.True(evidence.Observed);

        var lostAck = new LoseAckPinPut(
            new AwsS3ObjectLockAttemptPut(client, pinBucket));
        var pin = new S3ObjectLockVersionPinService(evidenceReader,
            new S3ObjectLockPinReader(client, pinBucket,
                "ci-separate-pin-store"),
            lostAck, "ci-evidence-store", "ci-separate-pin-store");
        var result = await pin.PublishOnceAsync(id, "ci-tenant",
            "ci-subject", scope, evidenceKey, pinKey,
            evidence.VersionId!, DateTimeOffset.UtcNow, default);
        Assert.True(result.Uncertain);
        Assert.False(result.PinObserved);
        Assert.Null(result.PinVersionId);
        Assert.False(result.MayAutoRetry);
        Assert.False(result.MayReleaseRecovery);
        Assert.Equal(1, lostAck.Calls);
        Assert.NotNull(lostAck.CommittedVersion);

        // Simulates a DISTINCT, trusted externally retained VersionId.
        // It does NOT assert such an external authority is provisioned.
        var verified = await pin.VerifyPinnedAsync(id, "ci-tenant",
            "ci-subject", scope, evidenceKey, pinKey,
            evidence.VersionId!, lostAck.CommittedVersion!,
            DateTimeOffset.UtcNow, default);
        Assert.True(verified.PinObserved);
        Assert.False(verified.ProvesSourceDeleteCommit);
    }

    [Fact]
    public async Task ShortPinRetentionForeignStoreAndUnsafeKeysFailClosedWithoutPut()
    {
        var id = Guid.NewGuid();
        var scope = RandomNumberGenerator.GetBytes(32);
        var evidenceKey = RandomNumberGenerator.GetBytes(32);
        var pinKey = RandomNumberGenerator.GetBytes(32);
        var now = DateTimeOffset.UtcNow;
        const string evidenceVersion = "evidence-v1";
        const string pinVersion = "pin-v1";
        const string evidenceStore = "ci-evidence-store";
        const string pinStore = "ci-pin-store";
        var evidenceExpiry = now.AddDays(2);
        var original = ImmutableAttemptEvidence.CreateSynthetic(id,
            "ci-tenant", "ci-subject", scope, evidenceKey);
        var pinContent = S3ObjectLockVersionPinService.CreateSignedSynthetic(
            id, "ci-tenant", "ci-subject", scope, evidenceKey, pinKey,
            evidenceStore, evidenceVersion);
        var evidenceReader = new FixedReader(
            new ImmutableAttemptObjectVersion(evidenceStore,
                ImmutableAttemptEvidence.ObjectKey(id), evidenceVersion,
                ImmutableAttemptImmutabilityMode.S3Compliance,
                evidenceExpiry, original));
        var badRetention = new FixedReader(
            new ImmutableAttemptObjectVersion(pinStore,
                S3ObjectLockVersionPinService.PinKey(id), pinVersion,
                ImmutableAttemptImmutabilityMode.S3Compliance,
                now.AddHours(2), pinContent));
        var writer = new CountingWriter();
        var service = new S3ObjectLockVersionPinService(evidenceReader,
            badRetention, writer, evidenceStore, pinStore);

        var shortened = await service.VerifyPinnedAsync(id, "ci-tenant",
            "ci-subject", scope, evidenceKey, pinKey, evidenceVersion,
            pinVersion, now, default);
        Assert.True(shortened.Uncertain);
        Assert.False(shortened.MayReleaseRecovery);
        Assert.Equal(0, writer.Calls);

        var wrongIdentity = new S3ObjectLockVersionPinService(
            evidenceReader, new FixedReader(
                new ImmutableAttemptObjectVersion("foreign-pin-store",
                    S3ObjectLockVersionPinService.PinKey(id), pinVersion,
                    ImmutableAttemptImmutabilityMode.S3Compliance,
                    evidenceExpiry, pinContent)),
            writer, evidenceStore, pinStore);
        Assert.True((await wrongIdentity.VerifyPinnedAsync(id,
            "ci-tenant", "ci-subject", scope, evidenceKey,
            pinKey, evidenceVersion, pinVersion, now, default)).Uncertain);
        Assert.Equal(0, writer.Calls);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.PublishOnceAsync(id, "ci-tenant", "ci-subject",
                scope, evidenceKey, scope, evidenceVersion, now, default));
        Assert.Equal(0, writer.Calls);

        // Even a validly HMAC-signed receipt from a fake Azure-locked
        // provider must not cause an S3 COMPLIANCE pin PUT.
        var azureReceipt = new FixedReader(
            new ImmutableAttemptObjectVersion(evidenceStore,
                ImmutableAttemptEvidence.ObjectKey(id), evidenceVersion,
                ImmutableAttemptImmutabilityMode.AzureLocked,
                evidenceExpiry, original));
        var wrongMode = new S3ObjectLockVersionPinService(azureReceipt,
            badRetention, writer, evidenceStore, pinStore);
        var refused = await wrongMode.PublishOnceAsync(id, "ci-tenant",
            "ci-subject", scope, evidenceKey, pinKey,
            evidenceVersion, now, default);
        Assert.True(refused.Uncertain);
        Assert.Equal(0, writer.Calls);
    }

    private sealed class FixedReader(ImmutableAttemptObjectVersion version)
        : IImmutableAttemptObjectReader
    {
        public Task<ImmutableAttemptObjectVersion?> ReadVersionAsync(
            string objectKey, string versionId, CancellationToken ct) =>
            Task.FromResult<ImmutableAttemptObjectVersion?>(version);
    }

    private sealed class CountingWriter : IS3ObjectLockAttemptPut
    {
        public int Calls { get; private set; }
        public Task<S3ObjectLockPutAcknowledgment> PutOnceAsync(
            string objectKey, byte[] signedContent,
            DateTimeOffset retainUntilUtc, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new S3ObjectLockPutAcknowledgment("pin-v1"));
        }
    }

    private sealed class LoseAckPinPut(IS3ObjectLockAttemptPut inner)
        : IS3ObjectLockAttemptPut
    {
        public int Calls { get; private set; }
        public string? CommittedVersion { get; private set; }
        public async Task<S3ObjectLockPutAcknowledgment> PutOnceAsync(
            string key, byte[] bytes, DateTimeOffset until,
            CancellationToken ct)
        {
            Calls++;
            CommittedVersion = (await inner.PutOnceAsync(
                key, bytes, until, ct)).VersionId;
            throw new IOException("Synthetic pin ACK lost after real PUT");
        }
    }

    private static async Task CreateLockedBucket(
        AmazonS3Client client, string bucket)
    {
        await client.PutBucketAsync(new PutBucketRequest
        {
            BucketName = bucket, ObjectLockEnabledForBucket = true
        });
    }

    private static AmazonS3Client CiS3()
    {
        if (Environment.GetEnvironmentVariable("CI") != "true" ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("OMNIA_S3_CI_ONLY") != "1" ||
            Environment.GetEnvironmentVariable("OMNIA_TEST_S3_ENDPOINT")
                != "http://127.0.0.1:18433")
            throw new InvalidOperationException("Synthetic S3-only CI.");
        return new AmazonS3Client(new BasicAWSCredentials(
                Environment.GetEnvironmentVariable("OMNIA_TEST_S3_ACCESS")
                    ?? throw new InvalidOperationException("Missing synthetic key"),
                Environment.GetEnvironmentVariable("OMNIA_TEST_S3_SECRET")
                    ?? throw new InvalidOperationException("Missing synthetic key")),
            new AmazonS3Config
            {
                ServiceURL = "http://127.0.0.1:18433",
                AuthenticationRegion = "us-east-1",
                ForcePathStyle = true, MaxErrorRetry = 0
            });
    }
}
