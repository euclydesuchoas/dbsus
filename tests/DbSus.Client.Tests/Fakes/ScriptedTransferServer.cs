using DbSus.Client.Transport;

namespace DbSus.Client.Tests.Fakes;

/// <summary>
/// An in-memory server for the transfers of one file, whose reads are asynchronous and can be
/// slow, stall, drop or end early, as the real transport's: each transfer goes through the
/// client's <see cref="ReadLimitStream"/>, and its connections can be closed by force. A stalled
/// read ends only on its token or, when it ignores the token (as an asynchronous socket read on
/// .NET Framework), only when its connection is aborted. Synchronous reads are refused, so a test
/// shows that the client never reads the network synchronously.
/// </summary>
internal sealed class ScriptedTransferServer(byte[] content)
{
    public const string Root = "/dissemin/publicos";

    private int _opened;
    private int _active;
    private int _aborted;
    private long _bytesServed;

    public byte[] Content { get; } = content;

    /// <summary>Gets or sets the most bytes a read returns.</summary>
    public int MaxChunk { get; set; } = 8192;

    /// <summary>Gets or sets a number of bytes after which the transfer stops sending and keeps silent.</summary>
    public long? StallAfter { get; set; }

    /// <summary>Gets or sets whether a stalled read ignores its token and ends only when the connection is aborted.</summary>
    public bool StallIgnoresToken { get; set; }

    /// <summary>Gets or sets a number of bytes after which the transfer fails, as a reset connection.</summary>
    public long? DropAfter { get; set; }

    /// <summary>Gets or sets a number of bytes after which the transfer ends, before the end of the file.</summary>
    public long? EndAfter { get; set; }

    /// <summary>Gets or sets a number of bytes sent after the end of the file.</summary>
    public int ExtraBytes { get; set; }

    /// <summary>Gets or sets a step awaited before each read, given the number of that read; a slow server.</summary>
    public Func<int, CancellationToken, Task>? BeforeRead { get; set; }

    /// <summary>Gets or sets the read timeout of the client's <see cref="ReadLimitStream"/>.</summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public int Opened => Volatile.Read(ref _opened);

    public int Active => Volatile.Read(ref _active);

    public int Aborted => Volatile.Read(ref _aborted);

    public long BytesServed => Interlocked.Read(ref _bytesServed);

    public IDatasusConnectionFactory Factory() => new ConnectionFactory(this);

    private sealed class ConnectionFactory(ScriptedTransferServer server) : IDatasusConnectionFactory
    {
        public Task<IDatasusConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref server._opened);
            Interlocked.Increment(ref server._active);
            return Task.FromResult<IDatasusConnection>(new Connection(server));
        }
    }

    private sealed class Connection(ScriptedTransferServer server) : IDatasusConnection, IAbortableConnection
    {
        private readonly CancellationTokenSource _abort = new();
        private int _disposed;

        public bool IsBroken => _abort.IsCancellationRequested;

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string directory, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RemoteFileInfo> GetInfoAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new ReadLimitStream(new Transfer(server, offset, _abort.Token), server.ReadTimeout, Abort));
        }

        public void Abort()
        {
            if (!_abort.IsCancellationRequested)
            {
                Interlocked.Increment(ref server._aborted);
                _abort.Cancel();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref server._active);
            }
        }
    }

    private sealed class Transfer(ScriptedTransferServer server, long offset, CancellationToken aborted) : Stream
    {
        private long _position = offset;
        private int _extraLeft = server.ExtraBytes;
        private int _reads;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (server.BeforeRead is { } before)
            {
                await before(++_reads, cancellationToken).ConfigureAwait(false);
            }

            ThrowIfAborted();
            if (server.DropAfter is { } drop && _position >= drop)
            {
                throw new IOException("Connection reset by the scripted server.");
            }

            if (server.StallAfter is { } stall && _position >= stall)
            {
                await StallAsync(cancellationToken).ConfigureAwait(false);
            }

            var end = server.Content.Length;
            foreach (var limit in new[] { server.DropAfter, server.StallAfter, server.EndAfter })
            {
                if (limit is { } bytes)
                {
                    end = (int)Math.Min(end, bytes);
                }
            }

            var available = (int)Math.Min(Math.Min(count, server.MaxChunk), end - _position);
            if (available <= 0 && _extraLeft > 0 && server.EndAfter is null)
            {
                var junk = Math.Min(Math.Min(count, server.MaxChunk), _extraLeft);
                for (var i = 0; i < junk; i++)
                {
                    buffer[offset + i] = 0xEE;
                }

                _extraLeft -= junk;
                Interlocked.Add(ref server._bytesServed, junk);
                return junk;
            }

            if (available <= 0)
            {
                return 0;
            }

            Array.Copy(server.Content, _position, buffer, offset, available);
            _position += available;
            Interlocked.Add(ref server._bytesServed, available);
            return available;
        }

#if NET
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            System.Runtime.InteropServices.MemoryMarshal.TryGetArray<byte>(buffer, out var segment)
                ? new(ReadAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken))
                : throw new NotSupportedException("The scripted server reads into arrays only.");
#endif

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("The scripted server refuses synchronous reads.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private async Task StallAsync(CancellationToken cancellationToken)
        {
            using var wait = server.StallIgnoresToken
                ? CancellationTokenSource.CreateLinkedTokenSource(aborted)
                : CancellationTokenSource.CreateLinkedTokenSource(aborted, cancellationToken);
            try
            {
                await Task.Delay(Timeout.Infinite, wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ThrowIfAborted();
            }
        }

        private void ThrowIfAborted()
        {
            if (aborted.IsCancellationRequested)
            {
                throw new IOException("The connection was closed by force.");
            }
        }
    }
}
