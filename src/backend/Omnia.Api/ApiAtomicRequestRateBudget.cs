namespace Omnia.Api;

/// <summary>
/// Atomic signed actor + tenant request quota, after authentication and
/// authorization, before buffered JSON and database mutations.
/// </summary>
public static class ApiAtomicRequestRateBudget
{
    public static async Task InvokeAsync(
        HttpContext context, RequestDelegate next,
        ApiAtomicRequestWindowLimiter limiter)
    {
        // Preserve OIDC 401/403, CORS preflight and probes.
        if (!context.Request.Path.StartsWithSegments("/api/v1") ||
            context.User.Identity?.IsAuthenticated != true ||
            HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        if (!limiter.TryAcquire(
                ApiRateLimitPolicy.PartitionKey(context),
                ApiRateLimitPolicy.TenantPartitionKey(context),
                out var retryAfterSeconds))
        {
            await ApiRateBudgetRejection.WriteAsync(context, retryAfterSeconds);
            return;
        }

        await next(context);
    }
}
