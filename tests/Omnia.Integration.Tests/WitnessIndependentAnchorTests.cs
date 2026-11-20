using System.Security.Cryptography;
using Npgsql;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Third independent disposable PGDATA cluster anchors a signed witness prefix.
/// No external WORM/offsite custody or proof of upstream source completeness.
/// </summary>
public sealed class WitnessIndependentAnchorTests
{
    [Fact]
    public async Task ThirdClusterAnchorRejectsWitnessRollbackAndConflictingReissue()
    {
        await using var witnessWriter = NpgsqlDataSource.Create(Require("OMNIA_TEST_WITNESS_WRITER_DB"));
        await using var witnessReader = NpgsqlDataSource.Create(Require("OMNIA_TEST_WITNESS_READER_DB"));
        await using var anchorWriter = NpgsqlDataSource.Create(Require("OMNIA_TEST_ANCHOR_WRITER_DB"));
        await using var anchorReader = NpgsqlDataSource.Create(Require("OMNIA_TEST_ANCHOR_READER_DB"));
        await using var witnessAdmin = NpgsqlDataSource.Create(Require("OMNIA_TEST_WITNESS_ADMIN_DB"));
        await using var anchorAdmin = NpgsqlDataSource.Create(Require("OMNIA_TEST_ANCHOR_ADMIN_DB"));

        await using (var w = witnessAdmin.CreateCommand("SELECT system_identifier FROM pg_control_system()"))
        await using (var a = anchorAdmin.CreateCommand("SELECT system_identifier FROM pg_control_system()"))
            Assert.NotEqual(await w.ExecuteScalarAsync(), await a.ExecuteScalarAsync());

        var tenant = "anchor-" + Guid.NewGuid().ToString("N");
        var id = Guid.NewGuid();
        await new PostgresErasureWitness(witnessWriter).AppendAsync(
            new ErasureDispatchEvent(id, tenant, "synthetic-actor", "erase-actor",
                DateTimeOffset.UtcNow), default);
        var key = RandomNumberGenerator.GetBytes(32);

        var issuer = new PostgresWitnessAnchorIssuer(witnessReader,
            anchorWriter, anchorReader);
        var firstCount = await issuer.IssueAsync(key, default);
        var stored = await new PostgresWitnessAnchorReader(anchorReader).ReadAsync(default);
        Assert.NotNull(stored);
        Assert.Equal(firstCount, stored!.Count);
        Assert.True(firstCount >= 1);

        // Fresh reader must verify the checkpoint against the independently
        // held bytes, NOT a checkpoint freshly signed after the rollback.
        Assert.Equal(firstCount, await new PostgresWitnessAnchorVerifier(
            witnessReader, anchorReader).VerifyAsync(key, default));
        var snapshot = await PostgresErasureWitnessSnapshot.ReadAsync(
            witnessReader, 10000, default);
        Assert.Throws<InvalidDataException>(() => WitnessCheckpoint.Verify(
            snapshot.Take(firstCount - 1).ToArray(), stored.Checkpoint, key));
        Assert.Throws<InvalidDataException>(() => WitnessCheckpoint.Verify(
            snapshot.Select((receipt, index) => index == 0
                ? receipt with { OwnerSubject = "altered-actor" }
                : receipt).ToArray(), stored.Checkpoint, key));

        // A stale or conflicting replay must NOT rewrite the independent head.
        var anchor = new PostgresWitnessAnchor(anchorWriter);
        Assert.Equal(firstCount, await anchor.AdvanceAsync(
            firstCount, stored.Checkpoint, default));
        await Assert.ThrowsAsync<PostgresException>(() => anchor.AdvanceAsync(
            firstCount, [.. stored.Checkpoint, (byte)' '], default));
        if (firstCount > 1)
            await Assert.ThrowsAsync<PostgresException>(() => anchor.AdvanceAsync(
                firstCount - 1, stored.Checkpoint, default));
        Assert.Equal(stored, await new PostgresWitnessAnchorReader(
            anchorReader).ReadAsync(default));

        // An untrusted writer/reader login cannot inspect or change head SQL.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgresWitnessAnchor.VerifyWriterAsync(anchorReader));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgresWitnessAnchorReader.VerifyReaderAsync(anchorWriter));
        foreach (var db in new[] { anchorWriter, anchorReader })
        {
            await using var cmd = db.CreateCommand(
                "UPDATE anchor.checkpoints SET checkpoint = ''::bytea WHERE false");
            var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                cmd.ExecuteNonQueryAsync());
            Assert.Equal("42501", denied.SqlState);
        }

        var futureEvent = Guid.NewGuid();
        await new PostgresErasureWitness(witnessWriter).AppendAsync(
            new ErasureDispatchEvent(futureEvent, tenant, "synthetic-actor",
                "erase-actor", DateTimeOffset.UtcNow), default);
        var next = await issuer.IssueAsync(key, default);
        Assert.True(next > firstCount);
        Assert.Equal(next, await new PostgresWitnessAnchorVerifier(
            witnessReader, anchorReader).VerifyAsync(key, default));
    }

    [Fact]
    public async Task UntrustedAnchorMustNotAcceptAnUnverifiedNewCheckpoint()
    {
        await using var witnessReader = NpgsqlDataSource.Create(Require("OMNIA_TEST_WITNESS_READER_DB"));
        await using var anchorWriter = NpgsqlDataSource.Create(Require("OMNIA_TEST_ANCHOR_WRITER_DB"));
        await using var anchorReader = NpgsqlDataSource.Create(Require("OMNIA_TEST_ANCHOR_READER_DB"));
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        var state = await new PostgresWitnessAnchorReader(anchorReader).ReadAsync(default);
        if (state is not null)
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new PostgresWitnessAnchorIssuer(witnessReader,
                    anchorWriter, anchorReader).IssueAsync(wrongKey, default));
    }

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException("CI anchor unavailable.");
}
