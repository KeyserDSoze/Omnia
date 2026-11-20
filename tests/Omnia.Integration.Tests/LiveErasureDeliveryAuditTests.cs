using Npgsql;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// A live, committed source erasure is NOT a remotely delivered deletion.
/// The audit fails closed before witness receipt, including on forged scope,
/// with only EXECUTE-only credentials; it never authorizes recovery.
/// </summary>
public sealed class LiveErasureDeliveryAuditTests
{
    [Fact]
    public async Task CommittedErasureRemainsPendingUntilWitnessAcknowledgesSameEvent()
    {
        await using var admin = NpgsqlDataSource.Create(Require("OMNIA_TEST_DB"));
        await new PostgresMigrator(admin, "omnia_app", "omnia_retention",
            "omnia_backfill", "omnia_backfill_audit",
            dispatchRole: "omnia_dispatcher").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(Require("OMNIA_TEST_APP_DB"));
        await using var dispatch = NpgsqlDataSource.Create(Require("OMNIA_TEST_DISPATCH_DB"));
        await using var witnessWriter = NpgsqlDataSource.Create(Require("OMNIA_TEST_WITNESS_WRITER_DB"));
        await using var witnessReader = NpgsqlDataSource.Create(Require("OMNIA_TEST_WITNESS_READER_DB"));

        var tenant = "live-audit-" + Guid.NewGuid().ToString("N");
        var subject = "synthetic-delivery-audit";
        var now = DateTimeOffset.UtcNow;
        await new PostgresConsentRepository(app).GrantAsync(
            ConsentGrant.Issue(tenant, subject, "manual",
                ConsentGrant.MemoryCapture, "privacy-v1", now, now.AddDays(1)),
            default);
        await new PostgresMemoryRepository(app, TestEnvelopeCrypto.Create())
            .UpsertAsync(MemoryEntry.Capture(tenant, subject, "manual",
                Guid.NewGuid().ToString("N"), "text/plain", "synthetic",
                "{\"sample\":true}", "synthetic", [], now), default);
        await new PostgresPersonalDataService(app, TestEnvelopeCrypto.Create())
            .EraseAsync(tenant, subject, default);

        Guid eventId;
        await using (var query = admin.CreateCommand("""
            SELECT id FROM public.erasure_outbox
            WHERE tenant_id = @tenant AND owner_sub = @subject
            """))
        {
            query.Parameters.AddWithValue("tenant", tenant);
            query.Parameters.AddWithValue("subject", subject);
            eventId = (Guid)(await query.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("Expected committed test erasure."));
        }

        var audit = new PostgresErasureLiveDeliveryAudit(dispatch, witnessReader);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            audit.VerifyKnownEventAsync(eventId, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            audit.VerifyKnownEventAsync(Guid.NewGuid(), default));

        // A writer or unscoped app login is not an audit identity.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresErasureLiveDeliveryAudit(dispatch, witnessWriter)
                .VerifyKnownEventAsync(eventId, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresErasureLiveDeliveryAudit(app, witnessReader)
                .VerifyKnownEventAsync(eventId, default));

        await new PostgresErasureOutboxWitnessPublisher(dispatch, witnessWriter)
            .PublishBatchAsync(1000, default);
        var acknowledged = await audit.VerifyKnownEventAsync(eventId, default);
        Assert.True(acknowledged > 0);

        // Fresh process/audit sees the same durable acknowledgement;
        // it cannot advance a source cursor or mutate either cluster.
        Assert.Equal(acknowledged,
            await new PostgresErasureLiveDeliveryAudit(dispatch, witnessReader)
                .VerifyKnownEventAsync(eventId, default));
    }

    [Fact]
    public async Task ConflictingWitnessActorForCommittedEventMustFailClosed()
    {
        await using var admin = NpgsqlDataSource.Create(Require("OMNIA_TEST_DB"));
        await new PostgresMigrator(admin, "omnia_app", "omnia_retention",
            "omnia_backfill", "omnia_backfill_audit",
            dispatchRole: "omnia_dispatcher").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(Require("OMNIA_TEST_APP_DB"));
        await using var dispatch = NpgsqlDataSource.Create(Require("OMNIA_TEST_DISPATCH_DB"));
        await using var writer = NpgsqlDataSource.Create(Require("OMNIA_TEST_WITNESS_WRITER_DB"));
        await using var reader = NpgsqlDataSource.Create(Require("OMNIA_TEST_WITNESS_READER_DB"));

        var tenant = "live-mismatch-" + Guid.NewGuid().ToString("N");
        const string subject = "synthetic-scope-audit";
        await new PostgresPersonalDataService(app, TestEnvelopeCrypto.Create())
            .EraseAsync(tenant, subject, default);

        Guid eventId;
        await using (var query = admin.CreateCommand("""
            SELECT id FROM public.erasure_outbox
            WHERE tenant_id = @tenant AND owner_sub = @subject
            """))
        {
            query.Parameters.AddWithValue("tenant", tenant);
            query.Parameters.AddWithValue("subject", subject);
            eventId = (Guid)(await query.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("Expected committed test erasure."));
        }

        await new PostgresErasureWitness(writer).AppendAsync(
            new ErasureDispatchEvent(eventId, tenant, subject + "-forged",
                "erase-actor", DateTimeOffset.UtcNow), default);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PostgresErasureLiveDeliveryAudit(dispatch, reader)
                .VerifyKnownEventAsync(eventId, default));
    }

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Synthetic CI unavailable.");
}
