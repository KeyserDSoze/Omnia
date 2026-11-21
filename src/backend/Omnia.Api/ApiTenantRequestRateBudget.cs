namespace Omnia.Api;

/// <summary>
/// Authenticated /api/v1 aggregate tenant requests/minute budget.
/// Must run AFTER authentication, the existing per-actor ASP.NET limiter
/// and authorization, BEFORE any JSON buffer allocation or DB endpoint.
/// </summary>
public static class ApiTenantRequestRateBudget
{
    public static async Task InvokeAsync(
        HttpContext context, RequestDelegate next,
        ApiTenantRequestWindowLimiter limiter)
    {
        // No change to /health, /ready, OPTIONS preflight, anonymous
        // challenges or future public routes. Authentication and authz
        // remain upstream; fake headers never become OIDC identities.
        if (!context.Request.Path.StartsWithSegments("/api/v1") ||
            context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        if (!limiter.TryAcquire(
            ApiRateLimitPolicy.TenantPartitionKey(context), out var retryAfterSeconds))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Retry-After"] =
                retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await context.Response.WriteAsJsonAsync(
                new { error = "rate_limited" }, cancellationToken: context.RequestAborted);
            return;
        }

        await next(context);
    }
}
