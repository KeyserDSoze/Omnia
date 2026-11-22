namespace Omnia.Api;

/// <summary>
/// Process-local per-signed-actor fixed 60-second request windows, bounded
/// by a maximum number of concurrently retained partitions. Replaces the
/// former ASP.NET per-actor policy which had no configured cardinality bound.
/// </summary>
public sealed class ApiActorRequestWindowLimiter
{
    public const int DefaultRequestsPerMinute = 120;
    public const int MinimumRequestsPerMinute = 1;
    public const int MaximumRequestsPerMinute = 1000;
    public const int DefaultMaxTrackedActors = 8192;
    public const int MaximumMaxTrackedActors = 32_768;

    private readonly ApiFixedWindowRequestLimiter _limiter;

    public int RequestsPerMinute { get; }
    public int MaxTrackedActors { get; }
    public int TrackedActorPartitions => _limiter.TrackedPartitions;

    public ApiActorRequestWindowLimiter(
        int requestsPerMinute = DefaultRequestsPerMinute,
        int maxTrackedActors = DefaultMaxTrackedActors,
        TimeProvider? clock = null)
    {
        if (requestsPerMinute is <
            MinimumRequestsPerMinute or > MaximumRequestsPerMinute)
            throw new InvalidOperationException(
                "RateLimit:RequestsPerMinute must be between 1 and 1000.");
        if (maxTrackedActors is < 1 or > MaximumMaxTrackedActors)
            throw new InvalidOperationException(
                "RateLimit:MaxTrackedActors must be between 1 and 32768.");

        RequestsPerMinute = requestsPerMinute;
        MaxTrackedActors = maxTrackedActors;
        _limiter = new ApiFixedWindowRequestLimiter(
            requestsPerMinute, maxTrackedActors, 512, clock);
    }

    /// <summary>Key only from ApiRateLimitPolicy.PartitionKey.</summary>
    public bool TryAcquire(string actorPartitionKey, out int retryAfterSeconds) =>
        _limiter.TryAcquire(actorPartitionKey, out retryAfterSeconds);
}
