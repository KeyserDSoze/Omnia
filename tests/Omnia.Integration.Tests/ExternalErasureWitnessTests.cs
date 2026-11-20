using Npgsql;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// TWO truly independent disposable PGDATA clusters. Synthetic events only.
/// A witness that survives a test-only primary deletion does not prove
/// offsite availability, immutable retention or independent key custody.
/// </summary>
public sealed class ExternalErasureWitnessTests
{
    [Fact]
    public async Task CommittedErasureCanBeWitnessedOutsidePrimaryAndRetryIsIdempotent()
    {
        var sourceAdmin = Require("OMNIA_TEST_DB");
        var sourceApp = Require("OMNIA_TEST_APP_DB");
        var dispatch = Require("OMNIA_TEST_DISPATCH_DB");
        var witnessAdmin = Require("OMNIA_TEST_WITNESS_ADMIN_DB");
        var witnessWrite = Require("OMNIA_TEST_WITNESS_WRITER_DB");
        var witnessRead = Require("OMNIA_TEST_WITNESS_READER_DB");

        await using var source = NpgsqlDataSource.Create(sourceAdmin);
        await using var independent = NpgsqlDataSource.Create(witnessAdmin);
        await using (var primary = source.CreateCommand(
            "SELECT system_identifier FROM pg_control_system()"))
        await using (var replica = independent.CreateCommand(
            "SELECT system_identifier FROM pg_control_system()"))
            Assert.NotEqual(await primary.ExecuteScalarAsync(),
                await replica.ExecuteScalarAsync());

        await new PostgresMigrator(source, "omnia_app", "omnia_retention",
            "omnia_backfill", "omnia_backfill_audit",
            dispatchRole: "omnia_dispatcher").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(sourceApp);
        var tenant = "witness-" + Guid.NewGuid().ToString("N");
        const string subject = "synthetic-witness-subject";
        var now = DateTimeOffset.UtcNow;
        await new PostgresConsentRepository(app).GrantAsync(
            ConsentGrant.Issue(tenant, subject, "manual",
                ConsentGrant.MemoryCapture, "privacy-v1", now, now.AddDays(1)),
            default);
        await new PostgresMemoryRepository(app, TestEnvelopeCrypto.Create())
            .UpsertAsync(MemoryEntry.Capture(tenant, subject, "manual",
                Guid.NewGuid().ToString("N"), "text/plain", "synthetic",
                "{\"test\":true}", "synthetic", [], now), default);

        var erased = await new PostgresPersonalDataService(
            app, TestEnvelopeCrypto.Create()).EraseAsync(
                tenant, subject, default);
        Assert.Equal(1, erased.DeletedMemories);
        Assert.Equal(1, erased.DeletedConsents);

        Guid eventId;
        await using (var query = source.CreateCommand("""
            SELECT id FROM public.erasure_outbox
             WHERE tenant_id = @tenant AND owner_sub = @actor
            """))
        {
            query.Parameters.AddWithValue("tenant", tenant);
            query.Parameters.AddWithValue("actor", subject);
            eventId = (Guid)(await query.ExecuteScalarAsync() ??
                throw new InvalidOperationException("Outbox event was not persisted."));
        }

        await using var dispatchDb = NpgsqlDataSource.Create(dispatch);
        await using var witnessWriter = NpgsqlDataSource.Create(witnessWrite);
        await using var witnessReader = NpgsqlDataSource.Create(witnessRead);
        var publisher = new PostgresErasureOutboxWitnessPublisher(
            dispatchDb, witnessWriter);
        var first = await publisher.PublishBatchAsync(100, default);
        Assert.True(first >= 1);
        var second = await publisher.PublishBatchAsync(100, default);
        Assert.Equal(first, second);

        // A separately stored acknowledgement still exists after the
        // synthetic source event is deliberately removed by test admin.
        await using (var wipe = source.CreateCommand("""
            DELETE FROM public.erasure_outbox WHERE id = @id
            """))
        {
            wipe.Parameters.AddWithValue("id", eventId);
            Assert.Equal(1, await wipe.ExecuteNonQueryAsync());
        }

        var witness = new PostgresErasureWitness(witnessWriter);
        var remote = new PostgresErasureWitnessReader(witnessReader);
        var evt = await remote.FindAsync(eventId, default);
        Assert.NotNull(evt);
        Assert.Equal(tenant, evt!.TenantId);
        Assert.Equal(subject, evt.OwnerSubject);
        Assert.Equal("erase-actor", evt.EventType);
        Assert.True(evt.Sequence > 0);

        // Replaying the exact remote event is idempotent, never advances
        // the remote checkpoint nor modifies an existing receipt.
        Assert.Equal(evt.Sequence, await witness.AppendAsync(
            new ErasureDispatchEvent(eventId, tenant, subject,
                "erase-actor", now), default));
        await Assert.ThrowsAsync<PostgresException>(() =>
            witness.AppendAsync(new ErasureDispatchEvent(
                eventId, tenant + "-changed", subject,
                "erase-actor", now), default));
        Assert.Equal(evt, await remote.FindAsync(eventId, default));
    }

    [Fact]
    public async Task WitnessPermissionsDenyDirectReadMutationAndForgedOperations()
    {
        await using var writer = NpgsqlDataSource.Create(
            Require("OMNIA_TEST_WITNESS_WRITER_DB"));
        await using var reader = NpgsqlDataSource.Create(
            Require("OMNIA_TEST_WITNESS_READER_DB"));
        await PostgresErasureWitness.VerifyWriterAsync(writer);
        await PostgresErasureWitnessReader.VerifyReaderAsync(reader);

        foreach (var connection in new[] { writer, reader })
        {
            foreach (var query in new[]
            {
                "SELECT count(*) FROM witness.erasure_events",
                "SELECT count(*) FROM witness.journal_head",
                "DELETE FROM witness.erasure_events WHERE false",
                "UPDATE witness.journal_head SET next_sequence = 1 WHERE false"
            })
            {
                await using var cmd = connection.CreateCommand(query);
                var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                    cmd.ExecuteNonQueryAsync());
                Assert.Equal("42501", exception.SqlState);
            }
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgresErasureWitness.VerifyWriterAsync(reader));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgresErasureWitnessReader.VerifyReaderAsync(writer));
        await Assert.ThrowsAsync<PostgresException>(() =>
            new PostgresErasureWitness(writer).AppendAsync(
                new ErasureDispatchEvent(Guid.NewGuid(), "synthetic",
                    "synthetic", "not-an-erasure", DateTimeOffset.UtcNow),
                default));
    }

    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        Assert.False(string.IsNullOrWhiteSpace(value), name);
        return value!;
    }
}
