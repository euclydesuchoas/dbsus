namespace DbSus.Client.Transport;

/// <summary>
/// A connection that can be closed by force, from any thread, so that a transfer left in the
/// middle ends at once instead of waiting for the server's reply. The connection is then broken
/// and never reused. Connections without it are closed by disposing them.
/// </summary>
internal interface IAbortableConnection
{
    void Abort();
}
