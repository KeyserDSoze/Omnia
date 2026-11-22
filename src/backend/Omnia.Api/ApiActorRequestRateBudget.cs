namespace Omnia.Api;

/// <summary>
/// Authenticated /api/v1 per-actor request rate budget. Auth and authorization
/// must precede it; aggregate tenant and body budgets must follow it.
/// Applies the exact same fixed-window engine as tenant admission.
/// </summary>
public static class ApiActorRequestRateBudget
{
    public static async Task InvokeAsync(
        HttpContext context, RequestDelegate next,
        ApiActorRequestWindowLimiter limiter)
    {
        // Preserve anonymous challenges, health/readiness and CORS preflight.
        if (!context.Request.Path.StartsWithSegments("/api/v1") ||
            context.User.Identity?.IsAuthenticated != true ||
            HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        if (!limiter.TryAcquire(
            ApiRateLimitPolicy.PartitionKey(context), out var retryAfterSeconds))
        {
            await ApiRateBudgetRejection.WriteAsync(context, retryAfterSeconds);
            return;
        }

        await next(context);
    }
}
