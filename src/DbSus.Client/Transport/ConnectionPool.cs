using System.Collections.Concurrent;

namespace DbSus.Client.Transport;

/// <summary>
/// Lends connections to one operation at a time, never more than the configured maximum at once,
/// and keeps idle healthy connections for reuse (fewer logins on the DATASUS server).
/// </summary>
internal sealed class ConnectionPool(IDatasusConnectionFactory factory, int maxConnections) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(maxConnections, maxConnections);
    private readonly ConcurrentBag<IDatasusConnection> _idle = [];

    // Taken by Return and Dispose, so that a connection is never added to _idle after Dispose has emptied it.
    private readonly object _gate = new();

    // Canceled by Dispose, so that an operation waiting for a place stops waiting. Never disposed:
    // AcquireAsync may still link to it while Dispose runs.
    private readonly CancellationTokenSource _closing = new();
    private volatile bool _disposed;

    /// <summary>Gets how many places are free; tests check it.</summary>
    internal int FreePlaces => _slots.CurrentCount;

    /// <summary>
    /// Lends a place and a connection. An operation that waits for a place when the pool is
    /// disposed, or gets one afterward, connects nothing: it fails with
    /// <see cref="ObjectDisposedException"/>, and the place it may have got is free again.
    /// </summary>
    public async Task<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        Guard.NotDisposed(_disposed, this);
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token))
        {
            try
            {
                await _slots.WaitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_closing.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw Closed();
            }
        }

        try
        {
            if (_disposed)
            {
                throw Closed();
            }

            var connection = _idle.TryTake(out var idle) ? idle : await factory.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, connection);
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _closing.Cancel();

        while (_idle.TryTake(out var connection))
        {
            connection.Dispose();
        }
    }

    private static ObjectDisposedException Closed() =>
        new(nameof(ConnectionPool), "The connections were closed while the operation waited for one, so no new connection is made.");

    // A connection for a lease that keeps its place; none once the pool is disposed.
    private Task<IDatasusConnection> ConnectInPlaceAsync(CancellationToken cancellationToken) =>
        _disposed
            ? throw new ObjectDisposedException(nameof(ConnectionPool), "The connections were closed, so no new connection is made.")
            : factory.ConnectAsync(cancellationToken);

    // The place is freed whatever disposing the connection throws; the exception still propagates.
    // A lease whose replacement failed has no connection left to keep or close.
    private void Return(IDatasusConnection? connection, bool healthy)
    {
        try
        {
            var kept = false;
            if (connection is not null && healthy && !connection.IsBroken)
            {
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _idle.Add(connection);
                        kept = true;
                    }
                }
            }

            if (!kept)
            {
                connection?.Dispose();
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// A borrowed connection. Call <see cref="Keep"/> after a clean operation; otherwise disposing
    /// the lease closes the connection, which is the safe choice after any failure or abort.
    /// </summary>
    internal sealed class Lease(ConnectionPool pool, IDatasusConnection connection) : IDisposable
    {
        private IDatasusConnection? _connection = connection;
        private bool _keep;
        private bool _returned;

        /// <summary>Gets the connection; there is none after a replacement that failed.</summary>
        public IDatasusConnection Connection => _connection ?? throw new InvalidOperationException("This lease has no connection: replacing it failed.");

        public void Keep() => _keep = true;

        /// <summary>Gets a value indicating whether the pool is disposed, so that no new connection is made.</summary>
        public bool IsPoolDisposed => pool._disposed;

        /// <summary>
        /// Keeps the connection when it can still be used, and otherwise connects a new one in its
        /// place, as <see cref="ReplaceConnectionAsync"/> does. Only the operation that holds the lease calls it.
        /// </summary>
        /// <exception cref="ObjectDisposedException">A new connection was needed and the pool is disposed.</exception>
        public Task EnsureConnectionAsync(CancellationToken cancellationToken) =>
            _connection is { IsBroken: false } ? Task.CompletedTask : ReplaceConnectionAsync(cancellationToken);

        /// <summary>Closes the connection by force, when it can be; it is then never kept.</summary>
        public void Abort() => (_connection as IAbortableConnection)?.Abort();

        /// <summary>
        /// Discards the connection and connects a new one in its place, without freeing the place
        /// in between, so that no other operation takes it. Only the operation that holds the lease
        /// calls it. When connecting fails, or the pool is disposed, the lease is left without a
        /// connection; disposing it then frees the place, once, and closes nothing more.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The pool is disposed; no connection is made.</exception>
        public async Task ReplaceConnectionAsync(CancellationToken cancellationToken)
        {
            Guard.NotDisposed(_returned, this);
            var discarded = _connection;
            _connection = null;
            _keep = false;
            discarded?.Dispose();
            _connection = await pool.ConnectInPlaceAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_returned)
            {
                return;
            }

            _returned = true;
            pool.Return(_connection, _keep);
        }
    }
}
