namespace Omnia.Api;

/// <summary>
/// Process-local fixed 60-second aggregate request budget for an authenticated
/// OIDC tenant. It complements, but never replaces, ASP.NET per-actor limits.
/// Fixed-window edges can burst; it is NOT a distributed rate limiter.
/// </summary>
public sealed class ApiTenantRequestWindowLimiter
{
    public const int DefaultTenantRequestsPerMinute = 600;
    public const int MinimumTenantRequestsPerMinute = 1;
    public const int MaximumTenantRequestsPerMinute = 10_000;
    public const int DefaultMaxTrackedTenants = 4096;
    public const int MaximumMaxTrackedTenants = 16_384;

    private const int MaxPartitionKeyLength = 256;
    private static readonly TimeSpan WindowDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private DateTimeOffset _nextSweepAt = DateTimeOffset.MinValue;

    public int TenantRequestsPerMinute { get; }
    public int MaxTrackedTenants { get; }

    /// <summary>
    /// Stored partition cardinality only; entries expire lazily on subsequent
    /// requests, never via a background task or a durable storage layer.
    /// </summary>
    public int TrackedTenantPartitions
    {
        get { lock (_gate) return _windows.Count; }
    }

    public ApiTenantRequestWindowLimiter(
        int tenantRequestsPerMinute = DefaultTenantRequestsPerMinute,
        int maxTrackedTenants = DefaultMaxTrackedTenants,
        TimeProvider? clock = null)
    {
        if (tenantRequestsPerMinute is <
            MinimumTenantRequestsPerMinute or > MaximumTenantRequestsPerMinute)
            throw new InvalidOperationException(
                "RateLimit:TenantRequestsPerMinute must be between 1 and 10000.");
        if (maxTrackedTenants is < 1 or > MaximumMaxTrackedTenants)
            throw new InvalidOperationException(
                "RateLimit:MaxTrackedTenants must be between 1 and 16384.");

        TenantRequestsPerMinute = tenantRequestsPerMinute;
        MaxTrackedTenants = maxTrackedTenants;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// True if one request is admitted. False means fail-fast 429 with a
    /// generic bounded Retry-After; no waiting, queuing or response PII.
    /// The key MUST come from ApiRateLimitPolicy.TenantPartitionKey(context)
    /// after authentication, NEVER from client headers or a request body.
    /// </summary>
    public bool TryAcquire(string tenantPartitionKey, out int retryAfterSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantPartitionKey);
        if (tenantPartitionKey.Length > MaxPartitionKeyLength)
            throw new ArgumentException("Tenant partition key is too long.",
                nameof(tenantPartitionKey));

        var now = _clock.GetUtcNow();
        retryAfterSeconds = 0;
        lock (_gate)
        {
            // Avoid an O(N) dictionary walk on EVERY rejected request when
            // capacity is full. Sweep at most once per 15s of request traffic;
            // if a full map contains newly expired partitions between sweeps,
            // fail closed briefly until the next sweep, never bypass capacity.
            if (now >= _nextSweepAt)
            {
                RemoveExpired(now);
                _nextSweepAt = now + CleanupInterval;
            }

            if (_windows.TryGetValue(tenantPartitionKey, out var existing))
            {
                if (existing.EndsAt <= now)
                {
                    _windows[tenantPartitionKey] = new Window(now + WindowDuration, 1);
                    return true;
                }
                if (existing.Accepted >= TenantRequestsPerMinute)
                {
                    retryAfterSeconds = RetrySeconds(existing.EndsAt - now);
                    return false;
                }

                _windows[tenantPartitionKey] = existing with
                {
                    Accepted = existing.Accepted + 1
                };
                return true;
            }

            if (_windows.Count >= MaxTrackedTenants)
            {
                // Never evict an active tenant to let a new principal through.
                // A full partition budget fails closed, not with unbounded
                // process memory and not by bypassing the tenant budget.
                retryAfterSeconds = RetrySeconds(
                    _windows.Values.Min(x => x.EndsAt) - now);
                return false;
            }

            _windows.Add(tenantPartitionKey, new Window(now + WindowDuration, 1));
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
        (int)Math.Clamp(Math.Ceiling(remaining.TotalSeconds), 1, 60);

    private readonly record struct Window(DateTimeOffset EndsAt, int Accepted);
}
