using Microsoft.Extensions.Configuration;
using Npgsql;
using Omnia.Api;
using Omnia.Domain;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// Background retention must observe the SAME external quarantine as HTTP.
/// These tests deliberately model live marker revocation between DB batches.
/// </summary>
public sealed class RecoveryRetentionGateTests
{
    [Fact]
    public async Task UntrustedOrMissingRecoveryStateNeverInvokesThePurge()
    {
        using var marker = new PrivateMarker();
        marker.Write("quarantined\n");
        var config = Config(marker.Path);
        var runner = new RecoveryGuardedRetentionCycle(config, isDevelopment: false);
        var calls = 0;
        async Task<int> Purge(int batch, CancellationToken ct)
        {
            calls++;
            await Task.Yield();
            return batch;
        }

        Assert.Equal(0, await runner.RunAsync(Purge, 1, 3, default));
        Assert.Equal(0, calls);

        marker.Write("ready\ninvalid");
        Assert.Equal(0, await runner.RunAsync(Purge, 1, 3, default));
        Assert.Equal(0, calls);

        File.Delete(marker.Path);
        Assert.Equal(0, await runner.RunAsync(Purge, 1, 3, default));
        Assert.Equal(0, calls);

        var missingConfig = new RecoveryGuardedRetentionCycle(Config(null),
            isDevelopment: false);
        Assert.Equal(0, await missingConfig.RunAsync(Purge, 1, 3, default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RechecksBeforeEachBatchAndStopsAfterLiveRevocation()
    {
        using var marker = new PrivateMarker();
        marker.Write("ready\n");
        var runner = new RecoveryGuardedRetentionCycle(
            Config(marker.Path), isDevelopment: false);
        var calls = 0;
        async Task<int> Purge(int batch, CancellationToken ct)
        {
            await Task.Yield();
            calls++;
            // Simulates an operator revoking release while the first
            // already-authorized SQL batch is completing.
            marker.Write("quarantined\n");
            return batch;
        }

        Assert.Equal(2, await runner.RunAsync(Purge, 2, 5, default));
        Assert.Equal(1, calls); // No second batch after marker revocation.

        marker.Write("ready\n");
        Assert.Equal(0, await runner.RunAsync((_, _) =>
            Task.FromResult(0), 2, 5, default));

        // Preserve the Development-only no-file exception for local tests.
        var dev = new RecoveryGuardedRetentionCycle(Config(null),
            isDevelopment: true);
        Assert.Equal(1, await dev.RunAsync((_, _) =>
            Task.FromResult(1), 1, 1, default));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.RunAsync(Purge, 501, 1, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.RunAsync(Purge, 1, 0, default));
    }

    [Fact]
    public async Task QuarantinePreventsRealPostgresRetentionButReleasePurgesOnlyExpired()
    {
        var adminCs = Environment.GetEnvironmentVariable("OMNIA_TEST_DB");
        var appCs = Environment.GetEnvironmentVariable("OMNIA_TEST_APP_DB");
        var workerCs = Environment.GetEnvironmentVariable("OMNIA_TEST_RETENTION_DB");
        Assert.False(string.IsNullOrWhiteSpace(adminCs));
        Assert.False(string.IsNullOrWhiteSpace(appCs));
        Assert.False(string.IsNullOrWhiteSpace(workerCs));

        await using var admin = NpgsqlDataSource.Create(adminCs!);
        await new PostgresMigrator(admin, "omnia_app", "omnia_retention")
            .MigrateAsync();
        await using var app = NpgsqlDataSource.Create(appCs!);
        await using var retention = NpgsqlDataSource.Create(workerCs!);
        await PostgresRetentionRepository.VerifyWorkerRoleAsync(retention);
        var worker = new PostgresRetentionRepository(retention);

        var tenant = "ret-recovery-" + Guid.NewGuid().ToString("N");
        var owner = "synthetic-only";
        var now = DateTimeOffset.UtcNow;
        var memories = new PostgresMemoryRepository(app, TestEnvelopeCrypto.Create());
        await new PostgresConsentRepository(app).GrantAsync(
            ConsentGrant.Issue(tenant, owner, "manual", ConsentGrant.MemoryCapture,
                "privacy-v1", now, now.AddDays(1)), default);
        var expired = await memories.UpsertAsync(MemoryEntry.Capture(
            tenant, owner, "manual", Guid.NewGuid().ToString("N"),
            "text/plain", "expired synthetic", "{\"test\":true}",
            "expired test", [], now), default);
        var live = await memories.UpsertAsync(MemoryEntry.Capture(
            tenant, owner, "manual", Guid.NewGuid().ToString("N"),
            "text/plain", "active synthetic", "{\"test\":true}",
            "active test", [], now), default);

        try
        {
            await using (var age = admin.CreateCommand(
                "UPDATE public.memory_entries SET expires_at = now() - interval '1 day' WHERE id = @id"))
            {
                age.Parameters.AddWithValue("id", expired.Id);
                Assert.Equal(1, await age.ExecuteNonQueryAsync());
            }

            async Task<long> CountAsync(Guid id)
            {
                await using var check = admin.CreateCommand(
                    "SELECT count(*) FROM public.memory_entries WHERE id = @id");
                check.Parameters.AddWithValue("id", id);
                return (long)(await check.ExecuteScalarAsync() ?? -1L);
            }

            using var marker = new PrivateMarker();
            marker.Write("quarantined\n");
            var cycle = new RecoveryGuardedRetentionCycle(
                Config(marker.Path), isDevelopment: false);
            Assert.Equal(0, await cycle.RunAsync(worker.PurgeBatchAsync, 500, 1, default));
            Assert.Equal(1L, await CountAsync(expired.Id));

            marker.Write("ready\n");
            Assert.True(await cycle.RunAsync(worker.PurgeBatchAsync, 500, 1, default) >= 1);
            Assert.Equal(0L, await CountAsync(expired.Id));
            Assert.Equal(1L, await CountAsync(live.Id));

            marker.Write("quarantined\n");
            Assert.Equal(0, await cycle.RunAsync(worker.PurgeBatchAsync, 500, 1, default));
            Assert.Equal(1L, await CountAsync(live.Id));
        }
        finally
        {
            await using var clean = admin.CreateCommand(
                "DELETE FROM public.memory_entries WHERE id = ANY(@ids)");
            clean.Parameters.AddWithValue("ids", new[] { expired.Id, live.Id });
            await clean.ExecuteNonQueryAsync();
            await using var grants = admin.CreateCommand(
                "DELETE FROM public.consent_grants WHERE tenant_id = @tenant");
            grants.Parameters.AddWithValue("tenant", tenant);
            await grants.ExecuteNonQueryAsync();
        }
    }

    private static IConfiguration Config(string? path) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            path is null ? [] : new Dictionary<string, string?>
            {
                ["Recovery:AccessStateFile"] = path
            }).Build();

    private sealed class PrivateMarker : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "omnia-retention-quarantine-" +
            Guid.NewGuid().ToString("N"));
        public void Write(string content)
        {
            File.WriteAllText(Path, content);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
    }
}
