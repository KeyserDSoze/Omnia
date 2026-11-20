using System.Security.Cryptography;
using Npgsql;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// CI-only: a separately anchored, authenticated PRE-ERASE intent prefix.
/// This is NEVER a source DELETE commit receipt or production release proof.
/// </summary>
public sealed class PreEraseIntentIndependentAnchorTests
{
    [Fact]
    public async Task IntentPrefixSurvivesSyntheticRollbackAndCannotBeReSignedSilently()
    {
        await using var intentWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_WRITER_DB"));
        await using var intentReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_READER_DB"));
        await using var anchorWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_ANCHOR_WRITER_DB"));
        await using var anchorReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_ANCHOR_READER_DB"));
        await using var anchorAdmin = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_ANCHOR_ADMIN_DB"));
        await using var witnessAdmin = NpgsqlDataSource.Create(Required("OMNIA_TEST_WITNESS_ADMIN_DB"));
        await using (var w = witnessAdmin.CreateCommand("SELECT system_identifier FROM pg_control_system()"))
        await using (var a = anchorAdmin.CreateCommand("SELECT system_identifier FROM pg_control_system()"))
            Assert.NotEqual(await w.ExecuteScalarAsync(), await a.ExecuteScalarAsync());

        var tenant = "intent-checkpoint-" + Guid.NewGuid().ToString("N");
        var first = Guid.NewGuid();
        await new PostgresPreEraseIntentWriter(intentWriter).RecordAsync(
            first, tenant, "synthetic-intent-checkpoint", default);
        var key = RandomNumberGenerator.GetBytes(32);
        var issuer = new PostgresPreEraseIntentAnchorIssuer(
            intentReader, anchorWriter, anchorReader);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            issuer.IssueAsync(key, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            issuer.BootstrapAsync(key, operatorApproved: false, default));
        var firstCount = await issuer.BootstrapAsync(
            key, operatorApproved: true, default);
        Assert.True(firstCount >= 1);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            issuer.BootstrapAsync(key, operatorApproved: true, default));
        Assert.Equal(firstCount, await new PostgresPreEraseIntentAnchorVerifier(
            intentReader, anchorReader).VerifyAsync(key, default));

        var state = await new PostgresWitnessAnchorReader(anchorReader).ReadAsync(default);
        Assert.NotNull(state);
        Assert.Equal(firstCount, state!.Count);
        var snapshot = await PostgresPreEraseIntentSnapshot.ReadAsync(
            intentReader, IntentCheckpoint.MaxIntents, default);
        Assert.True(snapshot.Count >= firstCount);
        Assert.Equal(firstCount, IntentCheckpoint.Verify(snapshot,
            state.Checkpoint, key));
        Assert.Throws<InvalidDataException>(() => IntentCheckpoint.Verify(
            snapshot.Take(firstCount - 1).ToArray(), state.Checkpoint, key));
        Assert.Throws<InvalidDataException>(() => IntentCheckpoint.Verify(
            snapshot.Select((x, i) => i == 0
                ? x with { OwnerSubject = "tampered" } : x).ToArray(),
            state.Checkpoint, key));
        Assert.Throws<InvalidDataException>(() => IntentCheckpoint.Verify(
            snapshot, state.Checkpoint, RandomNumberGenerator.GetBytes(32)));
        Assert.Throws<InvalidDataException>(() => WitnessCheckpoint.Verify(
            [], state.Checkpoint, key));

        // Re-sending the same immutable prefix is idempotent. Replacing
        // the same count or decreasing it is always refused by third PGDATA.
        var anchor = new PostgresWitnessAnchor(anchorWriter);
        Assert.Equal(firstCount, await anchor.AdvanceAsync(
            firstCount, state.Checkpoint, default));
        await Assert.ThrowsAsync<PostgresException>(() => anchor.AdvanceAsync(
            firstCount, [.. state.Checkpoint, (byte)' '], default));
        if (firstCount > 1)
            await Assert.ThrowsAsync<PostgresException>(() => anchor.AdvanceAsync(
                firstCount - 1, state.Checkpoint, default));
        Assert.Equal(state, await new PostgresWitnessAnchorReader(
            anchorReader).ReadAsync(default));

        await new PostgresPreEraseIntentWriter(intentWriter).RecordAsync(
            Guid.NewGuid(), tenant, "synthetic-intent-checkpoint", default);
        var increased = await issuer.IssueAsync(key, default);
        Assert.True(increased > firstCount);
        Assert.Equal(increased, await new PostgresPreEraseIntentAnchorVerifier(
            intentReader, anchorReader).VerifyAsync(key, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            issuer.IssueAsync(RandomNumberGenerator.GetBytes(32), default));

        // No owner/admin/writer substitute is a valid read-only authority.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresPreEraseIntentAnchorVerifier(
                witnessAdmin, anchorReader).VerifyAsync(key, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresPreEraseIntentAnchorVerifier(
                intentReader, anchorAdmin).VerifyAsync(key, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresPreEraseIntentAnchorIssuer(
                intentReader, anchorReader, anchorWriter)
                .IssueAsync(key, default));
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException(
                "Synthetic independent intent anchor not configured.");
}
