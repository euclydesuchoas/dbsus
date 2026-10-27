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
    private volatile bool _disposed;

    public async Task<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        Guard.NotDisposed(_disposed, this);
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
        _disposed = true;
        while (_idle.TryTake(out var connection))
        {
            connection.Dispose();
        }
    }

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
            if (connection is not null && healthy && !connection.IsBroken && !_disposed)
            {
                _idle.Add(connection);
            }
            else
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
