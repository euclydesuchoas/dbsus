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

    public void Track(FtpSocketStream stream)
    {
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
            Sockets.Track(stream);
            return base.ConnectAsync(stream, token);
        }

        protected override Task ConnectAsync(FtpSocketStream stream, string host, int port, FtpIpVersion ipVersions, CancellationToken token)
        {
            Sockets.Track(stream);
            return base.ConnectAsync(stream, host, port, ipVersions, token);
        }
    }
}

/// <summary>Runs each call of the synchronous client on the thread pool; cancellation goes through <see cref="Abort"/>.</summary>
internal sealed class SyncFtpSession(string host, int port, FtpConfig config) : FtpSession
{
    private readonly TrackingClient _client = new(host, port, config);

    public override int ConnectCount => _client.Status.ConnectCount;

    public override FtpReply LastReply => _client.LastReply;

    public override Task ConnectAsync(CancellationToken cancellationToken) => Task.Run(() => _client.Connect(), cancellationToken);

    public override Task<FtpReply> ExecuteAsync(string command, CancellationToken cancellationToken) =>
        Task.Run(() => _client.Execute(command), cancellationToken);

    public override Task<FtpListItem[]> GetListingAsync(string path, FtpListOption options, CancellationToken cancellationToken) =>
        Task.Run(() => _client.GetListing(path, options), cancellationToken);

    public override Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken) =>
        Task.Run(() => _client.OpenRead(path, FtpDataType.Binary, offset, checkIfFileExists: false), cancellationToken);

    public override void Abort() => _client.Sockets.CloseAll();

    public override void Dispose() => _client.Dispose();

    private sealed class TrackingClient(string host, int port, FtpConfig config) : FtpClient(host, "anonymous", "anonymous@", port, config)
    {
        public SocketTracker Sockets { get; } = new();

        protected override void Connect(FtpSocketStream stream)
        {
            Sockets.Track(stream);
            base.Connect(stream);
        }

        protected override void Connect(FtpSocketStream stream, string host, int port, FtpIpVersion ipVersions)
        {
            Sockets.Track(stream);
            base.Connect(stream, host, port, ipVersions);
        }
    }
}
