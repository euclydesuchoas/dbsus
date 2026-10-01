namespace DbSus.Client;

/// <summary>
/// Options for <see cref="DatasusClient"/>. The defaults are deliberately gentle with the DATASUS
/// server: few connections, exponential backoff, and listings reused for a while.
/// </summary>
public sealed class DatasusClientOptions
{
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
    /// Gets or sets the delay before the first retry; each further retry doubles it, with random
    /// jitter, up to 30 seconds. The default is 1 second.
    /// </summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

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
