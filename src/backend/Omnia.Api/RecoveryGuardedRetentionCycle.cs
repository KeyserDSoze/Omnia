using Microsoft.Extensions.Configuration;

namespace Omnia.Api;

/// <summary>
/// Cooperative stop for the in-process retention worker while the recovered
/// database is quarantined. Checks the operator's external state immediately
/// before EVERY bounded SQL batch, not only at host startup.
///
/// This is not a distributed lease or a transaction-level fence: a SQL batch
/// already in progress can finish after quarantine is activated.
/// </summary>
public sealed class RecoveryGuardedRetentionCycle(
    IConfiguration configuration, bool isDevelopment)
{
    public async Task<int> RunAsync(
        Func<int, CancellationToken, Task<int>> purge,
        int batchSize, int maxBatchesPerCycle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(purge);
        if (batchSize is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (maxBatchesPerCycle is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(maxBatchesPerCycle));

        var deleted = 0;
        for (var i = 0; i < maxBatchesPerCycle; i++)
        {
            ct.ThrowIfCancellationRequested();
            // No datasource commands are issued while the file is absent,
            // unreadable, invalid or explicitly quarantined.
            if (!RecoveryQuarantineGate.IsReady(configuration, isDevelopment))
                return deleted;

            var count = await purge(batchSize, ct);
            if (count < 0 || count > batchSize)
                throw new InvalidOperationException(
                    "The retention routine returned an invalid bounded count.");
            deleted += count;
            if (count < batchSize) return deleted;
        }
        return deleted;
    }
}
