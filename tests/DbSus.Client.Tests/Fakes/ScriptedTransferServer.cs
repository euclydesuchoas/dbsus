using System.Collections.Concurrent;
using DbSus.Client.Transport;

namespace DbSus.Client.Tests.Fakes;

/// <summary>How a scripted transfer fails, once.</summary>
internal enum ScriptedFaultKind
{
    /// <summary>Fails as a reset connection.</summary>
    Drop,

    /// <summary>Stops sending and keeps silent; the read ends on its token.</summary>
    Stall,

    /// <summary>Stops sending and keeps silent; the read ignores its token and ends only when the connection is aborted.</summary>
    StallIgnoringToken,

    /// <summary>Ends, before the end of the file.</summary>
    EndEarly,
}

/// <summary>A failure of one transfer, after a number of bytes counted from where that transfer started.</summary>
internal sealed class ScriptedFault(ScriptedFaultKind kind, long afterBytes)
{
    public ScriptedFaultKind Kind { get; } = kind;

    public long AfterBytes { get; } = afterBytes;
}

/// <summary>
/// An in-memory server for the transfers of one file, whose reads are asynchronous and can be
/// slow, stall, drop or end early, as the real transport's: each transfer goes through the
/// client's <see cref="ReadLimitStream"/>, and its connections can be closed by force. A stalled
/// read ends only on its token or, when it ignores the token (as an asynchronous socket read on
/// .NET Framework), only when its connection is aborted. Synchronous reads are refused, so a test
/// shows that the client never reads the network synchronously. The properties fail every
/// transfer at the same place in the file; <see cref="Faults"/> fail the next transfers once each;
/// <see cref="CloseIdleConnections"/> closes the connections made so far, when the test says.
/// </summary>
internal sealed class ScriptedTransferServer(byte[] content)
{
    public const string Root = "/dissemin/publicos";

    private int _opened;
    private int _active;
    private int _aborted;
    private int _transfers;
    private int _infoCalls;
    private long _bytesServed;
    private int _closedUpTo;

    public byte[] Content { get; } = content;

