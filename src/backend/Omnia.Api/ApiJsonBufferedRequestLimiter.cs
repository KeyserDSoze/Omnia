namespace Omnia.Api;

/// <summary>
/// Process-local, non-queued admission for middleware-owned buffered JSON
/// request lifetimes, INCLUDING downstream handler execution. The global
/// and signed-tenant counters are atomic and only active tenant keys exist.
/// This is not a distributed quota, guaranteed reservation or total DB cap.
/// </summary>
public sealed class ApiJsonBufferedRequestLimiter
{
    public const int DefaultMaxConcurrentBufferedRequests = 64;
    public const int MinimumMaxConcurrentBufferedRequests = 1;
    public const int MaximumMaxConcurrentBufferedRequests = 256;
    public const int DefaultMaxConcurrentBufferedRequestsPerTenant = 16;

    private const int MaximumTenantPartitionKeyLength = 256;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _activeByTenant =
        new(StringComparer.Ordinal);
    private int _totalActive;

    public int MaxConcurrentBufferedRequests { get; }
    public int MaxConcurrentBufferedRequestsPerTenant { get; }

    /// <summary>Only the count is exposed, never signed tenant key values.</summary>
    public int ActiveTenantPartitions
    {
        get { lock (_gate) return _activeByTenant.Count; }
    }

    public ApiJsonBufferedRequestLimiter(
        int maxConcurrentBufferedRequests,
        int? maxConcurrentBufferedRequestsPerTenant = null)
    {
        if (maxConcurrentBufferedRequests is <
            MinimumMaxConcurrentBufferedRequests or >
            MaximumMaxConcurrentBufferedRequests)
            throw new InvalidOperationException(
                "RequestBody:MaxConcurrentBufferedRequests must be between 1 and 256.");

        // A lower global limit remains a safe configuration without requiring
        // a second explicit override. Invalid explicit tenant caps fail closed.
        var tenantLimit = maxConcurrentBufferedRequestsPerTenant ??
            Math.Min(DefaultMaxConcurrentBufferedRequestsPerTenant,
                maxConcurrentBufferedRequests);
        if (tenantLimit < 1 || tenantLimit > maxConcurrentBufferedRequests)
            throw new InvalidOperationException(
                "RequestBody:MaxConcurrentBufferedRequestsPerTenant must be between 1 and MaxConcurrentBufferedRequests.");

        MaxConcurrentBufferedRequests = maxConcurrentBufferedRequests;
        MaxConcurrentBufferedRequestsPerTenant = tenantLimit;
    }

    /// <summary>
    /// Admit without blocking. Pass ONLY ApiRateLimitPolicy.TenantPartitionKey
    /// computed from an already-authenticated HttpContext (anonymous peer
    /// fallback); never a tenant/client/forwarded header.
    /// </summary>
    public IDisposable? TryAcquire(string tenantPartitionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantPartitionKey);
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

            // One atomic capacity transaction: accept both or neither.
            _totalActive++;
            _activeByTenant[tenantPartitionKey] = tenantActive + 1;
            return new Permit(this, tenantPartitionKey);
        }
    }

    private void Release(string tenantPartitionKey)
    {
        lock (_gate)
        {
            var remaining = _activeByTenant[tenantPartitionKey] - 1;
            if (remaining == 0)
                _activeByTenant.Remove(tenantPartitionKey);
            else
                _activeByTenant[tenantPartitionKey] = remaining;
            _totalActive--;
        }
    }

    private sealed class Permit(
        ApiJsonBufferedRequestLimiter owner, string tenantPartitionKey) : IDisposable
    {
        private ApiJsonBufferedRequestLimiter? _owner = owner;

        public void Dispose()
        {
            // Disposed once even on double Dispose or exception paths.
            Interlocked.Exchange(ref _owner, null)?.Release(tenantPartitionKey);
        }
    }
}
