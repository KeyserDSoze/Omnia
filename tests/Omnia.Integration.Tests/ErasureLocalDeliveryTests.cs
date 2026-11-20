using System.Security.Cryptography;
using Npgsql;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Demonstrates read from an isolated SQL role followed by a locally
/// authenticated journal append. A local file is NOT a trusted offsite
/// delivery or a durable/monotonic recovery authority.
/// </summary>
public sealed class ErasureLocalDeliveryTests
{
    [Fact]
    public async Task ActualCommittedOutboxEventIsPublishedWithStableUuidAndRetryIsIdempotent()
    {
        await using var admin = NpgsqlDataSource.Create(Require("OMNIA_TEST_DB"));
        await new PostgresMigrator(admin, "omnia_app", "omnia_retention",
            "omnia_backfill", "omnia_backfill_audit",
            dispatchRole: "omnia_dispatcher").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(Require("OMNIA_TEST_APP_DB"));
        await using var dispatch = NpgsqlDataSource.Create(Require("OMNIA_TEST_DISPATCH_DB"));
        using var files = new PrivateFiles();

        var tenant = "dispatch-delivery-" + Guid.NewGuid().ToString("N");
        const string actor = "synthetic-only";
        var now = DateTimeOffset.UtcNow;
        await new PostgresConsentRepository(app).GrantAsync(
            ConsentGrant.Issue(tenant, actor, "manual",
                ConsentGrant.MemoryCapture, "privacy-v1", now, now.AddDays(2)),
            default);
        await new PostgresMemoryRepository(app, TestEnvelopeCrypto.Create())
            .UpsertAsync(MemoryEntry.Capture(tenant, actor, "manual",
                Guid.NewGuid().ToString("N"), "text/plain",
                "synthetic-only", "{\"synthetic\":true}",
                "synthetic-only", [], now), default);

        var deleted = await new PostgresPersonalDataService(
            app, TestEnvelopeCrypto.Create()).EraseAsync(tenant, actor, default);
        Assert.Equal(1, deleted.DeletedMemories);

        Guid durableEventId;
        await using (var sql = admin.CreateCommand("""
            SELECT id FROM public.erasure_outbox
            WHERE tenant_id = @tenant AND owner_sub = @owner
            """))
        {
            sql.Parameters.AddWithValue("tenant", tenant);
            sql.Parameters.AddWithValue("owner", actor);
            durableEventId = (Guid)(await sql.ExecuteScalarAsync() ??
                throw new InvalidOperationException("Missing synthetic outbox event."));
        }

        var sender = new PostgresErasureOutboxLocalPublisher(
            dispatch, files.Journal, files.Checkpoint,
            files.JournalKey, files.CheckpointKey);
        var firstCount = await sender.PublishAllAsync(maxEvents: 500, default);
        Assert.True(firstCount >= 1);
        var state = files.Verify();
        Assert.Equal(firstCount, state.Count);
        var journal = File.ReadAllBytes(files.Journal);
        Assert.Contains(durableEventId.ToString("N"), File.ReadAllText(files.Journal),
            StringComparison.Ordinal);

        // The publisher rescans the source from the beginning on every run,
        // since timestamp-based commit order cannot be used as an ACK. A
        // re-delivery of the same durable event ID must NOT append twice.
        var second = await sender.PublishAllAsync(maxEvents: 500, default);
        Assert.Equal(firstCount, second);
        Assert.Equal(journal, File.ReadAllBytes(files.Journal));
        Assert.Equal(firstCount, files.Verify().Count);

        // A checkpoint failure must block publication. The source outbox
        // is untouched and can be retried after operator recovery.
        File.WriteAllText(files.Checkpoint, "{}");
        files.Secure(files.Checkpoint);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            sender.PublishAllAsync(maxEvents: 500, default));
        Assert.Equal(journal, File.ReadAllBytes(files.Journal));
        await using var remains = admin.CreateCommand("""
            SELECT count(*) FROM public.erasure_outbox WHERE id = @id
            """);
        remains.Parameters.AddWithValue("id", durableEventId);
        Assert.Equal(1L, (long)(await remains.ExecuteScalarAsync() ?? -1L));
    }

    [Fact]
    public async Task WrongRoleCannotPublishEvenWithValidLocalSigningKeys()
    {
        await using var admin = NpgsqlDataSource.Create(Require("OMNIA_TEST_DB"));
        await new PostgresMigrator(admin, "omnia_app", "omnia_retention",
            "omnia_backfill", "omnia_backfill_audit",
            dispatchRole: "omnia_dispatcher").MigrateAsync();
        await using var app = NpgsqlDataSource.Create(Require("OMNIA_TEST_APP_DB"));
        using var files = new PrivateFiles();
        var sender = new PostgresErasureOutboxLocalPublisher(
            app, files.Journal, files.Checkpoint,
            files.JournalKey, files.CheckpointKey);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.PublishAllAsync(maxEvents: 500, default));
        Assert.False(File.Exists(files.Journal));
        Assert.False(File.Exists(files.Checkpoint));
    }

    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        Assert.False(string.IsNullOrWhiteSpace(value), name);
        return value!;
    }

    private sealed class PrivateFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), "omnia-dispatch-fixture-" + Guid.NewGuid().ToString("N"));
        public string Journal => Path.Combine(_directory, "journal.jsonl");
        public string Checkpoint => Path.Combine(_directory, "checkpoint.json");
        public string JournalKey => Path.Combine(_directory, "journal.key");
        public string CheckpointKey => Path.Combine(_directory, "checkpoint.key");

        public PrivateFiles()
        {
            Directory.CreateDirectory(_directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            WritePrivate(JournalKey, RandomNumberGenerator.GetBytes(32));
            WritePrivate(CheckpointKey, RandomNumberGenerator.GetBytes(32));
        }

        public RecoveryJournalVerifier.Verification Verify() =>
            RecoveryJournalFileAudit.VerifyFiles(
                Journal, Checkpoint, JournalKey, CheckpointKey);

        public void Secure(string path)
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        private void WritePrivate(string path, byte[] raw)
        {
            File.WriteAllBytes(path, raw);
            Secure(path);
            CryptographicOperations.ZeroMemory(raw);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }
}
