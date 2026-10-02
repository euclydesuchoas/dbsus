using System.Runtime.InteropServices;
using FluentFTP;

namespace DbSus.Client.Transport;

/// <summary>
/// The FluentFTP calls a connection makes, in one shape for both FluentFTP clients. On .NET
/// Framework the synchronous client is used: there, asynchronous socket reads observe neither
/// FluentFTP's read timeout nor the cancellation token, so an asynchronous read from a server that
/// stops answering would never end, while synchronous reads enforce the timeout. Elsewhere the
/// asynchronous client is used. Either way, <see cref="Abort"/> closes the sockets at once, from
/// any thread, which ends any pending read.
/// </summary>
internal abstract class FtpSession : IDisposable
{
    /// <summary>Gets a value indicating whether this runtime needs the synchronous client.</summary>
    public static bool UsesSynchronousClient { get; } =
        RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.Ordinal);

    /// <summary>Gets how many times FluentFTP has opened the control connection (it reopens it by itself).</summary>
    public abstract int ConnectCount { get; }

    /// <summary>Gets the last reply received on the control connection.</summary>
    public abstract FtpReply LastReply { get; }

    public static FtpSession Create(string host, int port, FtpConfig config) =>
        UsesSynchronousClient ? new SyncFtpSession(host, port, config) : new AsyncFtpSession(host, port, config);

    public abstract Task ConnectAsync(CancellationToken cancellationToken);

    public abstract Task<FtpReply> ExecuteAsync(string command, CancellationToken cancellationToken);

    public abstract Task<FtpListItem[]> GetListingAsync(string path, FtpListOption options, CancellationToken cancellationToken);

    public abstract Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken);

    /// <summary>Closes the control and data sockets now; the session cannot be used afterwards.</summary>
    public abstract void Abort();

    public abstract void Dispose();
}

/// <summary>The sockets a FluentFTP client opens, control and data, so they can be closed by force.</summary>
internal sealed class SocketTracker
{
    private readonly object _gate = new();
    private readonly List<FtpSocketStream> _streams = [];
    private bool _closed;
    private int _controlRegistrations;
    private int _dataRegistrations;

    /// <summary>Gets how many control connections the hooks have reported.</summary>
    public int ControlRegistrations => Volatile.Read(ref _controlRegistrations);

    /// <summary>Gets how many data connections the hooks have reported.</summary>
    public int DataRegistrations => Volatile.Read(ref _dataRegistrations);

    public void Track(FtpSocketStream stream, bool isData)
    {
        Interlocked.Increment(ref isData ? ref _dataRegistrations : ref _controlRegistrations);
        lock (_gate)
        {
            if (!_closed)
            {
                _streams.RemoveAll(tracked => tracked.IsDisposed);
                if (!_streams.Contains(stream))
                {
                    _streams.Add(stream);
                }

                return;
            }
        }

        // Aborted already: a connection opened afterwards is closed as well.
        Close(stream);
    }

    public void CloseAll()
    {
        FtpSocketStream[] streams;
        lock (_gate)
        {
            _closed = true;
            streams = [.. _streams];
            _streams.Clear();
        }

        foreach (var stream in streams)
        {
            Close(stream);
        }
    }

    // FtpSocketStream.Close disposes the stream and the socket, without any network exchange.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "Closing by force is best effort; a socket that fails to close is already unusable.")]
    private static void Close(FtpSocketStream stream)
    {
        try
        {
            stream.Close();
        }
        catch (Exception)
        {
        }
    }
}

internal sealed class AsyncFtpSession(string host, int port, FtpConfig config) : FtpSession
{
    private readonly TrackingClient _client = new(host, port, config);

    public override int ConnectCount => _client.Status.ConnectCount;

    public override FtpReply LastReply => _client.LastReply;

    public override Task ConnectAsync(CancellationToken cancellationToken) => _client.Connect(cancellationToken);

    public override Task<FtpReply> ExecuteAsync(string command, CancellationToken cancellationToken) => _client.Execute(command, cancellationToken);

    public override Task<FtpListItem[]> GetListingAsync(string path, FtpListOption options, CancellationToken cancellationToken) =>
        _client.GetListing(path, options, cancellationToken);

