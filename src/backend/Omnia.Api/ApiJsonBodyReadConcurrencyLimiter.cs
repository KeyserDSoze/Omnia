namespace Omnia.Api;

/// <summary>
/// Bounded process-local, non-queuing admission for JSON body reads.
/// Admission is atomic across the total and per-actor budgets. Actor partitions
/// exist only while a body is being read and are erased when idle.
/// This is NOT a distributed or per-tenant resource quota.
/// </summary>
public sealed class ApiJsonBodyReadConcurrencyLimiter
{
    public const int DefaultMaxConcurrentReads = 32;
    public const int MinimumMaxConcurrentReads = 1;
    public const int MaximumMaxConcurrentReads = 256;
    public const int DefaultMaxConcurrentReadsPerActor = 4;

    // The signed-claim partition key produced by ApiRateLimitPolicy is
    // length-framed and bounded. This extra bound forbids accidental
    // unbounded string retention from a future caller.
    private const int MaximumActorPartitionKeyLength = 512;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _activeByActor =
        new(StringComparer.Ordinal);
    private int _totalActive;

    public int MaxConcurrentReads { get; }
    public int MaxConcurrentReadsPerActor { get; }

    /// <summary>Only the count is exposed: never expose actor keys in diagnostics.</summary>
    public int ActiveActorPartitions
    {
        get { lock (_gate) return _activeByActor.Count; }
    }

    public ApiJsonBodyReadConcurrencyLimiter(
        int maxConcurrentReads, int? maxConcurrentReadsPerActor = null)
    {
        if (maxConcurrentReads is < MinimumMaxConcurrentReads or > MaximumMaxConcurrentReads)
            throw new InvalidOperationException(
                "RequestBody:MaxConcurrentReads must be between 1 and 256.");

        // When operators reduce the global limit below the default actor
        // limit, derive a safe default rather than fail an otherwise valid
        // pre-existing deployment configuration.
        var perActor = maxConcurrentReadsPerActor ??
            Math.Min(DefaultMaxConcurrentReadsPerActor, maxConcurrentReads);
        if (perActor < 1 || perActor > maxConcurrentReads)
            throw new InvalidOperationException(
                "RequestBody:MaxConcurrentReadsPerActor must be between 1 and MaxConcurrentReads.");

        MaxConcurrentReads = maxConcurrentReads;
        MaxConcurrentReadsPerActor = perActor;
    }

    /// <summary>
    /// No waiting/queueing. Call only with ApiRateLimitPolicy.PartitionKey from
    /// the authenticated HttpContext (anonymous fallback: actual peer IP).
    /// </summary>
    public IDisposable? TryAcquire(string actorPartitionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorPartitionKey);
        if (actorPartitionKey.Length > MaximumActorPartitionKeyLength)
            throw new ArgumentException("Actor partition key is too long.",
                nameof(actorPartitionKey));

        lock (_gate)
        {
            if (_totalActive >= MaxConcurrentReads)
                return null;

            _activeByActor.TryGetValue(actorPartitionKey, out var actorActive);
            if (actorActive >= MaxConcurrentReadsPerActor)
                return null;

            _totalActive++;
            _activeByActor[actorPartitionKey] = actorActive + 1;
            return new Permit(this, actorPartitionKey);
        }
    }

    private void Release(string actorPartitionKey)
    {
        lock (_gate)
        {
            var remaining = _activeByActor[actorPartitionKey] - 1;
            if (remaining == 0)
                _activeByActor.Remove(actorPartitionKey);
            else
                _activeByActor[actorPartitionKey] = remaining;
            _totalActive--;
        }
    }

    private sealed class Permit(
        ApiJsonBodyReadConcurrencyLimiter owner, string actorPartitionKey) : IDisposable
    {
        private ApiJsonBodyReadConcurrencyLimiter? _owner = owner;

        public void Dispose()
        {
            // Release once even with the nested using safety net and the
            // explicit early Dispose before downstream endpoint execution.
            Interlocked.Exchange(ref _owner, null)?.Release(actorPartitionKey);
        }
    }
}
