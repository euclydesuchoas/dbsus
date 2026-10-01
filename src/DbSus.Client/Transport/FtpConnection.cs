using System.Globalization;
using System.Net.Sockets;
using FluentFTP;
using FluentFTP.Exceptions;

namespace DbSus.Client.Transport;

/// <summary>
/// FTP transport over FluentFTP. The DATASUS server is IIS FTP: anonymous, plain FTP only (it
/// refuses TLS), with SIZE, MDTM and REST but no MLSD.
/// </summary>
internal sealed class FtpConnectionFactory(DatasusClientOptions options) : IDatasusConnectionFactory
{
    public async Task<IDatasusConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var config = new FtpConfig
        {
            // Plain text on purpose: the server does not offer TLS. Integrity comes from the size
            // check and the DBC's CRC-32, which detects corruption but not tampering.
            EncryptionMode = FtpEncryptionMode.None,
            DataConnectionType = FtpDataConnectionType.AutoPassive,
            ConnectTimeout = Milliseconds(options.ConnectTimeout),
            ReadTimeout = Milliseconds(options.ReadTimeout),
            DataConnectionConnectTimeout = Milliseconds(options.ConnectTimeout),
            DataConnectionReadTimeout = Milliseconds(options.ReadTimeout),
            SocketKeepAlive = true,
        };
        var client = new AsyncFtpClient(options.Host, "anonymous", "anonymous@", options.Port, config);
        try
        {
            await FtpConnection.Translate(async () =>
            {
                await client.Connect(cancellationToken).ConfigureAwait(false);
                await FtpConnection.Expect(client.Execute("TYPE I", cancellationToken), "TYPE I").ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
            return new FtpConnection(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static int Milliseconds(TimeSpan value) => (int)Math.Min(int.MaxValue, value.TotalMilliseconds);
}

internal sealed class FtpConnection(AsyncFtpClient client) : IDatasusConnection
{
    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string directory, CancellationToken cancellationToken) =>
        Translate<IReadOnlyList<RemoteEntry>>(async () =>
        {
            // ForceList: the server has no MLSD; its LIST output (DOS format) is parsed by FluentFTP.
            var items = await client.GetListing(directory, FtpListOption.ForceList, cancellationToken).ConfigureAwait(false);
            return
            [
                .. items
                    .Where(item => item.Type is FtpObjectType.File or FtpObjectType.Directory)
                    .Select(item => new RemoteEntry(item.Name, item.Type == FtpObjectType.File ? item.Size : 0, item.Type == FtpObjectType.Directory)),
            ];
        });

    public Task<RemoteFileInfo> GetInfoAsync(string path, CancellationToken cancellationToken) =>
        Translate(async () =>
        {
            // Raw SIZE and MDTM: MDTM is UTC by definition (RFC 3659), so it is parsed here rather
            // than through FluentFTP's time-zone conversion.
            var size = await Expect(client.Execute("SIZE " + path, cancellationToken), path).ConfigureAwait(false);
            var modified = await Expect(client.Execute("MDTM " + path, cancellationToken), path).ConfigureAwait(false);
            return new RemoteFileInfo(
                long.Parse(size.Message.Trim(), NumberStyles.None, CultureInfo.InvariantCulture),
                DateTime.SpecifyKind(
                    DateTime.ParseExact(modified.Message.Trim().Substring(0, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture),
                    DateTimeKind.Utc));
        });

    public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken) =>
        Translate(() => client.OpenRead(path, FtpDataType.Binary, offset, checkIfFileExists: false, cancellationToken));

    public void Dispose() => client.Dispose();

    internal static async Task<FtpReply> Expect(Task<FtpReply> command, string subject)
    {
        var reply = await command.ConfigureAwait(false);
        if (reply.Success)
        {
            return reply;
        }

        throw Classify(reply.Code, $"The server answered '{reply.Code} {reply.Message}' for {subject}.", null);
    }

    // Maps FluentFTP and socket failures to the transport contract: not found, transient, permanent.
    internal static async Task<T> Translate<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (FtpCommandException exception)
        {
            throw Classify(exception.CompletionCode, exception.Message, exception);
        }
        catch (Exception exception) when (exception is FtpException or IOException or SocketException or TimeoutException
                                          && exception is not FileNotFoundException and not TransientTransportException)
        {
            throw new TransientTransportException($"FTP transport failure: {exception.Message}", exception);
        }
    }

    private static Exception Classify(string? code, string message, Exception? inner)
    {
        if (code == "550")
        {
            return inner is null ? new FileNotFoundException(message) : new FileNotFoundException(message, inner);
        }

        if (code is { Length: 3 } && code[0] == '4')
        {
            return inner is null ? new TransientTransportException(message) : new TransientTransportException(message, inner);
        }

        return inner is null ? new IOException(message) : new IOException(message, inner);
    }
}
