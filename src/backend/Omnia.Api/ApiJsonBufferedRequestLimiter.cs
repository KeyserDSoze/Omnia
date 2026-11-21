namespace Omnia.Api;

/// <summary>
/// Process-local non-queued admission for middleware-owned buffered JSON
/// request lifetimes, including downstream endpoint handlers. One lock
/// changes global, verified-tenant and verified-actor counters atomically.
/// Both partition dictionaries contain ONLY in-flight request identities.
/// NOT a distributed resource quota or reserved-fair scheduler.
/// </summary>
public sealed class ApiJsonBufferedRequestLimiter
{
    public const int DefaultMaxConcurrentBufferedRequests = 64;
    public const int MinimumMaxConcurrentBufferedRequests = 1;
    public const int MaximumMaxConcurrentBufferedRequests = 256;
    public const int DefaultMaxConcurrentBufferedRequestsPerTenant = 16;
    public const int DefaultMaxConcurrentBufferedRequestsPerActor = 4;

    // Jwt-derived tenant/sub partitions are length-framed and bounded;
    // these checks prevent accidental retention of long future caller keys.
    private const int MaximumTenantPartitionKeyLength = 256;
    private const int MaximumActorPartitionKeyLength = 512;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _activeByTenant =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _activeByActor =
        new(StringComparer.Ordinal);
    private int _totalActive;

    public int MaxConcurrentBufferedRequests { get; }
    public int MaxConcurrentBufferedRequestsPerTenant { get; }
    public int MaxConcurrentBufferedRequestsPerActor { get; }

    /// <summary>Only partition cardinalities, never tenant or actor keys.</summary>
    public int ActiveTenantPartitions
    {
        get { lock (_gate) return _activeByTenant.Count; }
    }

    public int ActiveActorPartitions
    {
        get { lock (_gate) return _activeByActor.Count; }
    }

    public ApiJsonBufferedRequestLimiter(
        int maxConcurrentBufferedRequests,
        int? maxConcurrentBufferedRequestsPerTenant = null,
        int? maxConcurrentBufferedRequestsPerActor = null)
    {
        if (maxConcurrentBufferedRequests is <
            MinimumMaxConcurrentBufferedRequests or >
            MaximumMaxConcurrentBufferedRequests)
            throw new InvalidOperationException(
                "RequestBody:MaxConcurrentBufferedRequests must be between 1 and 256.");

        // When global/tenant limits are lowered, absent actor configuration
        // clamps safely. Explicitly inconsistent values fail startup.
        var tenantLimit = maxConcurrentBufferedRequestsPerTenant ??
            Math.Min(DefaultMaxConcurrentBufferedRequestsPerTenant,
                maxConcurrentBufferedRequests);
        if (tenantLimit < 1 || tenantLimit > maxConcurrentBufferedRequests)
            throw new InvalidOperationException(
                "RequestBody:MaxConcurrentBufferedRequestsPerTenant must be between 1 and MaxConcurrentBufferedRequests.");

        var actorLimit = maxConcurrentBufferedRequestsPerActor ??
            Math.Min(DefaultMaxConcurrentBufferedRequestsPerActor, tenantLimit);
        if (actorLimit < 1 || actorLimit > tenantLimit)
            throw new InvalidOperationException(
                "RequestBody:MaxConcurrentBufferedRequestsPerActor must be between 1 and MaxConcurrentBufferedRequestsPerTenant.");

        MaxConcurrentBufferedRequests = maxConcurrentBufferedRequests;
        MaxConcurrentBufferedRequestsPerTenant = tenantLimit;
        MaxConcurrentBufferedRequestsPerActor = actorLimit;
    }

    /// <summary>
    /// Nonblocking admission. Pass ONLY ApiRateLimitPolicy.PartitionKey
    /// and TenantPartitionKey from an already-authenticated HttpContext.
    /// Neither a forwarded identity nor any user-provided header is trusted.
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
            if (_totalActive >= MaxConcurrentBufferedRequests)
                return null;

            _activeByTenant.TryGetValue(tenantPartitionKey, out var tenantActive);
            if (tenantActive >= MaxConcurrentBufferedRequestsPerTenant)
                return null;

            _activeByActor.TryGetValue(actorPartitionKey, out var actorActive);
            if (actorActive >= MaxConcurrentBufferedRequestsPerActor)
                return null;

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
        ApiJsonBufferedRequestLimiter owner,
        string actorPartitionKey, string tenantPartitionKey) : IDisposable
    {
        private ApiJsonBufferedRequestLimiter? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?
                .Release(actorPartitionKey, tenantPartitionKey);
        }
    }
}
