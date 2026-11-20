using System.Security.Cryptography;
using Npgsql;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Synthetic CI-only: independent one-shot execution claim on THIRD PGDATA.
/// Losing the primary outbox in a source PITR must NOT make a request reusable.
/// A claimed request does not mean the primary DELETE ever committed.
/// </summary>
public sealed class PreEraseExecutionClaimTests
{
    [Fact]
    public async Task RemoteSingleUseClaimRefusesReplaysEvenWhenSourceHistoryIsMissing()
    {
        await using var admin = NpgsqlDataSource.Create(Required("OMNIA_TEST_DB"));
        await new PostgresMigrator(admin, "omnia_app", "omnia_retention",
            "omnia_backfill", "omnia_backfill_audit",
            dispatchRole: "omnia_dispatcher").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(Required("OMNIA_TEST_APP_DB"));
        await using var intentWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_WRITER_DB"));
        await using var intentReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_INTENT_READER_DB"));
        await using var anchorWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_CLAIM_ANCHOR_WRITER_DB"));
        await using var anchorReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_CLAIM_ANCHOR_READER_DB"));
        await using var claimWriter = NpgsqlDataSource.Create(Required("OMNIA_TEST_EXECUTION_CLAIM_WRITER_DB"));
        await using var claimReader = NpgsqlDataSource.Create(Required("OMNIA_TEST_EXECUTION_CLAIM_READER_DB"));
        await using var claimAdmin = NpgsqlDataSource.Create(Required("OMNIA_TEST_EXECUTION_CLAIM_ADMIN_DB"));
        await using var witnessAdmin = NpgsqlDataSource.Create(Required("OMNIA_TEST_WITNESS_ADMIN_DB"));
        await using (var a = claimAdmin.CreateCommand("SELECT system_identifier FROM pg_control_system()"))
        await using (var w = witnessAdmin.CreateCommand("SELECT system_identifier FROM pg_control_system()"))
            Assert.NotEqual(await a.ExecuteScalarAsync(), await w.ExecuteScalarAsync());

        var tenant = "one-shot-" + Guid.NewGuid().ToString("N");
        const string subject = "synthetic-claim-actor";
        var now = DateTimeOffset.UtcNow;
        var crypto = TestEnvelopeCrypto.Create();
        var memory = new PostgresMemoryRepository(app, crypto);
        var consents = new PostgresConsentRepository(app);
        var eraser = new PostgresPersonalDataService(app, crypto);
        await consents.GrantAsync(ConsentGrant.Issue(tenant, subject, "manual",
            ConsentGrant.MemoryCapture, "privacy-v1", now, now.AddDays(2)), default);
        await memory.UpsertAsync(MemoryEntry.Capture(tenant, subject, "manual",
            Guid.NewGuid().ToString("N"), "text/plain", "synthetic",
            "{\"synthetic\":true}", "one-shot source note", [], now), default);

        var id = Guid.NewGuid();
        var key = RandomNumberGenerator.GetBytes(32);
        await new PostgresPreEraseIntentWriter(intentWriter).RecordAsync(
            id, tenant, subject, default);
        await new PostgresPreEraseIntentAnchorIssuer(intentReader, anchorWriter,
            anchorReader).BootstrapAsync(key, operatorApproved:true, default);

        var writer = new PostgresPreEraseExecutionClaimWriter(claimWriter);
        var reader = new PostgresPreEraseExecutionClaimReader(claimReader);
        var service = new PostgresPersonalDataService(app, crypto);

        // Incorrect independent claim role and missing/invalid intent must
        // be rejected BEFORE consuming a claim or deleting source actor.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EraseWithOneShotAnchoredIntentAsync(id, tenant, subject,
                intentReader, anchorReader, claimReader, key, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.EraseWithOneShotAnchoredIntentAsync(
                Guid.NewGuid(), tenant, subject, intentReader, anchorReader,
                claimWriter, key, default));
        Assert.False(await reader.HasClaimAsync(id, default));
        Assert.Single(await memory.RecentAsync(tenant, subject, 10, default));

        var first = await service.EraseWithOneShotAnchoredIntentAsync(id, tenant,
            subject, intentReader, anchorReader, claimWriter, key, default);
        Assert.Equal(1, first.DeletedMemories);
        Assert.Equal(1, first.DeletedConsents);
        Assert.True(await reader.HasClaimAsync(id, default));
        Assert.Empty(await memory.RecentAsync(tenant, subject, 10, default));