    /// <summary>Gets or sets the modification time that SIZE and MDTM report; changing it publishes a new version.</summary>
    public DateTime ModifiedUtc { get; set; } = new(2025, 8, 8, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Gets the size and time the server reports now.</summary>
    public RemoteFileInfo Version => new(Content.Length, ModifiedUtc);

    /// <summary>Gets or sets the most bytes a read returns.</summary>
    public int MaxChunk { get; set; } = 8192;

    /// <summary>Gets or sets an offset in the file at which every transfer stops sending and keeps silent.</summary>
    public long? StallAfter { get; set; }

    /// <summary>Gets or sets whether a stalled read ignores its token and ends only when the connection is aborted.</summary>
    public bool StallIgnoresToken { get; set; }

    /// <summary>Gets or sets an offset in the file at which every transfer fails, as a reset connection.</summary>
    public long? DropAfter { get; set; }

    /// <summary>Gets or sets an offset in the file at which every transfer ends, before the end of the file.</summary>
    public long? EndAfter { get; set; }

    /// <summary>Gets or sets a number of bytes sent after the end of the file.</summary>
    public int ExtraBytes { get; set; }

    /// <summary>Gets the failures of the next transfers, one each, in order.</summary>
    public ConcurrentQueue<ScriptedFault> Faults { get; } = new();

    /// <summary>Gets or sets a step awaited before each read, given the number of that read; a slow server.</summary>
    public Func<int, CancellationToken, Task>? BeforeRead { get; set; }

    /// <summary>Gets or sets a step awaited before each connection is made, given its number (from 1).</summary>
    public Func<int, CancellationToken, Task>? BeforeConnect { get; set; }

    /// <summary>Gets or sets a step run before each transfer opens, given its number (from 1); it may throw, as a refused RETR.</summary>
    public Action<int>? BeforeTransfer { get; set; }

    /// <summary>Gets or sets the read timeout of the client's <see cref="ReadLimitStream"/>.</summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the offset each transfer started at (its REST), in order.</summary>
    public ConcurrentQueue<long> TransferOffsets { get; } = new();

    public int Opened => Volatile.Read(ref _opened);

    public int Active => Volatile.Read(ref _active);

    public int Aborted => Volatile.Read(ref _aborted);

    public int Transfers => Volatile.Read(ref _transfers);

    public int InfoCalls => Volatile.Read(ref _infoCalls);

    public long BytesServed => Interlocked.Read(ref _bytesServed);

    public IDatasusConnectionFactory Factory() => new ConnectionFactory(this);

    /// <summary>
    /// Closes every connection made so far, as a server does once they have been idle for longer
    /// than it waits: the next read of their transfers fails as a reset, and their control
    /// connections answer nothing more (421). Connections made afterward are not affected.
    /// </summary>
    public void CloseIdleConnections() => Volatile.Write(ref _closedUpTo, Opened);

    private sealed class ConnectionFactory(ScriptedTransferServer server) : IDatasusConnectionFactory
    {
        public async Task<IDatasusConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = Interlocked.Increment(ref server._opened);
            if (server.BeforeConnect is { } before)
            {
                await before(number, cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Increment(ref server._active);
            return new Connection(server, number);
        }
    }

    private sealed class Connection(ScriptedTransferServer server, int number) : IDatasusConnection, IAbortableConnection
    {
        private readonly CancellationTokenSource _abort = new();
        private int _disposed;

        public bool IsBroken => _abort.IsCancellationRequested || ClosedByServer;

        private bool ClosedByServer => number <= Volatile.Read(ref server._closedUpTo);

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string directory, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RemoteFileInfo> GetInfoAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfClosedByServer();
            Interlocked.Increment(ref server._infoCalls);
            return Task.FromResult(server.Version);
        }

        public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfClosedByServer();
            var transfer = Interlocked.Increment(ref server._transfers);
            server.BeforeTransfer?.Invoke(transfer);
            server.TransferOffsets.Enqueue(offset);
            var fault = server.Faults.TryDequeue(out var next) ? next : null;
            return Task.FromResult<Stream>(new ReadLimitStream(new Transfer(server, offset, fault, () => ClosedByServer, _abort.Token), server.ReadTimeout, Abort));
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

        private void ThrowIfClosedByServer()
        {
            if (ClosedByServer)
            {
                throw new TransientTransportException("421 Timeout: the server closed the idle connection.", "421");
            }
        }
    }

    private sealed class Transfer(ScriptedTransferServer server, long offset, ScriptedFault? fault, Func<bool> closedByServer, CancellationToken aborted) : Stream
    {
        private readonly long? _dropAt = Earliest(server.DropAfter, fault, offset, ScriptedFaultKind.Drop);
        private readonly long? _stallAt = Earliest(Earliest(server.StallAfter, fault, offset, ScriptedFaultKind.Stall), fault, offset, ScriptedFaultKind.StallIgnoringToken);
        private readonly long? _endAt = Earliest(server.EndAfter, fault, offset, ScriptedFaultKind.EndEarly);
        private readonly bool _stallIgnoresToken = server.StallIgnoresToken || fault?.Kind == ScriptedFaultKind.StallIgnoringToken;
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
            if (closedByServer())
            {
                throw new IOException("Connection reset: the scripted server closed the idle transfer.");
            }

            if (_dropAt is { } drop && _position >= drop)
            {
                throw new IOException("Connection reset by the scripted server.");
            }

            if (_stallAt is { } stall && _position >= stall)
            {
                await StallAsync(cancellationToken).ConfigureAwait(false);
            }

            var end = (long)server.Content.Length;
            foreach (var limit in new[] { _dropAt, _stallAt, _endAt })
            {
                if (limit is { } bytes)
                {
                    end = Math.Min(end, bytes);
                }
            }

            var available = (int)Math.Min(Math.Min(count, server.MaxChunk), end - _position);
            if (available <= 0 && _extraLeft > 0 && _endAt is null)
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

        // The earlier of a fault at an offset of the file and this transfer's own fault of that kind.
        private static long? Earliest(long? always, ScriptedFault? fault, long start, ScriptedFaultKind kind)
        {
            if (fault?.Kind != kind)
            {
                return always;
            }

            var once = start + fault.AfterBytes;
            return always is { } at ? Math.Min(at, once) : once;
        }

        private async Task StallAsync(CancellationToken cancellationToken)
        {
            using var wait = _stallIgnoresToken
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
