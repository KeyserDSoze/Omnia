namespace Omnia.Api;

/// <summary>
/// Uniform, privacy-preserving response for actor and tenant minute budgets.
/// Called before request body bytes or downstream endpoint work are touched.
/// </summary>
internal static class ApiRateBudgetRejection
{
    public static async Task WriteAsync(
        HttpContext context, int retryAfterSeconds)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        await context.Response.WriteAsJsonAsync(
            new { error = "rate_limited" }, cancellationToken: context.RequestAborted);
    }
}
