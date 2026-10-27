using System.Collections.Concurrent;
using DbSus.Client.Transport;

namespace DbSus.Client.Tests.Fakes;

/// <summary>
/// In-memory stand-in for the DATASUS FTP server, with counters and failure injection, so the
/// client is tested in CI without any network.
/// </summary>
internal sealed class FakeServer
{
    public const string Root = "/dissemin/publicos";

    private readonly ConcurrentDictionary<string, FakeFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Func<Exception>> _connectFailures = new();
    private readonly ConcurrentQueue<Func<Exception>> _operationFailures = new();
    private readonly ConcurrentQueue<long> _transferFaults = new();
    private readonly ConcurrentQueue<int> _extraBytes = new();
    private int _activeConnections;
    private int _maxActiveConnections;
    private int _connectionsOpened;
    private int _transfers;
    private int _listCalls;
    private int _infoCalls;
    private long _bytesServed;
    private int _resumptionsThatTimeOut;
    private int _held;
    private int _overlaps;
    private readonly TaskCompletionSource<bool> _holdRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int ActiveConnections => _activeConnections;

    public int MaxActiveConnections => _maxActiveConnections;

    public int ConnectionsOpened => _connectionsOpened;

    public int Transfers => _transfers;

    public int ListCalls => _listCalls;

    public int InfoCalls => _infoCalls;

    public long BytesServed => Interlocked.Read(ref _bytesServed);

    public ConcurrentQueue<long> TransferOffsets { get; } = new();

    /// <summary>Called before each transfer starts (e.g. to republish the file).</summary>
    public Action<int>? BeforeTransfer { get; set; }

    /// <summary>Delays each transfer chunk, to make concurrent requests overlap.</summary>
    public TimeSpan ChunkDelay { get; set; }

    /// <summary>Gets or sets the most bytes a transfer serves to one read, 4096 by default.</summary>
    public int MaxChunk { get; set; } = 4096;

    /// <summary>
    /// Gets or sets which transfers (by full path and offset) wait, before they start, until
    /// <see cref="ReleaseHeld"/>; the thread that opens them is blocked meanwhile, as a server that
    /// does not answer would hold it.
    /// </summary>
    public Func<string, long, bool>? Hold { get; set; }

    /// <summary>Gets how many transfers have started to wait (see <see cref="Hold"/>).</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>
    /// Gets how many listings, info requests or transfers started on a connection whose previous
    /// transfer was not closed yet: an FTP connection takes one command at a time.
    /// </summary>
    public int Overlaps => Volatile.Read(ref _overlaps);

    /// <summary>Delays the closing of each transfer, as reading the server's final reply would.</summary>
    public TimeSpan TransferCloseDelay { get; set; }

    /// <summary>Delays the read that finds the end of each transfer, as a server slow to close it would.</summary>
    public TimeSpan TransferEndDelay { get; set; }

    /// <summary>
    /// Gets or sets how many of the next transfers from an offset beyond 0 fail to open, as the
    /// transport reports a resumption whose REST the server never answers (the opening limit ran
    /// out); <see cref="int.MaxValue"/> for all of them.
    /// </summary>
    public int ResumptionsThatTimeOut
    {
        get => Volatile.Read(ref _resumptionsThatTimeOut);
        set => Volatile.Write(ref _resumptionsThatTimeOut, value);
    }

    public static DateTime Time(int minute) => new(2025, 8, 8, 12, minute, 0, DateTimeKind.Utc);

    public void Add(string relativePath, byte[] content, DateTime? modifiedUtc = null) =>
        _files[Root + relativePath] = new FakeFile(content, modifiedUtc ?? Time(0));

    public void Remove(string relativePath) => _files.TryRemove(Root + relativePath, out _);

    public void FailNextConnects(int count, Func<Exception> failure)
    {
        for (var i = 0; i < count; i++)
        {
            _connectFailures.Enqueue(failure);
        }
    }

    /// <summary>The next listings, info requests or transfers fail, on an open connection.</summary>
    public void FailNextOperations(int count, Func<Exception> failure)
    {
        for (var i = 0; i < count; i++)
        {
            _operationFailures.Enqueue(failure);
        }
    }

    private void FailIfAsked()
    {
        if (_operationFailures.TryDequeue(out var failure))
        {
            throw failure();
        }
    }

    /// <summary>The next transfer drops the connection after serving <paramref name="bytes"/> bytes.</summary>
    public void FailNextTransferAfter(long bytes) => _transferFaults.Enqueue(bytes);

    /// <summary>The next <paramref name="transfers"/> transfers send <paramref name="bytes"/> bytes after the end of the file.</summary>
    public void SendExtraBytes(int transfers, int bytes)
    {
        for (var i = 0; i < transfers; i++)
        {
            _extraBytes.Enqueue(bytes);
        }
    }

    public IDatasusConnectionFactory Factory() => new Factory_(this);

    /// <summary>Lets every transfer held by <see cref="Hold"/> go on, and those that come after.</summary>
    public void ReleaseHeld() => _holdRelease.TrySetResult(true);

