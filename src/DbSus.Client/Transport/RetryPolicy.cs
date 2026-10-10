using System.Diagnostics.CodeAnalysis;

namespace DbSus.Client.Transport;

/// <summary>
/// Retries transient failures with exponential backoff, capped at 30 seconds; each wait is between
/// half and all of its nominal delay (jitter). Missing files and permanent errors are never
/// retried; cancellation stops at once.
/// </summary>
internal sealed class RetryPolicy(
    int maxRetries,
    TimeSpan initialDelay,
    Func<TimeSpan, CancellationToken, Task> delay,
    Action<DatasusRetryInfo>? onRetry = null)
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);
    private readonly Random _random = new();

    public int MaxRetries => maxRetries;

    public async Task<T> RunAsync<T>(string operation, string remotePath, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
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
                var attempts = attempt + 1;
                if (attempt >= maxRetries)
                {
                    // An unexpected exception of the FTP library is reported itself, as it was thrown.
                    throw new DatasusRetriesExhaustedException(
                        $"{operation} failed after {attempts} {(attempts == 1 ? "attempt" : "attempts")}: {exception.Message}",
                        exception.ReplyCode,
                        exception.IsLibraryFailure ? exception.InnerException : exception)
                    {
                        RemotePath = remotePath,
                        Attempts = attempts,
                    };
                }

                var wait = Backoff(attempt);
                Notify(new DatasusRetryInfo(operation, remotePath, attempts, wait, exception, exception.ReplyCode));
                await delay(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (DatasusTransferException exception)
            {
                if (exception.RemotePath.Length == 0)
                {
                    exception.RemotePath = remotePath;
                }

                exception.Attempts = attempt + 1;
                throw;
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

    // A failing callback must not change how the operation ends; see DatasusClientOptions.OnRetry.
    [SuppressMessage("Design", "CA1031", Justification = "Exceptions from the caller's OnRetry callback are deliberately ignored.")]
    private void Notify(DatasusRetryInfo info)
    {
        if (onRetry is null)
        {
            return;
        }

        try
        {
            onRetry(info);
        }
        catch (Exception)
        {
            // Ignored by contract.
        }
    }
}
