namespace Omnia.Api;

/// <summary>
/// Process-local admission control for concurrent JSON request-body reads.
/// No queue: a held reader permit is released after complete buffering,
/// never retained while the application handler runs.
/// Not a global connection limit or distributed tenant quota.
/// </summary>
public sealed class ApiJsonBodyReadConcurrencyLimiter
{
    public const int DefaultMaxConcurrentReads = 32;
    public const int MinimumMaxConcurrentReads = 1;
    public const int MaximumMaxConcurrentReads = 256;

    private readonly SemaphoreSlim _permits;

    public int MaxConcurrentReads { get; }

    public ApiJsonBodyReadConcurrencyLimiter(int maxConcurrentReads)
    {
        if (maxConcurrentReads is < MinimumMaxConcurrentReads or > MaximumMaxConcurrentReads)
            throw new InvalidOperationException(
                "RequestBody:MaxConcurrentReads must be between 1 and 256.");

        MaxConcurrentReads = maxConcurrentReads;
        _permits = new SemaphoreSlim(maxConcurrentReads, maxConcurrentReads);
    }

    /// <summary>
    /// Returns null on immediate capacity exhaustion; never waits or queues.
    /// The returned lease may be safely disposed more than once.
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
