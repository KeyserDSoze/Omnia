namespace Omnia.Api;

/// <summary>
/// One process's bounded 60-second fixed windows, shared by actor and tenant
/// quota adapters. A single lock makes admission and lazy expiry atomic.
/// Never queue, retain credentials, log identities or grow beyond max keys.
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
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private DateTimeOffset _nextSweepAt = DateTimeOffset.MinValue;

    public ApiFixedWindowRequestLimiter(
        int requestLimit, int maxPartitions, int maximumKeyLength, TimeProvider? clock)
    {
        _requestLimit = requestLimit;
        _maxPartitions = maxPartitions;
        _maximumKeyLength = maximumKeyLength;
        _clock = clock ?? TimeProvider.System;
    }

    public int TrackedPartitions
    {
        get { lock (_gate) return _windows.Count; }
    }

    /// <summary>
    /// Called only with signed-claims-derived actor or tenant partitions.
    /// Expired keys are removed lazily once each 15s on request traffic,
    /// never on a background task or full O(N) scan for every 429 denial.
    /// </summary>
    public bool TryAcquire(string partitionKey, out int retryAfterSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        if (partitionKey.Length > _maximumKeyLength)
            throw new ArgumentException("Rate partition key is too long.",
                nameof(partitionKey));

        var now = _clock.GetUtcNow();
        retryAfterSeconds = 0;
        lock (_gate)
        {
            if (now >= _nextSweepAt)
            {
                RemoveExpired(now);
                _nextSweepAt = now + CleanupInterval;
            }

            if (_windows.TryGetValue(partitionKey, out var current))
            {
                if (current.EndsAt <= now)
                {
                    _windows[partitionKey] = new Window(now + WindowDuration, 1);
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

            if (_windows.Count >= _maxPartitions)
            {
                // Never evict an active actor/tenant to admit a new key.
                // Hint when the next cleanup opportunity arrives; avoid
                // scanning O(N) partitions on every denial under attack.
                // The hint is not a guarantee that capacity will be free.
                retryAfterSeconds = RetrySeconds(_nextSweepAt - now);
                return false;
            }

            _windows.Add(partitionKey, new Window(now + WindowDuration, 1));
            return true;
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var key in _windows
            .Where(x => x.Value.EndsAt <= now)
            .Select(x => x.Key).ToArray())
            _windows.Remove(key);
    }

    private static int RetrySeconds(TimeSpan remaining) =>
        (int)Math.Clamp(Math.Ceiling(remaining.TotalSeconds), 1, WindowSeconds);

    private readonly record struct Window(DateTimeOffset EndsAt, int Accepted);
}
