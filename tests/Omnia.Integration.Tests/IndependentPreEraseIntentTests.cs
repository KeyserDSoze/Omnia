using Npgsql;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// CI-only pre-erasure INTENT (not completed source DELETE or ACK).
/// A durable intent may survive a primary PITR but cannot prove the
/// source DELETE committed or authorize actor deletion automatically.
/// </summary>
public sealed class IndependentPreEraseIntentTests
{
    [Fact]
    public async Task IndependentIntentIsIdempotentScopedAndNotAnErasureReceipt()
    {
        await using var writer = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_WRITER_DB"));
        await using var reader = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_READER_DB"));
        await using var witness = NpgsqlDataSource.Create(Required("OMNIA_TEST_WITNESS_READER_DB"));
        await using var witnessWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_WITNESS_WRITER_DB"));
        await using var admin = NpgsqlDataSource.Create(Required("OMNIA_TEST_WITNESS_ADMIN_DB"));
        await using var primary = NpgsqlDataSource.Create(Required("OMNIA_TEST_DB"));

        await using (var sourceId = primary.CreateCommand(
            "SELECT system_identifier FROM pg_control_system()"))
        await using (var witnessId = admin.CreateCommand(
            "SELECT system_identifier FROM pg_control_system()"))
            Assert.NotEqual(await sourceId.ExecuteScalarAsync(),
                await witnessId.ExecuteScalarAsync());

        var id = Guid.NewGuid();
        var tenant = "preintent-" + Guid.NewGuid().ToString("N");
        var owner = "synthetic-requested-actor";
        var service = new PostgresPreEraseIntentWriter(writer);
        var lookup = new PostgresPreEraseIntentReader(reader);
        await PostgresPreEraseIntentWriter.VerifyWriterAsync(writer);
        await PostgresPreEraseIntentReader.VerifyReaderAsync(reader);

        var first = await service.RecordAsync(id, tenant, owner, default);
        Assert.True(first > 0);
        Assert.Equal(first, await service.RecordAsync(id, tenant, owner, default));
        var receipt = await lookup.FindAsync(id, default);
        Assert.NotNull(receipt);
        Assert.Equal(first, receipt!.Sequence);
        Assert.Equal(tenant, receipt.TenantId);
        Assert.Equal(owner, receipt.OwnerSubject);
        Assert.Equal("erase-actor-intent", receipt.IntentType);
        Assert.Null(await new PostgresErasureWitnessReader(witness)
            .FindAsync(id, default));

        await Assert.ThrowsAsync<PostgresException>(() =>
            service.RecordAsync(id, tenant, owner + "-forged", default));
        await Assert.ThrowsAsync<PostgresException>(() =>
            service.RecordAsync(id, tenant + "-forged", owner, default));
        Assert.Equal(receipt, await lookup.FindAsync(id, default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RecordAsync(Guid.Empty, tenant, owner, default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RecordAsync(Guid.NewGuid(), " ", owner, default));

        var batch = await lookup.ReadBatchAsync(first - 1, 100, default);
        Assert.Contains(receipt, batch);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            lookup.ReadBatchAsync(0, 101, default));

        // The independent intent cannot be mistaken for a post-commit
        // erasure receipt; existing roles are not authorized to issue it.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresPreEraseIntentWriter(witnessWriter)
                .RecordAsync(Guid.NewGuid(), tenant, owner, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresPreEraseIntentReader(witness)
                .FindAsync(id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresPreEraseIntentWriter(admin)
                .RecordAsync(Guid.NewGuid(), tenant, owner, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresPreEraseIntentReader(admin)
                .FindAsync(id, default));
    }

    [Fact]
    public async Task RestrictedIntentRolesCannotReadTablesOrForgeCompletion()
    {
        await using var writer = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_WRITER_DB"));
        await using var reader = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_READER_DB"));
        await using var witnessWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_WITNESS_WRITER_DB"));
        await using var witnessReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_WITNESS_READER_DB"));
        foreach (var db in new[] { writer, reader })
        {
            foreach (var sql in new[]
            {
                "SELECT count(*) FROM witness.erasure_intents",
                "SELECT count(*) FROM witness.intent_head",
                "DELETE FROM witness.erasure_intents WHERE false",
                "UPDATE witness.intent_head SET next_sequence = 1 WHERE false",
                "SELECT count(*) FROM witness.erasure_events"
            })
            {
                await using var query = db.CreateCommand(sql);
                var ex = await Assert.ThrowsAsync<PostgresException>(
                    () => query.ExecuteNonQueryAsync());
                Assert.Equal("42501", ex.SqlState);
            }
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgresPreEraseIntentWriter.VerifyWriterAsync(reader));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgresPreEraseIntentReader.VerifyReaderAsync(writer));

        // Intent-only credentials have NO completed-erasure capability.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgresErasureWitness.VerifyWriterAsync(writer));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgresErasureWitnessReader.VerifyReaderAsync(reader));
        await PostgresErasureWitness.VerifyWriterAsync(witnessWriter);
        await PostgresErasureWitnessReader.VerifyReaderAsync(witnessReader);
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException("CI witness identity missing.");
}
