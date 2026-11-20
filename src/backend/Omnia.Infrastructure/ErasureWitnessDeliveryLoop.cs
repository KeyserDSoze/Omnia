namespace Omnia.Infrastructure;

/// <summary>
/// Bounded periodic re-delivery from the committed primary outbox to an
/// independent witness. Failed/uncertain deliveries are NEVER acknowledged
/// locally: the next attempt rescans the source, and witness UUID deduplication
/// makes retries safe. This loop does not authorize recovery or prove that
/// an event survived source loss before publication.
/// </summary>
public sealed class ErasureWitnessDeliveryLoop
{
    private readonly Func<CancellationToken, Task<int>> _publish;
    private readonly TimeSpan _interval;
    private readonly Action<bool>? _cycleObserved;

    public ErasureWitnessDeliveryLoop(
        Func<CancellationToken, Task<int>> publish,
        TimeSpan interval,
        Action<bool>? cycleObserved = null)
    {
        ArgumentNullException.ThrowIfNull(publish);
        if (interval < TimeSpan.FromMilliseconds(10) ||
            interval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(interval));
        _publish = publish;
        _interval = interval;
        _cycleObserved = cycleObserved;
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var successful = false;
            try
            {
                // The publisher verifies both restricted SQL identities on
                // every run, including after transient reconnection.
                await _publish(stoppingToken);
                successful = true;
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // No exception details: provider errors may contain connection
                // strings or personal metadata. Treat this as an unconfirmed
                // delivery, not an ACK, and retry the full bounded scan.
                consecutiveFailures = Math.Min(consecutiveFailures + 1, 7);
            }

            _cycleObserved?.Invoke(successful);

            // Back off on repeated failures, but never beyond one minute.
            // A successful cycle resets the delay to the normal poll interval.
            var multiplier = successful ? 1L : 1L << (consecutiveFailures - 1);
            var millis = Math.Min(
                TimeSpan.FromMinutes(1).TotalMilliseconds,
                _interval.TotalMilliseconds * multiplier);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(millis), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
