using System.Security.Cryptography;
using Npgsql;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// CI-only witness anchored-prefix proof. The HMAC checkpoint MUST be
/// preserved by an independent trust domain for rollback detection.
/// </summary>
public sealed class WitnessCheckpointTests
{
    [Fact]
    public void AuthenticatedCheckpointRejectsMissingAlteredOrReorderedPrefix()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var otherKey = RandomNumberGenerator.GetBytes(32);
        var first = new ErasureWitnessReceipt(
            1, Guid.NewGuid(), "synthetic-tenant-a", "synthetic-subject-a",
            "erase-actor");
        var second = new ErasureWitnessReceipt(
            2, Guid.NewGuid(), "synthetic-tenant-b", "synthetic-subject-b",
            "erase-actor");
        var third = new ErasureWitnessReceipt(
            3, Guid.NewGuid(), "synthetic-tenant-c", "synthetic-subject-c",
            "erase-actor");
        var signed = WitnessCheckpoint.Create([first, second], key);
        Assert.Equal(2, WitnessCheckpoint.Verify([first, second], signed, key));
        // Later witness appends do not invalidate an older authentic prefix;
        // they are NOT covered by this checkpoint until independently re-anchored.
        Assert.Equal(2, WitnessCheckpoint.Verify([first, second, third], signed, key));

        Assert.Throws<InvalidDataException>(() =>
            WitnessCheckpoint.Verify([first], signed, key));
        Assert.Throws<InvalidDataException>(() =>
            WitnessCheckpoint.Verify([second, first], signed, key));
        Assert.Throws<InvalidDataException>(() =>
            WitnessCheckpoint.Verify(
                [first, second with { OwnerSubject = "forged" }], signed, key));
        Assert.Throws<InvalidDataException>(() =>
            WitnessCheckpoint.Verify([first, second], signed, otherKey));
        Assert.Throws<InvalidDataException>(() =>
            WitnessCheckpoint.Verify([first, second], [], key));
        Assert.Throws<InvalidDataException>(() =>
            WitnessCheckpoint.Verify([first, second],
                [.. signed, (byte)' '], key));
        Assert.Throws<InvalidDataException>(() =>
            WitnessCheckpoint.Create([first, first with { Sequence = 2 }], key));
        Assert.Throws<InvalidDataException>(() =>
            WitnessCheckpoint.Create([first with { Sequence = 2 }], key));
    }

    [Fact]
    public async Task RestrictedWitnessReaderProducesConsistentSnapshotForCheckpoint()
    {
        await using var writer = NpgsqlDataSource.Create(Require(
            "OMNIA_TEST_WITNESS_WRITER_DB"));
        await using var reader = NpgsqlDataSource.Create(Require(
            "OMNIA_TEST_WITNESS_READER_DB"));
        var sample = new ErasureDispatchEvent(
            Guid.NewGuid(), "checkpoint-synthetic-" + Guid.NewGuid().ToString("N"),
            "synthetic-subject", "erase-actor", DateTimeOffset.UtcNow);
        var sequence = await new PostgresErasureWitness(writer).AppendAsync(
            sample, default);

        var snapshot = await PostgresErasureWitnessSnapshot.ReadAsync(
            reader, 10000, default);
        Assert.Contains(snapshot, r => r.EventId == sample.Id
            && r.Sequence == sequence && r.TenantId == sample.TenantId);
        var secret = RandomNumberGenerator.GetBytes(32);
        var checkpoint = WitnessCheckpoint.Create(snapshot, secret);
        Assert.Equal(snapshot.Count,
            WitnessCheckpoint.Verify(snapshot, checkpoint, secret));

        // Bounded scans and least-privilege: writer cannot act as auditor.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgresErasureWitnessSnapshot.ReadAsync(writer, 10000, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            PostgresErasureWitnessSnapshot.ReadAsync(reader, 10001, default));
    }

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException("CI witness unavailable.");
}
