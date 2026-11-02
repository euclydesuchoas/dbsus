using System.Diagnostics.CodeAnalysis;

namespace DbSus.Client.Transport;

/// <summary>
/// Reports one stage of one file to the <see cref="IProgress{T}"/> of a call, from one thread at a
/// time: every start, and every file that reaches its size; in between, at most once every
/// <see cref="Interval"/>, which holds however fast the link is, and never the same bytes twice in a
/// row. Whatever the callback throws is ignored, as with OnRetry.
/// </summary>
internal sealed class ProgressMeter(IProgress<DatasusProgressInfo> progress, Func<DateTime> utcNow, string operation, RemotePath remote, DatasusProgressStage stage, long totalBytes)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private long _resumedAt;
    private long _done = -1;
    private DateTime _last;

    public void Start(long bytesDone, long resumedAt = 0)
    {
        _resumedAt = resumedAt;
        Report(bytesDone, utcNow());
    }

    public void Advance(long bytesDone)
    {
        if (bytesDone == _done)
        {
            return;
        }

        // A clock set back does not stop the reports.
        var now = utcNow();
        if (bytesDone >= totalBytes || now - _last >= Interval || now < _last)
        {
            Report(bytesDone, now);
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "Exceptions from the caller's callback are deliberately ignored, as those of OnRetry.")]
    private void Report(long bytesDone, DateTime now)
    {
        _done = bytesDone;
        _last = now;
        try
        {
            progress.Report(new DatasusProgressInfo(operation, remote.Relative, stage, bytesDone, totalBytes, _resumedAt));
        }
        catch (Exception)
        {
            // Ignored by contract.
        }
    }
}
