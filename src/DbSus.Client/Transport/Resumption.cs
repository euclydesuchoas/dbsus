namespace DbSus.Client.Transport;

/// <summary>The limits of a transfer that resumes from where an earlier one stopped.</summary>
internal static class Resumption
{
    /// <summary>
    /// The longest that opening a transfer from an offset beyond 0 may take, from the passive-mode
    /// request to the reply to RETR, REST included. Opening one takes a few round trips; a server
    /// that leaves one of those commands unanswered would otherwise hold the attempt until the read
    /// timeout, or until the TCP stack gives up on a command that is never acknowledged.
    /// </summary>
    public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Once a resumption has failed to open, a transfer that received up to this many bytes starts
    /// over from byte 0, without REST, instead of trying to resume again. A resumption whose opening
    /// fails costs about the opening limit, a login and a backoff wait (some 6 s), while reading
    /// 16 MiB again at 4 MB/s takes about 4 s: below this size, starting over costs less than
    /// another resumption that may fail too. Without such a failure, a transfer always resumes.
    /// </summary>
    public const long RestartFromStartUpTo = 16L * 1024 * 1024;
}
