namespace Tao.Api;

/// <summary>
/// Bounded process-local non-queued admission for JSON request body reads.
/// Global, tenant and actor counts change atomically under ONE lock.
/// Actor/tenant partitions exist only while reads are in progress and
/// are immediately deleted when their active count reaches zero.
/// NOT a multi-replica quota or a guaranteed per-tenant reservation.
/// </summary>
public sealed class ApiJsonBodyReadConcurrencyLimiter
{
    public const int DefaultMaxConcurrentReads = 32;
    public const int MinimumMaxConcurrentReads = 1;
    public const int MaximumMaxConcurrentReads = 256;
    public const int DefaultMaxConcurrentReadsPerActor = 4;
    public const int DefaultMaxConcurrentReadsPerTenant = 16;

    // The two keys are derived from one validated OIDC identity. Bounds are
    // defensive and don't retain unbounded strings for future call sites.
    private const int MaximumActorPartitionKeyLength = 512;
    private const int MaximumTenantPartitionKeyLength = 256;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _activeByActor =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _activeByTenant =
        new(StringComparer.Ordinal);
    private int _totalActive;

    public int MaxConcurrentReads { get; }
    public int MaxConcurrentReadsPerActor { get; }
    public int MaxConcurrentReadsPerTenant { get; }

    /// <summary>Count only; never expose the actor key or tenant key.</summary>
    public int ActiveActorPartitions
    {
        get { lock (_gate) return _activeByActor.Count; }
    }

    /// <summary>Count only; never expose the tenant key.</summary>
    public int ActiveTenantPartitions
    {
        get { lock (_gate) return _activeByTenant.Count; }
    }

    public ApiJsonBodyReadConcurrencyLimiter(
        int maxConcurrentReads,
        int? maxConcurrentReadsPerActor = null,
        int? maxConcurrentReadsPerTenant = null)
    {
        if (maxConcurrentReads is < MinimumMaxConcurrentReads or > MaximumMaxConcurrentReads)
            throw new InvalidOperationException(
                "RequestBody:MaxConcurrentReads must be between 1 and 256.");

        var perActor = maxConcurrentReadsPerActor ??
            Math.Min(DefaultMaxConcurrentReadsPerActor, maxConcurrentReads);
        if (perActor < 1 || perActor > maxConcurrentReads)
            throw new InvalidOperationException(
                "RequestBody:MaxConcurrentReadsPerActor must be between 1 and MaxConcurrentReads.");

        var perTenant = maxConcurrentReadsPerTenant ??
            Math.Min(DefaultMaxConcurrentReadsPerTenant, maxConcurrentReads);
        if (perTenant < 1 || perTenant > maxConcurrentReads)
            throw new InvalidOperationException(
                "RequestBody:MaxConcurrentReadsPerTenant must be between 1 and MaxConcurrentReads.");

        MaxConcurrentReads = maxConcurrentReads;
        MaxConcurrentReadsPerActor = perActor;
        MaxConcurrentReadsPerTenant = perTenant;
    }

    /// <summary>
    /// Nonblocking admission. Both partition keys MUST come from the
    /// authenticated HttpContext using ApiRateLimitPolicy; not headers.
    /// </summary>
    public IDisposable? TryAcquire(
        string actorPartitionKey, string tenantPartitionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorPartitionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantPartitionKey);
        if (actorPartitionKey.Length > MaximumActorPartitionKeyLength)
            throw new ArgumentException("Actor partition key is too long.",
                nameof(actorPartitionKey));
        if (tenantPartitionKey.Length > MaximumTenantPartitionKeyLength)
            throw new ArgumentException("Tenant partition key is too long.",
                nameof(tenantPartitionKey));

        lock (_gate)
        {
            if (_totalActive >= MaxConcurrentReads)
                return null;

            _activeByTenant.TryGetValue(tenantPartitionKey, out var tenantActive);
            if (tenantActive >= MaxConcurrentReadsPerTenant)
                return null;

            _activeByActor.TryGetValue(actorPartitionKey, out var actorActive);
            if (actorActive >= MaxConcurrentReadsPerActor)
                return null;

            // All three counters are incremented together, or not at all.
            _totalActive++;
            _activeByTenant[tenantPartitionKey] = tenantActive + 1;
            _activeByActor[actorPartitionKey] = actorActive + 1;
            return new Permit(this, actorPartitionKey, tenantPartitionKey);
        }
    }

    private void Release(string actorPartitionKey, string tenantPartitionKey)
    {
        lock (_gate)
        {
            var actorRemaining = _activeByActor[actorPartitionKey] - 1;
            if (actorRemaining == 0)
                _activeByActor.Remove(actorPartitionKey);
            else
                _activeByActor[actorPartitionKey] = actorRemaining;

            var tenantRemaining = _activeByTenant[tenantPartitionKey] - 1;
            if (tenantRemaining == 0)
                _activeByTenant.Remove(tenantPartitionKey);
            else
                _activeByTenant[tenantPartitionKey] = tenantRemaining;

            _totalActive--;
        }
    }

    private sealed class Permit(
        ApiJsonBodyReadConcurrencyLimiter owner,
        string actorPartitionKey, string tenantPartitionKey) : IDisposable
    {
        private ApiJsonBodyReadConcurrencyLimiter? _owner = owner;

        public void Dispose()
        {
            // Safe both on early Dispose before the handler and later using
            // cleanup, as well as on exceptions, 408/413 and disconnects.
            Interlocked.Exchange(ref _owner, null)?
                .Release(actorPartitionKey, tenantPartitionKey);
        }
    }
}
