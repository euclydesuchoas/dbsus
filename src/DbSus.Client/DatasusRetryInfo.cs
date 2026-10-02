namespace DbSus.Client;

/// <summary>
/// Describes a retry, passed to <see cref="DatasusClientOptions.OnRetry"/> just before the client
/// waits and tries again.
/// </summary>
public sealed class DatasusRetryInfo
{
    internal DatasusRetryInfo(string operation, int attempt, TimeSpan delay, Exception exception)
    {
        Operation = operation;
        Attempt = attempt;
        Delay = delay;
        Exception = exception;
    }

    /// <summary>Gets a description of the operation, e.g. <c>Downloading /SIASUS/200801_/Dados/PAMG2407a.dbc</c>.</summary>
    public string Operation { get; }

    /// <summary>Gets the number of the attempt that failed, starting at 1.</summary>
    public int Attempt { get; }

    /// <summary>Gets how long the client waits before the next attempt.</summary>
    public TimeSpan Delay { get; }

    /// <summary>Gets the transient failure of that attempt.</summary>
    public Exception Exception { get; }
}
