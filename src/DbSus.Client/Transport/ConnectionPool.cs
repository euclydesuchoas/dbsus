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

    // The place is freed whatever disposing the connection throws; the exception still propagates.
    private void Return(IDatasusConnection connection, bool healthy)
    {
        try
        {
            if (healthy && !connection.IsBroken && !_disposed)
            {
                _idle.Add(connection);
            }
            else
            {
                connection.Dispose();
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
        private bool _keep;
        private bool _returned;

        public IDatasusConnection Connection { get; } = connection;

        public void Keep() => _keep = true;

        public void Dispose()
        {
            if (_returned)
            {
                return;
            }

            _returned = true;
            pool.Return(Connection, _keep);
        }
    }
}
