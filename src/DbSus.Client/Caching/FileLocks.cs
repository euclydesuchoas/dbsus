using System.Diagnostics;

namespace DbSus.Client.Caching;

/// <summary>
/// File-based locks that work between threads and between processes. An exclusive lock is an open
/// <see cref="FileStream"/> with <see cref="FileShare.None"/>: on Windows the share mode refuses any
/// other open, and on Linux and macOS .NET backs it with an exclusive <c>flock</c> (advisory, so it
/// only binds programs that also use it, such as other DbSus processes). Lock files are never
/// deleted while in use, which avoids the classic race of locking an unlinked file.
/// </summary>
internal static class FileLocks
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public static async Task<FileStream> AcquireAsync(string lockPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var handle = TryAcquire(lockPath);
            if (handle is not null)
            {
                return handle;
            }

            if (elapsed.Elapsed >= timeout)
            {
                throw new TimeoutException(
                    $"Timed out after {timeout} waiting for '{lockPath}', held by another thread or process that is using the same cache entry.");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public static FileStream? TryAcquire(string lockPath)
    {
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null; // e.g. Windows "delete pending" state
        }
    }

    /// <summary>
    /// Deletes a file only if nobody has it open: readers open cached files with
    /// <see cref="FileShare.Read"/>, so an exclusive open fails while any reader is active.
    /// </summary>
    public static bool TryDeleteIfUnused(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return !File.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
