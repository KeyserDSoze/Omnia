namespace Omnia.Api;

/// <summary>
/// Bounded, privacy-preserving JSON HTTP request-body budget for existing
/// authenticated /api/v1 mutation endpoints. TestServer and reverse proxies
/// may not apply Kestrel limits, and Content-Length is not authoritative for
/// chunked uploads. This guard reads at most MaxJsonBytes+1 before binding.
/// No body data is logged or retained after the request.
/// </summary>
public static class ApiJsonBodyBudget
{
    public const int DefaultMaxJsonBytes = 64 * 1024;
    public const int MinimumMaxJsonBytes = 1024;
    public const int MaximumMaxJsonBytes = 64 * 1024;

    public static int ValidateLimit(int maxJsonBytes)
    {
        if (maxJsonBytes is < MinimumMaxJsonBytes or > MaximumMaxJsonBytes)
            throw new InvalidOperationException(
                "RequestBody:MaxJsonBytes must be between 1024 and 65536.");
        return maxJsonBytes;
    }

    public static async Task InvokeAsync(
        HttpContext context, RequestDelegate next,
        int maxJsonBytes)
    {
        // Health, readiness, GET/DELETE and future upload endpoints are
        // not assumed to carry JSON. A separate media budget is required
        // before supporting binary ingestion.
        if (!context.Request.Path.StartsWithSegments("/api/v1") ||
            !(HttpMethods.IsPost(context.Request.Method) ||
              HttpMethods.IsPut(context.Request.Method) ||
              HttpMethods.IsPatch(context.Request.Method)))
        {
            await next(context);
            return;
        }

        // We do not register request decompression. Never let a proxy or
        // additional middleware silently expand a compressed content bomb.
        var encoding = context.Request.Headers["Content-Encoding"].ToString();
        if (!string.IsNullOrEmpty(encoding) &&
            !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase))
        {
            await RejectAsync(context,
                StatusCodes.Status415UnsupportedMediaType,
                "unsupported_content_encoding");
            return;
        }

        if (context.Request.ContentLength > maxJsonBytes)
        {
            await RejectAsync(context,
                StatusCodes.Status413PayloadTooLarge,
                "request_body_too_large");
            return;
        }

        // Do not trust absent or forged Content-Length: stream a maximum
        // of budget+1 bytes, then immediately 413, without reading or
        // allocating an unbounded body. The successful body is replayed
        // byte-for-byte to the existing Minimal API JSON binder.
        using var bounded = new MemoryStream(capacity: Math.Min(maxJsonBytes, 8192));
        var chunk = new byte[Math.Min(8192, maxJsonBytes + 1)];
        while (true)
        {
            var remaining = maxJsonBytes - (int)bounded.Length;
            var count = await context.Request.Body.ReadAsync(
                chunk.AsMemory(0, Math.Min(chunk.Length, remaining + 1)),
                context.RequestAborted);
            if (count == 0) break;
            if (count > remaining)
            {
                await RejectAsync(context,
                    StatusCodes.Status413PayloadTooLarge,
                    "request_body_too_large");
                return;
            }
            await bounded.WriteAsync(chunk.AsMemory(0, count),
                context.RequestAborted);
        }

        bounded.Position = 0;
        var original = context.Request.Body;
        context.Request.Body = bounded;
        try
        {
            await next(context);
        }
        finally
        {
            context.Request.Body = original;
        }
    }

    private static async Task RejectAsync(
        HttpContext context, int statusCode, string error)
    {
        context.Response.StatusCode = statusCode;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { error },
            cancellationToken: context.RequestAborted);
    }
}
