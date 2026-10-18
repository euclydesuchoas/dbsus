namespace DbSus.Client.Caching;

/// <summary>
/// A download without a cache, opened for reading: the system deletes the file when it is closed
/// (<see cref="FileOptions.DeleteOnClose"/>; on Linux and macOS .NET deletes it when the stream is
/// disposed, so a process that is killed leaves it behind), and then its folder goes too.
/// </summary>
internal sealed class TemporaryDownloadStream(string path)
    : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan | FileOptions.DeleteOnClose)
{
    private bool _removed;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && !_removed)
        {
            _removed = true;
            TempStore.RemoveDownloadFolder(Path.GetDirectoryName(Name)!);
        }
    }
}
