namespace Omnia.Api;

/// <summary>
/// One process's bounded 60-second fixed windows, shared by actor and tenant
/// quota adapters. A single lock makes admission and lazy expiry atomic.
/// Optional per-group partition ceilings prevent one signed tenant from
/// monopolizing the global actor window dictionary. No queues or log keys.
/// </summary>
internal sealed class ApiFixedWindowRequestLimiter
{
    private const int WindowSeconds = 60;
    private static readonly TimeSpan WindowDuration = TimeSpan.FromSeconds(WindowSeconds);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly int _requestLimit;
    private readonly int _maxPartitions;
    private readonly int _maximumKeyLength;
    private readonly int? _maxPartitionsPerGroup;
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);
    // Group cardinality is bounded by the number of active window entries;
    // no independent unbounded tenant cache is introduced.
    private readonly Dictionary<string, int> _groupCounts = new(StringComparer.Ordinal);
    private DateTimeOffset _nextSweepAt = DateTimeOffset.MinValue;

    public ApiFixedWindowRequestLimiter(
        int requestLimit, int maxPartitions, int maximumKeyLength,
        TimeProvider? clock, int? maxPartitionsPerGroup = null)
    {
        _requestLimit = requestLimit;
        _maxPartitions = maxPartitions;
        _maximumKeyLength = maximumKeyLength;
        _clock = clock ?? TimeProvider.System;
        _maxPartitionsPerGroup = maxPartitionsPerGroup;
    }

    public int TrackedPartitions
    {
        get { lock (_gate) return _windows.Count; }
    }

    /// <summary>
    /// Called only with identity partitions derived from signed OIDC claims.
    /// Actor calls MUST supply the signed tenant partition as groupKey.
    /// Expired windows and their group counts are removed in the same lock.
    /// </summary>
    public bool TryAcquire(
        string partitionKey, out int retryAfterSeconds, string? groupKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        if (partitionKey.Length > _maximumKeyLength)
            throw new ArgumentException("Rate partition key is too long.",
                nameof(partitionKey));
        if (_maxPartitionsPerGroup.HasValue &&
            (string.IsNullOrWhiteSpace(groupKey) || groupKey.Length > 256))
            throw new ArgumentException("Signed tenant partition is required and bounded.",
                nameof(groupKey));

        retryAfterSeconds = 0;
        lock (_gate)
        {
            // Obtain time AFTER taking the admission lock to avoid a stale
            // timestamp from an earlier, delayed contender.
            var now = _clock.GetUtcNow();
            if (now >= _nextSweepAt)
            {
                RemoveExpired(now);
                _nextSweepAt = now + CleanupInterval;
            }

            if (_windows.TryGetValue(partitionKey, out var current))
            {
                if (!string.Equals(current.GroupKey, groupKey, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Rate partition cannot change its tenant group.");

                if (current.EndsAt <= now)
                {
                    _windows[partitionKey] = new Window(now + WindowDuration, 1, groupKey);
                    return true;
                }

                if (current.Accepted >= _requestLimit)
                {
                    retryAfterSeconds = RetrySeconds(current.EndsAt - now);
                    return false;
                }

                _windows[partitionKey] = current with
                {
                    Accepted = current.Accepted + 1
                };
                return true;
            }

            if (_maxPartitionsPerGroup is int perGroup &&
                _groupCounts.GetValueOrDefault(groupKey!) >= perGroup)
            {
                // Keep existing active actors admitted; new actors in this
                // tenant fail closed, without evicting another tenant.
                retryAfterSeconds = RetrySeconds(_nextSweepAt - now);
                return false;
            }

            if (_windows.Count >= _maxPartitions)
            {
                // No active eviction and no O(N) scan on every 429.
                // Retry-After is a hint, not a promise of capacity.
                retryAfterSeconds = RetrySeconds(_nextSweepAt - now);
                return false;
            }

            _windows.Add(partitionKey, new Window(now + WindowDuration, 1, groupKey));
            if (groupKey is not null)
                _groupCounts[groupKey] = _groupCounts.GetValueOrDefault(groupKey) + 1;
            return true;
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var entry in _windows.Where(x => x.Value.EndsAt <= now).ToArray())
        {
            _windows.Remove(entry.Key);
            if (entry.Value.GroupKey is not { } group)
                continue;
            var remaining = _groupCounts[group] - 1;
            if (remaining == 0)
                _groupCounts.Remove(group);
            else
                _groupCounts[group] = remaining;
        }
    }

    private static int RetrySeconds(TimeSpan remaining) =>
        (int)Math.Clamp(Math.Ceiling(remaining.TotalSeconds), 1, WindowSeconds);

    private readonly record struct Window(
        DateTimeOffset EndsAt, int Accepted, string? GroupKey);
}
