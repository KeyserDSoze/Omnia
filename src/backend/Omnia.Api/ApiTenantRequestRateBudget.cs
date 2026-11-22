namespace Omnia.Api;

/// <summary>
/// Authenticated /api/v1 aggregate tenant requests/minute budget.
/// Must run AFTER authentication, authorization and the bounded per-actor
/// request budget, BEFORE any JSON buffer allocation or DB endpoint.
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
            context.User.Identity?.IsAuthenticated != true ||
            HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        if (!limiter.TryAcquire(
            ApiRateLimitPolicy.TenantPartitionKey(context), out var retryAfterSeconds))
        {
            await ApiRateBudgetRejection.WriteAsync(context, retryAfterSeconds);
            return;
        }

        await next(context);
    }
}
