using System.Security.Claims;

namespace Omnia.Api;

/// <summary>
/// Stable per-request rate-limit key. Only authenticated claims issued by
/// the configured OIDC/JWKS provider are accepted as an identity.
/// </summary>
public static class ApiRateLimitPolicy
{
    public static string PartitionKey(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var subjects = context.User.FindAll("sub").Select(c => c.Value).ToArray();
            var tenants = context.User.FindAll("tenant_id").Select(c => c.Value).ToArray();
            if (subjects.Length == 1 && tenants.Length == 1 &&
                !string.IsNullOrWhiteSpace(subjects[0]) &&
                !string.IsNullOrWhiteSpace(tenants[0]) &&
                subjects[0].Length <= 255 && tenants[0].Length <= 128)
            {
                // Length framing prevents a concatenation collision if claims contain ':'.
                return $"account:{tenants[0].Length}:{tenants[0]}:{subjects[0].Length}:{subjects[0]}";
            }
        }
        // Never trust an unconfigured forwarded IP header: this peer address is
        // a safety net for invalid/anonymous access, not a public IP identity.
        return $"anonymous:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }
}
