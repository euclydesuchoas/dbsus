using DbSus.Client.Transport;

namespace DbSus.Client.Tests.Fakes;

/// <summary>
/// The client's FTP connections, made as <see cref="FtpConnectionFactory"/> makes them, for a test of
/// a transfer that stalls, against the loopback server. The client gives its read timeout to every
/// read of a connection: to the transfer (each read of it, and its data connection), and also to
/// the replies on the control connection, from the greeting and the login to SIZE, MDTM and REST.
/// Such a test wants a short one, to see the stall soon. Here it limits the transfer alone, as in
/// the client, and the replies get <see cref="TestTimes.Read"/>, which a slow or busy runner does
/// not reach: the test never fails because a reply, on the connection that resumes say, took longer
/// than the stall it looks for, nor because such a reply ran out before a shorter limit or a
/// cancellation that the test waits for.
/// </summary>
internal sealed class TransferTimeoutConnectionFactory(DatasusClientOptions options) : IDatasusConnectionFactory
{
    public async Task<IDatasusConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var config = FtpConnectionFactory.CreateConfig(options);
        config.ReadTimeout = (int)Math.Max(TestTimes.Read.TotalMilliseconds, options.ReadTimeout.TotalMilliseconds);
        var connection = new FtpConnection(FtpSession.Create(options.Host, options.Port, config), options.Host, options.Port, options.ReadTimeout, options.ResumeOpenTimeout);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