        await using (var query = admin.CreateCommand("""
            SELECT count(*) FROM public.erasure_outbox
             WHERE id=@id AND tenant_id=@tenant AND owner_sub=@owner
            """))
        {
            query.Parameters.AddWithValue("id", id);
            query.Parameters.AddWithValue("tenant", tenant);
            query.Parameters.AddWithValue("owner", subject);
            Assert.Equal(1L, (long)(await query.ExecuteScalarAsync()
                ?? throw new InvalidDataException("Outbox missing.")));
        }

        // This remote attempt is consumed independently of the primary
        // transactional outbox and remains consumed even after source PITR.
        await consents.GrantAsync(ConsentGrant.Issue(tenant, subject, "manual",
            ConsentGrant.MemoryCapture, "privacy-v2", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(2)), default);
        await memory.UpsertAsync(MemoryEntry.Capture(tenant, subject, "manual",
            Guid.NewGuid().ToString("N"), "text/plain", "new synthetic",
            "{\"synthetic\":\"renewed\"}", "new source note", [],
            DateTimeOffset.UtcNow), default);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
            service.EraseWithOneShotAnchoredIntentAsync(id, tenant, subject,
                intentReader, anchorReader, claimWriter, key, default));
        Assert.Equal("23505", duplicate.SqlState);
        Assert.Single(await memory.RecentAsync(tenant, subject, 10, default));
        Assert.Single((await consents.ListAsync(tenant, subject, default))
            .Where(x => x.RevokedAt is null));

        // An attempt can be consumed WITHOUT a successful primary COMMIT.
        // This must be conservatively retained as uncertain, NOT retried.
        var orphan = Guid.NewGuid();
        await new PostgresPreEraseIntentWriter(intentWriter).RecordAsync(
            orphan, tenant, subject, default);
        await new PostgresPreEraseIntentAnchorIssuer(intentReader,
            anchorWriter, anchorReader).IssueAsync(key, default);
        await writer.ClaimOnceAsync(orphan, tenant, subject, key, default);
        Assert.True(await reader.HasClaimAsync(orphan, default));
        var uncommitted = await Assert.ThrowsAsync<PostgresException>(() =>
            writer.ClaimOnceAsync(orphan, tenant, subject, key, default));
        Assert.Equal("23505", uncommitted.SqlState);

        // Intent writer/reader and owner/admin never substitute the distinct
        // one-shot SQL capability or read raw tables.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresPreEraseExecutionClaimWriter(intentWriter)
                .ClaimOnceAsync(Guid.NewGuid(), tenant, subject, key, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PostgresPreEraseExecutionClaimReader(claimAdmin)
                .HasClaimAsync(id, default));
        await using var raw = claimReader.CreateCommand(
            "SELECT count(*) FROM admission.execution_claims");
        var forbidden = await Assert.ThrowsAsync<PostgresException>(() =>
            raw.ExecuteScalarAsync());
        Assert.Equal("42501", forbidden.SqlState);
    }

    [Fact]
    public async Task ConcurrentSameUuidClaimsAllowExactlyOneDurableAttempt()
    {
        await using var writerDb = NpgsqlDataSource.Create(
            Required("OMNIA_TEST_EXECUTION_CLAIM_WRITER_DB"));
        await using var readerDb = NpgsqlDataSource.Create(
            Required("OMNIA_TEST_EXECUTION_CLAIM_READER_DB"));
        var writer = new PostgresPreEraseExecutionClaimWriter(writerDb);
        var id = Guid.NewGuid();
        var key = RandomNumberGenerator.GetBytes(32);
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(async _ =>
            {
                try
                {
                    await writer.ClaimOnceAsync(id,
                        "concurrent-claim-test", "synthetic-owner", key, default);
                    return true;
                }
                catch (PostgresException ex) when (ex.SqlState == "23505")
                {
                    return false;
                }
            }));
        Assert.Single(results.Where(x => x));
        Assert.True(await new PostgresPreEraseExecutionClaimReader(readerDb)
            .HasClaimAsync(id, default));

        await using var invalid = writerDb.CreateCommand(
            "SELECT admission.claim_once(@id,@scope)");
        invalid.Parameters.AddWithValue("id", Guid.NewGuid());
        invalid.Parameters.AddWithValue("scope", new byte[31]);
        var invalidScope = await Assert.ThrowsAsync<PostgresException>(() =>
            invalid.ExecuteScalarAsync());
        Assert.Equal("22023", invalidScope.SqlState);

        await using var unauthorizedRead = writerDb.CreateCommand(
            "SELECT admission.has_claim(@id)");
        unauthorizedRead.Parameters.AddWithValue("id", id);
        var denied = await Assert.ThrowsAsync<PostgresException>(() =>
            unauthorizedRead.ExecuteScalarAsync());
        Assert.Equal("42501", denied.SqlState);
    }

    private static string Required(string key) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Synthetic CI connection missing.");
}
