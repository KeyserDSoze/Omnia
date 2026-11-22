namespace Omnia.Api;

/// <summary>
/// Process-local 60-second aggregate tenant budget. Shared bounded fixed
/// window engine with the per-actor limiter (#167 / ADR 0066).
/// Not a distributed or guaranteed-fair quota.
/// </summary>
public sealed class ApiTenantRequestWindowLimiter
{
    public const int DefaultTenantRequestsPerMinute = 600;
    public const int MinimumTenantRequestsPerMinute = 1;
    public const int MaximumTenantRequestsPerMinute = 10_000;
    public const int DefaultMaxTrackedTenants = 4096;
    public const int MaximumMaxTrackedTenants = 16_384;

    private readonly ApiFixedWindowRequestLimiter _limiter;

    public int TenantRequestsPerMinute { get; }
    public int MaxTrackedTenants { get; }
    public int TrackedTenantPartitions => _limiter.TrackedPartitions;

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
        _limiter = new ApiFixedWindowRequestLimiter(
            tenantRequestsPerMinute, maxTrackedTenants, 256, clock);
    }

    /// <summary>Key only from ApiRateLimitPolicy.TenantPartitionKey.</summary>
    public bool TryAcquire(string tenantPartitionKey, out int retryAfterSeconds) =>
        _limiter.TryAcquire(tenantPartitionKey, out retryAfterSeconds);
}
