namespace Omnia.Api;

/// <summary>
/// Process-local admission cap for middleware-owned buffered JSON requests.
/// Unlike ApiJsonBodyReadConcurrencyLimiter, its permit remains held through
/// downstream Minimal API processing until the bounded buffer is disposed.
/// No queue, tenant fairness, distributed budget or DB concurrency guarantee.
/// </summary>
public sealed class ApiJsonBufferedRequestLimiter
{
    public const int DefaultMaxConcurrentBufferedRequests = 64;
    public const int MinimumMaxConcurrentBufferedRequests = 1;
    public const int MaximumMaxConcurrentBufferedRequests = 256;

    private readonly SemaphoreSlim _permits;

    public int MaxConcurrentBufferedRequests { get; }

    public ApiJsonBufferedRequestLimiter(int maxConcurrentBufferedRequests)
    {
        if (maxConcurrentBufferedRequests is <
            MinimumMaxConcurrentBufferedRequests or >
            MaximumMaxConcurrentBufferedRequests)
            throw new InvalidOperationException(
                "RequestBody:MaxConcurrentBufferedRequests must be between 1 and 256.");

        MaxConcurrentBufferedRequests = maxConcurrentBufferedRequests;
        _permits = new SemaphoreSlim(
            maxConcurrentBufferedRequests, maxConcurrentBufferedRequests);
    }

    /// <summary>
    /// Acquire without blocking or queueing. Dispose the returned lease
    /// AFTER the handler has finished, or on any early failure.
    /// </summary>
    public IDisposable? TryAcquire() =>
        _permits.Wait(0) ? new Permit(_permits) : null;

    private sealed class Permit(SemaphoreSlim permits) : IDisposable
    {
        private SemaphoreSlim? _permits = permits;

        public void Dispose()
        {
            Interlocked.Exchange(ref _permits, null)?.Release();
        }
    }
}
