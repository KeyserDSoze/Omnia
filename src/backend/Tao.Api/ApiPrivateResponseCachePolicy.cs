namespace Tao.Api;

/// <summary>
/// Server-side non-cacheability for personal /api/v1 HTTP responses.
/// Works before auth, rate limits, body guards and
/// streaming results. Browser request cache:no-store is not sufficient:
/// intermediate HTTP caches see only the origin server response headers.
/// </summary>
public static class ApiPrivateResponseCachePolicy
{
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Path segment matching deliberately excludes /api/v1-other and
        // public /health; it includes all future /api/v1 children.
        if (!context.Request.Path.StartsWithSegments("/api/v1"))
        {
            await next(context);
            return;
        }

        // Register once, before downstream can short-circuit. OnStarting
        // ensures endpoint-specific/cache middleware cannot accidentally
        // turn a personal 200/401/403/429/503 into a cacheable response.
        context.Response.OnStarting(static state =>
        {
            var response = (HttpResponse)state;
            response.Headers.CacheControl = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            return Task.CompletedTask;
        }, context.Response);

        await next(context);
    }
}
