using Npgsql;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// An isolated login can read only bounded metadata through a fixed
/// SECURITY DEFINER function, never raw personal rows or outbox tables.
/// All events in the test are synthetic; there is no delivery ACK yet.
/// </summary>
public sealed class ErasureDispatchTests
{
    [Fact]
    public async Task DedicatedReaderCanPageActualCommittedErasureEvents()
    {
        var adminConnection = Require("OMNIA_TEST_DB");
        var appConnection = Require("OMNIA_TEST_APP_DB");
        var dispatchConnection = Require("OMNIA_TEST_DISPATCH_DB");

        await using var admin = NpgsqlDataSource.Create(adminConnection);
        await new PostgresMigrator(admin, "omnia_app", "omnia_retention",
            "omnia_backfill", "omnia_backfill_audit",
            dispatchRole: "omnia_dispatcher").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(appConnection);
        await using var dispatcher = NpgsqlDataSource.Create(dispatchConnection);
        await PostgresErasureDispatchReader.VerifyRoleAsync(dispatcher);
        var reader = new PostgresErasureDispatchReader(dispatcher);

        var tenant = "dispatch-" + Guid.NewGuid().ToString("N");
        const string subject = "synthetic-owner";
        var now = DateTimeOffset.UtcNow;
        var consents = new PostgresConsentRepository(app);
        await consents.GrantAsync(ConsentGrant.Issue(tenant, subject,
            "manual", ConsentGrant.MemoryCapture, "privacy-v1", now,
            now.AddDays(2)), default);
        await new PostgresMemoryRepository(app, TestEnvelopeCrypto.Create())
            .UpsertAsync(MemoryEntry.Capture(tenant, subject, "manual",
                Guid.NewGuid().ToString("N"), "text/plain",
                "synthetic only", "{\"synthetic\":true}",
                "synthetic only", [], now), default);
        var erase = await new PostgresPersonalDataService(
            app, TestEnvelopeCrypto.Create()).EraseAsync(
                tenant, subject, default);
        Assert.Equal(1, erase.DeletedMemories);
        Assert.Equal(1, erase.DeletedConsents);

        DateTimeOffset? cursorTime = null;
        Guid? cursorId = null;
        var matching = new List<ErasureDispatchEvent>();
        var seen = new HashSet<Guid>();
        for (var page = 0; page < 250; page++)
        {
            var events = await reader.ReadBatchAsync(
                2, cursorTime, cursorId, default);
            Assert.InRange(events.Count, 0, 2);
            foreach (var entry in events)
            {
                Assert.True(seen.Add(entry.Id), "Duplicate event in keyset traversal.");
                Assert.Equal("erase-actor", entry.EventType);
                if (entry.TenantId == tenant && entry.OwnerSubject == subject)
                    matching.Add(entry);
            }
            if (events.Count == 0)
                break;
            cursorTime = events[^1].OccurredAt;
            cursorId = events[^1].Id;
        }
        Assert.Single(matching);
        Assert.InRange(matching[0].OccurredAt,
            now.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(1));

        // This is a read-only primitive, not a durable cursor / ACK.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            { _ = reader.ReadBatchAsync(0, null, null, default); });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            { _ = reader.ReadBatchAsync(101, null, null, default); });
        Assert.Throws<ArgumentException>(() =>
            { _ = reader.ReadBatchAsync(5, DateTimeOffset.UtcNow, null, default); });
        Assert.Throws<ArgumentException>(() =>
            { _ = reader.ReadBatchAsync(5, null, Guid.NewGuid(), default); });
    }

    [Fact]
    public async Task DispatcherCannotReadRawTablesOrMutateAndAppCannotCallDispatcherFunction()
    {
        await using var admin = NpgsqlDataSource.Create(Require("OMNIA_TEST_DB"));
        await new PostgresMigrator(admin, "omnia_app", "omnia_retention",
            "omnia_backfill", "omnia_backfill_audit",
            dispatchRole: "omnia_dispatcher").MigrateAsync();
        await using var dedicated =
            NpgsqlDataSource.Create(Require("OMNIA_TEST_DISPATCH_DB"));
        await using var app =
            NpgsqlDataSource.Create(Require("OMNIA_TEST_APP_DB"));
        await using var retention =
            NpgsqlDataSource.Create(Require("OMNIA_TEST_RETENTION_DB"));
        await using var backfill =
            NpgsqlDataSource.Create(Require("OMNIA_TEST_BACKFILL_DB"));
        await using var auditor =
            NpgsqlDataSource.Create(Require("OMNIA_TEST_AUDIT_DB"));

        foreach (var sql in new[]
        {
            "SELECT count(*) FROM public.erasure_outbox",
            "SELECT count(*) FROM public.memory_entries",
            "SELECT count(*) FROM public.consent_grants",
            "DELETE FROM public.erasure_outbox WHERE false",
            "INSERT INTO public.erasure_outbox (id,tenant_id,owner_sub,event_type) VALUES (gen_random_uuid(),'no','no','erase-actor')"
        })
        {
            await using var command = dedicated.CreateCommand(sql);
            var error = await Assert.ThrowsAsync<PostgresException>(
                () => command.ExecuteNonQueryAsync());
            Assert.Equal("42501", error.SqlState);
        }

        foreach (var other in new[] { app, retention, backfill, auditor })
        {
            await using var command = other.CreateCommand(
                "SELECT * FROM public.omnia_read_erasure_outbox(1, NULL, NULL)");
            var error = await Assert.ThrowsAsync<PostgresException>(
                () => command.ExecuteNonQueryAsync());
            Assert.Equal("42501", error.SqlState);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PostgresErasureDispatchReader.VerifyRoleAsync(other));
        }

        // No public unbounded access; privileged function rejects unsafe
        // direct SQL arguments even for a valid dedicated login.
        foreach (var sql in new[]
        {
            "SELECT * FROM public.omnia_read_erasure_outbox(0, NULL, NULL)",
            "SELECT * FROM public.omnia_read_erasure_outbox(101, NULL, NULL)",
            "SELECT * FROM public.omnia_read_erasure_outbox(1, now(), NULL)"
        })
        {
            await using var command = dedicated.CreateCommand(sql);
            await Assert.ThrowsAsync<PostgresException>(
                () => command.ExecuteNonQueryAsync());
        }
    }

    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        Assert.False(string.IsNullOrWhiteSpace(value), name);
        return value!;
    }
}
