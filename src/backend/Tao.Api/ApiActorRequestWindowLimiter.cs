namespace Tao.Api;

/// <summary>
/// Per-signed-actor 60-second request budgets, with bounded total and
/// per-signed-tenant actor cardinalities. Process-local, not SaaS-wide.
/// </summary>
public sealed class ApiActorRequestWindowLimiter
{
    public const int DefaultRequestsPerMinute = 120;
    public const int MinimumRequestsPerMinute = 1;
    public const int MaximumRequestsPerMinute = 1000;
    public const int DefaultMaxTrackedActors = 8192;
    public const int MaximumMaxTrackedActors = 32_768;
    public const int DefaultMaxTrackedActorsPerTenant = 256;

    private readonly ApiFixedWindowRequestLimiter _limiter;

    public int RequestsPerMinute { get; }
    public int MaxTrackedActors { get; }
    public int MaxTrackedActorsPerTenant { get; }
    public int TrackedActorPartitions => _limiter.TrackedPartitions;

    public ApiActorRequestWindowLimiter(
        int requestsPerMinute = DefaultRequestsPerMinute,
        int maxTrackedActors = DefaultMaxTrackedActors,
        TimeProvider? clock = null,
        int? maxTrackedActorsPerTenant = null)
    {
        if (requestsPerMinute is <
            MinimumRequestsPerMinute or > MaximumRequestsPerMinute)
            throw new InvalidOperationException(
                "RateLimit:RequestsPerMinute must be between 1 and 1000.");
        if (maxTrackedActors is < 1 or > MaximumMaxTrackedActors)
            throw new InvalidOperationException(
                "RateLimit:MaxTrackedActors must be between 1 and 32768.");

        var perTenant = maxTrackedActorsPerTenant ??
            Math.Min(DefaultMaxTrackedActorsPerTenant, maxTrackedActors);
        if (perTenant < 1 || perTenant > maxTrackedActors)
            throw new InvalidOperationException(
                "RateLimit:MaxTrackedActorsPerTenant must be between 1 and MaxTrackedActors.");

        RequestsPerMinute = requestsPerMinute;
        MaxTrackedActors = maxTrackedActors;
        MaxTrackedActorsPerTenant = perTenant;
        _limiter = new ApiFixedWindowRequestLimiter(
            requestsPerMinute, maxTrackedActors, 512, clock, perTenant);
    }

    /// <summary>
    /// Both keys MUST be derived from the same authenticated OIDC identity
    /// via ApiRateLimitPolicy; no client-supplied tenancy selector.
    /// </summary>
    internal bool CanAcquire(
        string actorPartitionKey, string tenantPartitionKey,
        out int retryAfterSeconds) =>
        _limiter.CanAcquire(actorPartitionKey, out retryAfterSeconds, tenantPartitionKey);

    public bool TryAcquire(
        string actorPartitionKey, string tenantPartitionKey,
        out int retryAfterSeconds) =>
        _limiter.TryAcquire(actorPartitionKey, out retryAfterSeconds, tenantPartitionKey);
}
