namespace Omnia.Api;

/// <summary>
/// Stable identity partitions for request rate limiting and JSON body
/// receive admission. Authentication comes from configured OIDC/JWKS
/// middleware, not HTTP header values supplied by clients.
/// </summary>
public static class ApiRateLimitPolicy
{
    public static string PartitionKey(HttpContext context)
    {
        if (TryGetAuthenticatedClaims(context, out var tenant, out var subject))
        {
            // Length framing prevents collisions when claims include ':'.
            return $"account:{tenant.Length}:{tenant}:{subject.Length}:{subject}";
        }
        return AnonymousPartitionKey(context);
    }

    /// <summary>
    /// Every signed subject within one tenant receives the SAME bounded
    /// tenant partition; subjects in different tenants never share one.
    /// </summary>
    public static string TenantPartitionKey(HttpContext context)
    {
        if (TryGetAuthenticatedClaims(context, out var tenant, out _))
            return $"tenant:{tenant.Length}:{tenant}";
        // No forwarded identity is trusted. Invalid/missing claims use the
        // same direct-peer fallback as the per-actor rate limiter.
        return AnonymousPartitionKey(context);
    }

    private static bool TryGetAuthenticatedClaims(
        HttpContext context, out string tenant, out string subject)
    {
        tenant = "";
        subject = "";
        if (context.User.Identity?.IsAuthenticated != true)
            return false;

        var subjects = context.User.FindAll("sub").Select(c => c.Value).ToArray();
        var tenants = context.User.FindAll("tenant_id").Select(c => c.Value).ToArray();
        if (subjects.Length != 1 || tenants.Length != 1 ||
            string.IsNullOrWhiteSpace(subjects[0]) ||
            string.IsNullOrWhiteSpace(tenants[0]) ||
            subjects[0].Length > 255 || tenants[0].Length > 128)
            return false;

        tenant = tenants[0];
        subject = subjects[0];
        return true;
    }

    private static string AnonymousPartitionKey(HttpContext context) =>
        $"anonymous:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
}
