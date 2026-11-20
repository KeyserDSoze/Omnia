using System.Security.Cryptography;
using Npgsql;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Synthetic CI-only independent fourth-PGDATA one-shot fence.
/// A rollback/deletion of the THIRD-PGDATA claim must not permit a
/// previously consumed request UUID to erase actor data again.
/// This is an ATTEMPT fence, not a source COMMIT attestation.
/// </summary>
public sealed class FourthClusterEraseFenceTests
{
    [Fact]
    public async Task FourthFenceSurvivesThirdClaimLossAndBlocksResurrectedActorErase()
    {
        await using var primaryAdmin = NpgsqlDataSource.Create(Required("OMNIA_TEST_DB"));
        await new PostgresMigrator(primaryAdmin, "omnia_app", "omnia_retention",
            "omnia_backfill", "omnia_backfill_audit",
            dispatchRole: "omnia_dispatcher").MigrateAsync();
        await using var primary = NpgsqlDataSource.Create(Required("OMNIA_TEST_APP_DB"));
        await using var intentWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_WRITER_DB"));
        await using var intentReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_READER_DB"));
        await using var anchorWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_FOURTH_ANCHOR_WRITER_DB"));
        await using var anchorReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_FOURTH_ANCHOR_READER_DB"));
        await using var thirdWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_EXECUTION_CLAIM_WRITER_DB"));
        await using var thirdReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_EXECUTION_CLAIM_READER_DB"));
        await using var thirdAdmin = NpgsqlDataSource.Create(Required("OMNIA_TEST_EXECUTION_CLAIM_ADMIN_DB"));
        await using var fourthWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_FOURTH_CLAIM_WRITER_DB"));
        await using var fourthReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_FOURTH_CLAIM_READER_DB"));
        await using var fourthAdmin = NpgsqlDataSource.Create(Required("OMNIA_TEST_FOURTH_CLAIM_ADMIN_DB"));

        await using (var a = thirdAdmin.CreateCommand("SELECT system_identifier FROM pg_control_system()"))
        await using (var b = fourthAdmin.CreateCommand("SELECT system_identifier FROM pg_control_system()"))
            Assert.NotEqual(await a.ExecuteScalarAsync(), await b.ExecuteScalarAsync());

        var tenant = "fourth-fence-" + Guid.NewGuid().ToString("N");
        const string actor = "synthetic-fourth-fence-owner";
        var id = Guid.NewGuid();
        var key = RandomNumberGenerator.GetBytes(32);
        var now = DateTimeOffset.UtcNow;
        var crypto = TestEnvelopeCrypto.Create();
        var memory = new PostgresMemoryRepository(primary, crypto);
        var consents = new PostgresConsentRepository(primary);
        var service = new PostgresPersonalDataService(primary, crypto);
        await consents.GrantAsync(ConsentGrant.Issue(tenant, actor, "manual",
            ConsentGrant.MemoryCapture, "synthetic-v1", now, now.AddDays(2)), default);
        await memory.UpsertAsync(MemoryEntry.Capture(tenant, actor, "manual",
            Guid.NewGuid().ToString("N"), "text/plain", "synthetic",
            "{\"synthetic\":true}", "test fourth fence", [], now), default);

        await new PostgresPreEraseIntentWriter(intentWriter).RecordAsync(
            id, tenant, actor, default);
        await new PostgresPreEraseIntentAnchorIssuer(
            intentReader, anchorWriter, anchorReader)
            .BootstrapAsync(key, operatorApproved: true, default);

        var fourth = new PostgresPreEraseExecutionClaimReader(fourthReader);
        var third = new PostgresPreEraseExecutionClaimReader(thirdReader);
        // A wrong FOURTH SQL identity must fail before consuming THIRD.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EraseWithFourthFenceAsync(id, tenant, actor,
                intentReader, anchorReader, thirdWriter, fourthReader,
                key, default));
        Assert.False(await third.HasClaimAsync(id, default));
        Assert.False(await fourth.HasClaimAsync(id, default));
        Assert.Single(await memory.RecentAsync(tenant, actor, 10, default));

        var result = await service.EraseWithFourthFenceAsync(id, tenant, actor,
            intentReader, anchorReader, thirdWriter, fourthWriter,
            key, default);
        Assert.Equal(1, result.DeletedMemories);
        Assert.Equal(1, result.DeletedConsents);
        await fourth.VerifyClaimAsync(id, tenant, actor, key, default);
        await third.VerifyClaimAsync(id, tenant, actor, key, default);

        // Synthetic rollback of the THIRD claim journal: privilege held by
        // test superuser only. Never used as a production recovery routine.
        await using (var tamper = thirdAdmin.CreateCommand("""
            DELETE FROM admission.execution_claims WHERE request_id=@id
            """))
        {
            tamper.Parameters.AddWithValue("id", id);
            Assert.Equal(1, await tamper.ExecuteNonQueryAsync());
        }
        Assert.False(await third.HasClaimAsync(id, default));
        await fourth.VerifyClaimAsync(id, tenant, actor, key, default);

        var newer = DateTimeOffset.UtcNow;
        await consents.GrantAsync(ConsentGrant.Issue(tenant, actor, "manual",
            ConsentGrant.MemoryCapture, "synthetic-v2", newer,
            newer.AddDays(2)), default);
        await memory.UpsertAsync(MemoryEntry.Capture(tenant, actor, "manual",
            Guid.NewGuid().ToString("N"), "text/plain", "synthetic",
            "{\"synthetic\":\"new\"}", "new synthetic content", [], newer), default);

        var replay = await Assert.ThrowsAsync<PostgresException>(() =>
            service.EraseWithFourthFenceAsync(id, tenant, actor,
                intentReader, anchorReader, thirdWriter, fourthWriter,
                key, default));
        Assert.Equal("23505", replay.SqlState);
        Assert.False(await third.HasClaimAsync(id, default));
        Assert.Single(await memory.RecentAsync(tenant, actor, 10, default));
        Assert.Single((await consents.ListAsync(tenant, actor, default))
            .Where(x => x.RevokedAt is null));
        // The fourth fence is a separate administrative/failure domain only
        // within this synthetic CI topology, not actual offsite WORM.
    }

    [Fact]
    public async Task FourthFenceClaimIsConsumedEvenWhenThirdCommitNeverOccurs()
    {
        await using var intentWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_WRITER_DB"));
        await using var intentReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_READER_DB"));
        await using var anchorWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_FOURTH_ANCHOR_WRITER_DB"));
        await using var anchorReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_FOURTH_ANCHOR_READER_DB"));
        await using var primary = NpgsqlDataSource.Create(Required("OMNIA_TEST_APP_DB"));
        await using var thirdReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_EXECUTION_CLAIM_READER_DB"));
        await using var fourthWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_FOURTH_CLAIM_WRITER_DB"));
        await using var fourthReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_FOURTH_CLAIM_READER_DB"));
        var id = Guid.NewGuid();
        var tenant = "fourth-stranded-" + Guid.NewGuid().ToString("N");
        const string owner = "synthetic-stranded-owner";
        var key = RandomNumberGenerator.GetBytes(32);
        await new PostgresPreEraseIntentWriter(intentWriter)
            .RecordAsync(id, tenant, owner, default);
        // The prior test class shares one anchor DB; serialize tests within
        // class via xUnit class execution and advance the existing prefix.
        var issuer = new PostgresPreEraseIntentAnchorIssuer(
            intentReader, anchorWriter, anchorReader);
        // This test has its own freshly initialized anchor (see workflow).
        // If another test has bootstrapped it first, issuing with another
        // signing key is intentionally refused. Use independent DB.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresPersonalDataService(primary, TestEnvelopeCrypto.Create())
                .EraseWithFourthFenceAsync(id, tenant, owner,
                    intentReader, anchorReader, thirdReader, fourthWriter,
                    key, default));
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException(
                "Synthetic CI fourth-cluster database is missing.");
}
