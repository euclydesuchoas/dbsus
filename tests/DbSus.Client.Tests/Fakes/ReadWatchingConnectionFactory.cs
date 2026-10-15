using DbSus.Client.Transport;

namespace DbSus.Client.Tests.Fakes;

/// <summary>
/// Wraps a real connection factory and tells when a download asks for more after it has received
/// data: by then the client has written that data to its partial file and waits for the rest, so a
/// test can cancel in the middle of a transfer without guessing how long the transfer takes.
/// </summary>
internal sealed class ReadWatchingConnectionFactory(IDatasusConnectionFactory inner) : IDatasusConnectionFactory
{
    private readonly TaskCompletionSource<bool> _readAfterData = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets a task that completes when a read starts on a download stream that has already returned data.</summary>
    public Task ReadAfterData => _readAfterData.Task;

    public async Task<IDatasusConnection> ConnectAsync(CancellationToken cancellationToken) =>
        new Connection(await inner.ConnectAsync(cancellationToken).ConfigureAwait(false), _readAfterData);

    private sealed class Connection(IDatasusConnection inner, TaskCompletionSource<bool> readAfterData) : IDatasusConnection
    {
        public bool IsBroken => inner.IsBroken;

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string directory, CancellationToken cancellationToken) => inner.ListAsync(directory, cancellationToken);

        public Task<RemoteFileInfo> GetInfoAsync(string path, CancellationToken cancellationToken) => inner.GetInfoAsync(path, cancellationToken);

        public async Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken) =>
            new WatchedStream(await inner.OpenReadAsync(path, offset, cancellationToken).ConfigureAwait(false), readAfterData);

        public void Dispose() => inner.Dispose();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1835", Justification = "The client reads through the array overload, the one netstandard2.0 has.")]
    private sealed class WatchedStream(Stream inner, TaskCompletionSource<bool> readAfterData) : Stream
    {
        private bool _returnedData;

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
            Starting();
            return Returned(await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false));
        }

#if NET
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Starting();
            return Returned(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        }
#endif

        public override int Read(byte[] buffer, int offset, int count)
        {
            Starting();
            return Returned(inner.Read(buffer, offset, count));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Starting()
        {
            if (_returnedData)
            {
                readAfterData.TrySetResult(true);
            }
        }

        private int Returned(int read)
        {
            _returnedData |= read > 0;
            return read;
        }
    }
}
