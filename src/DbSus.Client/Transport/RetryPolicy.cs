namespace DbSus.Client.Transport;

/// <summary>
/// Retries transient failures with exponential backoff, capped at 30 seconds; each wait is between
/// half and all of its nominal delay (jitter). Missing files and permanent errors are never
/// retried; cancellation stops at once.
/// </summary>
internal sealed class RetryPolicy(int maxRetries, TimeSpan initialDelay, Func<TimeSpan, CancellationToken, Task> delay)
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);
    private readonly Random _random = new();

    public int MaxRetries => maxRetries;

    public async Task<T> RunAsync<T>(string operation, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (TransientTransportException exception)
            {
                if (attempt >= maxRetries)
                {
                    var attempts = attempt + 1;
                    throw new IOException(
                        $"{operation} failed after {attempts} {(attempts == 1 ? "attempt" : "attempts")}: {exception.Message}", exception);
                }

                await delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public TimeSpan Backoff(int attempt)
    {
        double jitter;
        lock (_random)
        {
            jitter = 0.5 + (_random.NextDouble() / 2);
        }

        var ticks = initialDelay.Ticks * Math.Pow(2, Math.Min(attempt, 20)) * jitter;
        return TimeSpan.FromTicks((long)Math.Min(MaxDelay.Ticks, ticks));
    }
}
