namespace Omnia.Api;

/// <summary>
/// One process-local synchronized admission for signed OIDC actor AND tenant.
/// No counter or fresh partition is spent unless both windows can admit.
/// The standalone facades remain for narrow tests, but production must call
/// ONLY this instance for authenticated API request-rate admission.
/// </summary>
public sealed class ApiAtomicRequestWindowLimiter
{
    private readonly object _gate = new();
    private readonly ApiActorRequestWindowLimiter _actors;
    private readonly ApiTenantRequestWindowLimiter _tenants;

    public ApiAtomicRequestWindowLimiter(
        ApiActorRequestWindowLimiter actors,
        ApiTenantRequestWindowLimiter tenants)
    {
        _actors = actors ?? throw new ArgumentNullException(nameof(actors));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
    }

    /// <summary>
    /// Both framed keys must be derived from the same authenticated principal.
    /// All production callers are serialized here across both probes and
    /// commits, so a denied request cannot spend the other minute budget.
    /// </summary>
    public bool TryAcquire(
        string actorPartitionKey, string tenantPartitionKey,
        out int retryAfterSeconds)
    {
        lock (_gate)
        {
            if (!_actors.CanAcquire(
                    actorPartitionKey, tenantPartitionKey, out retryAfterSeconds) ||
                !_tenants.CanAcquire(tenantPartitionKey, out retryAfterSeconds))
                return false;

            // No other production path can change either limiter while this
            // coordinator lock is held. The single-budget entry points remain
            // available only to pre-existing isolated tests.
            if (!_actors.TryAcquire(
                    actorPartitionKey, tenantPartitionKey, out _) ||
                !_tenants.TryAcquire(tenantPartitionKey, out _))
                throw new InvalidOperationException(
                    "Rate budget changed during synchronized admission.");

            retryAfterSeconds = 0;
            return true;
        }
    }
}
