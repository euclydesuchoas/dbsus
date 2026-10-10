namespace DbSus.Client;

/// <summary>
/// Options for <see cref="DatasusClient"/>. The defaults are deliberately gentle with the DATASUS
/// server: few connections, exponential backoff, and listings reused for a while.
/// </summary>
public sealed class DatasusClientOptions
{
    /// <summary>Creates options with the default values given on each property.</summary>
    public DatasusClientOptions()
    {
    }

    /// <summary>Gets or sets the FTP host. The default is <c>ftp.datasus.gov.br</c>.</summary>
    public string Host { get; set; } = "ftp.datasus.gov.br";

    /// <summary>Gets or sets the FTP port. The default is 21.</summary>
    public int Port { get; set; } = 21;

    /// <summary>
    /// Gets or sets the remote root that every path is relative to. The default is
    /// <c>/dissemin/publicos</c>, so <c>/SIASUS/200801_/Dados/PAMG2510a.dbc</c> names
    /// <c>/dissemin/publicos/SIASUS/200801_/Dados/PAMG2510a.dbc</c>.
    /// </summary>
    public string RootPath { get; set; } = "/dissemin/publicos";

    /// <summary>
    /// Gets or sets the directory of the persistent download cache, or <see langword="null"/> (the
    /// default) for no cache: downloads then go to temporary files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without a cache, each download goes to its own folder under the system temporary directory:
    /// <c>Path.GetTempPath()/DbSus/downloads/&lt;random id&gt;/</c> (on Windows usually
    /// <c>%TEMP%\DbSus\downloads</c>; on Linux and macOS <c>$TMPDIR</c> or <c>/tmp</c>). That
    /// location cannot be changed; to keep downloaded microdata in a folder you control, set a
    /// cache directory. Files opened by <c>OpenReaderAsync</c> are deleted when
    /// the reader is disposed; a file returned by <see cref="DatasusClient.DownloadAsync"/> is the
    /// caller's to delete. Folders left behind for more than 24 hours, whose files are not open, are
    /// removed by the first download without a cache of a later client and by
    /// <see cref="DatasusClient.PruneCacheAsync"/>.
    /// </para>
    /// <para>
    /// Cache layout: <c>&lt;cache&gt;/&lt;remote path&gt;/&lt;file&gt;/&lt;size&gt;-&lt;MDTM&gt;/</c> with the file,
    /// <c>entry.meta</c> (plain text) and <c>entry.lock</c> (an empty file used as a lock between
    /// threads and processes). Lock files and empty folders stay after pruning and are removed by
    /// <see cref="DatasusClient.ClearCacheAsync"/>.
    /// </para>
    /// </remarks>
    public string? CacheDirectory { get; set; }

    /// <summary>
    /// Gets or sets the maximum total size of cached files, or <see langword="null"/> (the default)
    /// for no limit. When exceeded, the least recently used entries that are not in use are removed.
    /// </summary>
    public long? MaxCacheSizeBytes { get; set; }

    /// <summary>Gets or sets the maximum number of simultaneous FTP connections, from 1 to 4. The default is 2.</summary>
    public int MaxConnections { get; set; } = 2;

    /// <summary>Gets or sets the time allowed to connect. The default is 30 seconds.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the longest silence tolerated on a connection. The default is 60 seconds.</summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets how many times a transient failure (dropped connection, timeout, FTP 4xx reply)
    /// is retried. The default is 4.
    /// </summary>
    public int MaxRetries { get; set; } = 4;

    /// <summary>
    /// Gets or sets the nominal delay before the first retry; each further retry doubles it, up to
    /// 30 seconds. The default is 1 second.
    /// </summary>
    /// <remarks>
    /// Each wait is a random fraction, between half and all, of its nominal delay, so the jitter
    /// only shortens waits. For example, with 200 ms and two retries the nominal waits are 200 and
    /// 400 ms, and the actual total is between 300 and 600 ms.
    /// </remarks>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets a callback invoked before each retry, with the operation, the number of the
    /// attempt that failed, the wait before the next one and the failure. The default is
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Only transient failures are retried; a missing file (FTP 550) is a
    /// <see cref="FileNotFoundException"/> and is never retried. The callback runs on the thread of
    /// the operation, before it waits; if it throws, its exception is ignored and the operation goes
    /// on. When the retries are exhausted, the operation throws
    /// <see cref="DatasusRetriesExhaustedException"/>.
    /// </remarks>
    public Action<DatasusRetryInfo>? OnRetry { get; set; }

    /// <summary>Gets or sets how long a directory listing is reused. The default is 10 minutes.</summary>
    public TimeSpan ListingCacheDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets or sets a value indicating whether each downloaded DBC is fully decompressed and its CRC-32
    /// checked before it is used or cached. The default is <see langword="true"/>.
    /// </summary>
    public bool VerifyOnDownload { get; set; } = true;

    /// <summary>
    /// Gets or sets how long to wait for another thread or process that is downloading the same
    /// file into the cache. The default is 10 minutes.
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(10);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException("Host is required.", nameof(Host));
        }

        if (string.IsNullOrEmpty(RootPath) || RootPath[0] != '/')
        {
            throw new ArgumentException("RootPath must start with '/'.", nameof(RootPath));
        }

        if (MaxConnections is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConnections), MaxConnections, "Use 1 to 4 connections.");
        }

        if (MaxRetries < 0 || InitialRetryDelay < TimeSpan.Zero || ConnectTimeout <= TimeSpan.Zero
            || ReadTimeout <= TimeSpan.Zero || LockTimeout < TimeSpan.Zero || ListingCacheDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetries), "Retries, delays and timeouts cannot be negative.");
        }

        if (MaxCacheSizeBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCacheSizeBytes), MaxCacheSizeBytes, "The cache limit must be positive.");
        }
    }
}