    private bool TakeResumptionTimeout()
    {
        for (var left = Volatile.Read(ref _resumptionsThatTimeOut); left > 0; left = Volatile.Read(ref _resumptionsThatTimeOut))
        {
            if (left == int.MaxValue || Interlocked.CompareExchange(ref _resumptionsThatTimeOut, left - 1, left) == left)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class Factory_(FakeServer server) : IDatasusConnectionFactory
    {
        public Task<IDatasusConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (server._connectFailures.TryDequeue(out var failure))
            {
                throw failure();
            }

            Interlocked.Increment(ref server._connectionsOpened);
            var active = Interlocked.Increment(ref server._activeConnections);
            InterlockedMax(ref server._maxActiveConnections, active);
            return Task.FromResult<IDatasusConnection>(new Connection(server));
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    private sealed class Connection(FakeServer server) : IDatasusConnection
    {
        private bool _disposed;
        private int _openTransfers;

        public bool IsBroken => false;

        public void TransferClosed() => Interlocked.Decrement(ref _openTransfers);

        private void CountOverlap()
        {
            if (Volatile.Read(ref _openTransfers) > 0)
            {
                Interlocked.Increment(ref server._overlaps);
            }
        }

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string directory, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref server._listCalls);
            CountOverlap();
            server.FailIfAsked();
            var prefix = directory.TrimEnd('/') + "/";
            var entries = new Dictionary<string, RemoteEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in server._files)
            {
                if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rest = pair.Key.Substring(prefix.Length);
                var slash = rest.IndexOf('/');
                entries[slash < 0 ? rest : rest.Substring(0, slash)] = slash < 0
                    ? new RemoteEntry(rest, pair.Value.Content.Length, isDirectory: false)
                    : new RemoteEntry(rest.Substring(0, slash), 0, isDirectory: true);
            }

            return Task.FromResult<IReadOnlyList<RemoteEntry>>([.. entries.Values]);
        }

        public Task<RemoteFileInfo> GetInfoAsync(string path, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref server._infoCalls);
            CountOverlap();
            server.FailIfAsked();
            return server._files.TryGetValue(path, out var file)
                ? Task.FromResult(new RemoteFileInfo(file.Content.Length, file.ModifiedUtc))
                : throw new FileNotFoundException($"550 {path}: The system cannot find the file specified.");
        }

        public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken)
        {
            var transfer = Interlocked.Increment(ref server._transfers);
            CountOverlap();
            server.TransferOffsets.Enqueue(offset);
            server.BeforeTransfer?.Invoke(transfer);
            if (server.Hold?.Invoke(path, offset) == true)
            {
                Interlocked.Increment(ref server._held);
                server._holdRelease.Task.Wait(60_000, CancellationToken.None);
            }

            server.FailIfAsked();
            if (offset > 0 && server.TakeResumptionTimeout())
            {
                throw new TransientTransportException($"FTP transport failure while opening {path} for reading: the server did not open the resumed transfer within 5 s, so the connection was closed.")
                {
                    IsExpiredLimit = true,
                };
            }

            if (!server._files.TryGetValue(path, out var file))
            {
                throw new FileNotFoundException($"550 {path}");
            }

            long? failAfter = server._transferFaults.TryDequeue(out var bytes) ? bytes : null;
            var extra = server._extraBytes.TryDequeue(out var more) ? more : 0;
            Interlocked.Increment(ref _openTransfers);
            return Task.FromResult<Stream>(new TransferStream(server, this, file.Content, offset, failAfter, extra));
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Interlocked.Decrement(ref server._activeConnections);
            }
        }
    }

    private sealed class TransferStream(FakeServer server, Connection connection, byte[] content, long offset, long? failAfter, int extra) : Stream
    {
        private long _position = offset;
        private long _served;
        private int _extraLeft = extra;
        private int _closed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (server.ChunkDelay > TimeSpan.Zero)
            {
                Thread.Sleep(server.ChunkDelay);
            }

            if (failAfter is { } limit && _served >= limit)
            {
                throw new IOException("Connection reset by the fake server.");
            }

            var available = (int)Math.Min(Math.Min(count, server.MaxChunk), content.Length - _position);
            if (failAfter is { } cap)
            {
                available = (int)Math.Min(available, cap - _served);
            }

            if (available <= 0 && _extraLeft > 0 && failAfter is null)
            {
                // Bytes the file does not have, as a server that sends too much would.
                var junk = Math.Min(Math.Min(count, 4096), _extraLeft);
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
                if (server.TransferEndDelay > TimeSpan.Zero)
                {
                    Thread.Sleep(server.TransferEndDelay);
                }

                return 0;
            }

            Array.Copy(content, _position, buffer, offset, available);
            _position += available;
            _served += available;
            Interlocked.Add(ref server._bytesServed, available);
            return available;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
            {
                if (server.TransferCloseDelay > TimeSpan.Zero)
                {
                    Thread.Sleep(server.TransferCloseDelay);
                }

                connection.TransferClosed();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FakeFile(byte[] content, DateTime modifiedUtc)
    {
        public byte[] Content { get; } = content;

        public DateTime ModifiedUtc { get; } = modifiedUtc;
    }
}