    public override Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken) =>
        _client.OpenRead(path, FtpDataType.Binary, offset, checkIfFileExists: false, cancellationToken);

    public override void Abort() => _client.Sockets.CloseAll();

    public override void Dispose() => _client.Dispose();

    private sealed class TrackingClient(string host, int port, FtpConfig config) : AsyncFtpClient(host, "anonymous", "anonymous@", port, config)
    {
        public SocketTracker Sockets { get; } = new();

        protected override Task ConnectAsync(FtpSocketStream stream, CancellationToken token)
        {
            Sockets.Track(stream, isData: false);
            return base.ConnectAsync(stream, token);
        }

        protected override Task ConnectAsync(FtpSocketStream stream, string host, int port, FtpIpVersion ipVersions, CancellationToken token)
        {
            Sockets.Track(stream, isData: true);
            return base.ConnectAsync(stream, host, port, ipVersions, token);
        }
    }
}

/// <summary>Hooks a test can switch off, to check that the session notices.</summary>
[Flags]
internal enum SocketHooks
{
    None = 0,
    Control = 1,
    Data = 2,
}

/// <summary>
/// Runs each call of the synchronous client on the thread pool; cancellation goes through
/// <see cref="Abort"/>. Since that needs FluentFTP to report every connection through its hooks,
/// the session checks that it did: after connecting, and after opening each data connection
/// (before the caller can read a download; right after a listing, whose data FluentFTP reads in
/// the same call). A FluentFTP version that does not report them is refused with a
/// <see cref="NotSupportedException"/> rather than used without a way to end stalled operations.
/// </summary>
internal sealed class SyncFtpSession(string host, int port, FtpConfig config, SocketHooks disabledForTest = SocketHooks.None) : FtpSession
{
    /// <summary>The FluentFTP version the client is tested with.</summary>
    public const string TestedFluentFtpVersion = "55.0.0";

    private readonly TrackingClient _client = new(host, port, config, disabledForTest);

    public override int ConnectCount => _client.Status.ConnectCount;

    public override FtpReply LastReply => _client.LastReply;

    public override Task ConnectAsync(CancellationToken cancellationToken) => Task.Run(
        () =>
        {
            _client.Connect();
            if (_client.Sockets.ControlRegistrations == 0)
            {
                throw Unsupported("control");
            }
        },
        cancellationToken);

    public override Task<FtpReply> ExecuteAsync(string command, CancellationToken cancellationToken) =>
        Task.Run(() => _client.Execute(command), cancellationToken);

    public override Task<FtpListItem[]> GetListingAsync(string path, FtpListOption options, CancellationToken cancellationToken) => Task.Run(
        () =>
        {
            var before = _client.Sockets.DataRegistrations;
            var items = _client.GetListing(path, options);
            return _client.Sockets.DataRegistrations != before ? items : throw Unsupported("data");
        },
        cancellationToken);

    public override Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken) => Task.Run(
        () =>
        {
            var before = _client.Sockets.DataRegistrations;
            var stream = _client.OpenRead(path, FtpDataType.Binary, offset, checkIfFileExists: false);
            if (_client.Sockets.DataRegistrations != before)
            {
                return stream;
            }

            Abort();
            stream.Dispose();
            throw Unsupported("data");
        },
        cancellationToken);

    public override void Abort() => _client.Sockets.CloseAll();

    public override void Dispose() => _client.Dispose();

    private static NotSupportedException Unsupported(string connection) => new(
        $"FluentFTP {typeof(FtpClient).Assembly.GetName().Version?.ToString(3)} did not report its {connection} connection to DbSus.Client, "
        + $"which needs that on .NET Framework to end stalled or cancelled operations; this FluentFTP version is not supported "
        + $"(the client is tested with FluentFTP {TestedFluentFtpVersion}).");

    private sealed class TrackingClient(string host, int port, FtpConfig config, SocketHooks disabledForTest) : FtpClient(host, "anonymous", "anonymous@", port, config)
    {
        public SocketTracker Sockets { get; } = new();

        protected override void Connect(FtpSocketStream stream)
        {
            if ((disabledForTest & SocketHooks.Control) == 0)
            {
                Sockets.Track(stream, isData: false);
            }

            base.Connect(stream);
        }

        protected override void Connect(FtpSocketStream stream, string host, int port, FtpIpVersion ipVersions)
        {
            if ((disabledForTest & SocketHooks.Data) == 0)
            {
                Sockets.Track(stream, isData: true);
            }

            base.Connect(stream, host, port, ipVersions);
        }
    }
}
