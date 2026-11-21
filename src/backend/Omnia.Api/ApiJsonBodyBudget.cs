using Microsoft.Net.Http.Headers;

namespace Omnia.Api;

/// <summary>
/// Bounded, privacy-preserving JSON HTTP request-body budget for existing
/// authenticated /api/v1 mutation endpoints. TestServer and reverse proxies
/// may not apply Kestrel limits. The total read deadline is separate from
/// Kestrel's slow-header/minimum-data-rate protections.
/// </summary>
public static class ApiJsonBodyBudget
{
    public const int DefaultMaxJsonBytes = 64 * 1024;
    public const int MinimumMaxJsonBytes = 1024;
    public const int MaximumMaxJsonBytes = 64 * 1024;
    public const int DefaultReadTimeoutMilliseconds = 10_000;
    public const int MinimumReadTimeoutMilliseconds = 100;
    public const int MaximumReadTimeoutMilliseconds = 30_000;

    public static int ValidateLimit(int maxJsonBytes)
    {
        if (maxJsonBytes is < MinimumMaxJsonBytes or > MaximumMaxJsonBytes)
            throw new InvalidOperationException(
                "RequestBody:MaxJsonBytes must be between 1024 and 65536.");
        return maxJsonBytes;
    }

    public static int ValidateReadTimeoutMilliseconds(int milliseconds)
    {
        if (milliseconds is < MinimumReadTimeoutMilliseconds
            or > MaximumReadTimeoutMilliseconds)
            throw new InvalidOperationException(
                "RequestBody:ReadTimeoutMilliseconds must be between 100 and 30000.");
        return milliseconds;
    }

    public static async Task InvokeAsync(
        HttpContext context, RequestDelegate next,
        int maxJsonBytes,
        int readTimeoutMilliseconds = DefaultReadTimeoutMilliseconds,
        ApiJsonBodyReadConcurrencyLimiter? concurrencyLimiter = null)
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

        // JSON endpoints must reject unsupported/malformed media types before
        // allocating a buffer or reading a single request-body byte. Minimal
        // API binding would reject them later, after this middleware buffered
        // the body; charset and duplicate parameters must also fail closed.
        if (!HasSupportedJsonMediaType(context.Request))
        {
            await RejectAsync(context,
                StatusCodes.Status415UnsupportedMediaType,
                "unsupported_media_type");
            return;
        }

        // Do not queue slow senders once this instance's JSON intake is full.
        // The lease is acquired after cheap 413/415 checks but before any
        // buffer allocation or ReadAsync; it is shared across API requests.
        // The using statement releases on timeout, abort, 413 or exceptions.
        // Use the same collision-safe partition as the authenticated rate
        // limiter; never derive an actor from user-supplied headers.
        using var readPermit = concurrencyLimiter?.TryAcquire(
            ApiRateLimitPolicy.PartitionKey(context),
            ApiRateLimitPolicy.TenantPartitionKey(context));
        if (concurrencyLimiter is not null && readPermit is null)
        {
            context.Response.Headers["Retry-After"] = "1";
            await RejectAsync(context,
                StatusCodes.Status503ServiceUnavailable,
                "request_body_busy");
            return;
        }

        // Do not trust absent/forged Content-Length. Never buffer more than
        // max+1 bytes, even for streaming/chunked requests. A SINGLE total
        // deadline covers all reads so a trickle cannot reset the timer.
        using var bounded = new MemoryStream(capacity: Math.Min(maxJsonBytes, 8192));
        var chunk = new byte[Math.Min(8192, maxJsonBytes + 1)];
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted))
        {
            deadline.CancelAfter(TimeSpan.FromMilliseconds(
                ValidateReadTimeoutMilliseconds(readTimeoutMilliseconds)));
            try
            {
                while (true)
                {
                    var remaining = maxJsonBytes - (int)bounded.Length;
                    var count = await context.Request.Body.ReadAsync(
                        chunk.AsMemory(0, Math.Min(chunk.Length, remaining + 1)),
                        deadline.Token);
                    if (count == 0) break;
                    if (count > remaining)
                    {
                        await RejectAsync(context,
                            StatusCodes.Status413PayloadTooLarge,
                            "request_body_too_large");
                        return;
                    }
                    await bounded.WriteAsync(chunk.AsMemory(0, count),
                        deadline.Token);
                }
            }
            catch (OperationCanceledException) when (
                deadline.IsCancellationRequested &&
                !context.RequestAborted.IsCancellationRequested)
            {
                // The app-level body deadline expired: 408, with no payload,
                // no replay, no model binding and no DB/AI mutation.
                // A true client disconnect is NOT an application timeout.
                await RejectAsync(context,
                    StatusCodes.Status408RequestTimeout,
                    "request_body_timeout");
                return;
            }
        }

        // The body-read permit is NOT held during downstream DB/AI work.
        // Dispose is idempotent; the using declaration remains a safety net
        // for any failure during buffering or exception from a stream.
        readPermit?.Dispose();

        // Dispose the body-only deadline BEFORE normal downstream work:
        // successful DB/AI handling must not be cancelled by a body timer.
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

    private static bool HasSupportedJsonMediaType(HttpRequest request)
    {
        // Use the same JSON media-family recognition as Minimal API binding:
        // application/json and application/*+json, never text/plain or XML.
        if (!request.HasJsonContentType() ||
            !MediaTypeHeaderValue.TryParse(request.ContentType, out var parsed))
            return false;

        var sawCharset = false;
        foreach (var parameter in parsed.Parameters)
        {
            if (sawCharset ||
                !string.Equals(parameter.Name.ToString(), "charset",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    HeaderUtilities.RemoveQuotes(parameter.Value).ToString(),
                    "utf-8", StringComparison.OrdinalIgnoreCase))
                return false;
            sawCharset = true;
        }
        return true;
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
