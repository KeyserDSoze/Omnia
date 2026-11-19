using Npgsql;
using Omnia.Infrastructure;

namespace Omnia.Api;

/// <summary>
/// Periodic, bounded, no-personal-content logging; uses a dedicated restricted
/// DB principal (not the HTTP API login nor the schema owner).
/// Each SQL call uses SKIP LOCKED, so multiple API replicas cannot double-delete.
/// </summary>
public sealed class RetentionSweepService(
    NpgsqlDataSource retentionDb, ILogger<RetentionSweepService> logger,
    RecoveryGuardedRetentionCycle recoveryCycle)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private const int BatchSize = 500;
    private const int MaxBatchesPerCycle = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunSafelyAsync(stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunSafelyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private async Task RunSafelyAsync(CancellationToken ct)
    {
        try
        {
            var repository = new PostgresRetentionRepository(retentionDb);
            // Recheck the operator-controlled recovery state before each
            // SQL batch. Already-running SQL cannot be preempted by a marker.
            var deleted = await recoveryCycle.RunAsync(
                repository.PurgeBatchAsync, BatchSize, MaxBatchesPerCycle, ct);
            if (deleted > 0)
                logger.LogInformation("Retention sweep removed {Count} expired database notes.", deleted);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            // Deliberately no exception details/SQL/connection strings in logs.
            logger.LogError("Retention sweep failed ({Type}); retry at next interval.",
                e.GetType().Name);
        }
    }
}
