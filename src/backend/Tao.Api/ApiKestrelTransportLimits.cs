using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Tao.Api;

/// <summary>
/// Bound the HTTP transport boundary before routing/OIDC and JSON middleware.
/// These are local Kestrel limits, not proxy/WAF or multi-replica protections.
/// </summary>
public static class ApiKestrelTransportLimits
{
    public const int DefaultRequestHeadersTimeoutMilliseconds = 10_000;
    public const int MinimumRequestHeadersTimeoutMilliseconds = 1_000;
    public const int MaximumRequestHeadersTimeoutMilliseconds = 30_000;

    public const int DefaultMaxRequestHeadersTotalBytes = 16 * 1024;
    public const int MinimumMaxRequestHeadersTotalBytes = 8 * 1024;
    public const int MaximumMaxRequestHeadersTotalBytes = 32 * 1024;

    public const int DefaultMaxRequestHeaderCount = 64;
    public const int MinimumMaxRequestHeaderCount = 16;
    public const int MaximumMaxRequestHeaderCount = 100;

    // Defence in depth with the 64KiB application JSON budget. We are NOT
    // enabling any binary upload endpoint in this slice.
    public const long KestrelMaxRequestBodyBytes = 70 * 1024;

    // Kestrel's safe default stays explicitly enabled: a minimum body
    // transfer rate is not equivalent to a total body receive deadline.
    private const double MinimumRequestBodyBytesPerSecond = 240;
    private static readonly TimeSpan MinimumRequestBodyGracePeriod =
        TimeSpan.FromSeconds(5);

    public static void Validate(
        int requestHeadersTimeoutMilliseconds,
        int maxRequestHeadersTotalBytes,
        int maxRequestHeaderCount)
    {
        if (requestHeadersTimeoutMilliseconds is <
            MinimumRequestHeadersTimeoutMilliseconds or >
            MaximumRequestHeadersTimeoutMilliseconds)
            throw new InvalidOperationException(
                "Transport:RequestHeadersTimeoutMilliseconds must be between 1000 and 30000.");

        if (maxRequestHeadersTotalBytes is <
            MinimumMaxRequestHeadersTotalBytes or >
            MaximumMaxRequestHeadersTotalBytes)
            throw new InvalidOperationException(
                "Transport:MaxRequestHeadersTotalBytes must be between 8192 and 32768.");

        if (maxRequestHeaderCount is <
            MinimumMaxRequestHeaderCount or >
            MaximumMaxRequestHeaderCount)
            throw new InvalidOperationException(
                "Transport:MaxRequestHeaderCount must be between 16 and 100.");
    }

    public static void Configure(
        KestrelServerLimits limits,
        int requestHeadersTimeoutMilliseconds,
        int maxRequestHeadersTotalBytes,
        int maxRequestHeaderCount)
    {
        ArgumentNullException.ThrowIfNull(limits);
        Validate(requestHeadersTimeoutMilliseconds,
            maxRequestHeadersTotalBytes, maxRequestHeaderCount);

        // These values are enforced by the real Kestrel HTTP parser BEFORE
        // any ASP.NET Core middleware (authentication, RLS, body-budget).
        limits.RequestHeadersTimeout =
            TimeSpan.FromMilliseconds(requestHeadersTimeoutMilliseconds);
        limits.MaxRequestHeadersTotalSize = maxRequestHeadersTotalBytes;
        limits.MaxRequestHeaderCount = maxRequestHeaderCount;
        limits.MaxRequestBodySize = KestrelMaxRequestBodyBytes;
        limits.MinRequestBodyDataRate = new MinDataRate(
            MinimumRequestBodyBytesPerSecond, MinimumRequestBodyGracePeriod);
    }
}
