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
        var connection = new FtpConnection(FtpSession.Create(options.Host, options.Port, config), options.Host, options.Port, options.ReadTimeout);
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

    private static int Milliseconds(TimeSpan value) => (int)Math.Min(int.MaxValue, value.TotalMilliseconds);
}

/// <summary>
/// What a connection was doing when it failed, for the error message: the step, the server, and
/// whether the step opens a passive data connection besides the control connection.
/// </summary>
internal sealed class FtpStep
{
    private FtpStep(string description, int port, bool opensDataConnection)
    {
        Description = description;
        Port = port;
        OpensDataConnection = opensDataConnection;
    }

    public string Description { get; }

    public int Port { get; }

    public bool OpensDataConnection { get; }

    public static FtpStep Connecting(string host, int port) => new($"connecting to {host}:{port}", port, opensDataConnection: false);

    public static FtpStep Listing(string directory, string host, int port) => new($"listing {directory} on {host}:{port}", port, opensDataConnection: true);

    public static FtpStep ReadingInfo(string path, string host, int port) => new($"reading the size and date of {path} on {host}:{port}", port, opensDataConnection: false);

    public static FtpStep OpeningForReading(string path, string host, int port) => new($"opening {path} for reading on {host}:{port}", port, opensDataConnection: true);
}

internal sealed class FtpConnection(FtpSession session, string host, int port, TimeSpan readTimeout) : IDatasusConnection
{
    private volatile bool _broken;

    public bool IsBroken => _broken;

    public Task OpenAsync(CancellationToken cancellationToken) =>
        Guard(FtpStep.Connecting(host, port), null, async () =>
        {
            await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await Expect(session.ExecuteAsync("TYPE I", cancellationToken), "TYPE I").ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string directory, CancellationToken cancellationToken)
    {
        var step = FtpStep.Listing(directory, host, port);
        return Guard<IReadOnlyList<RemoteEntry>>(step, ConnectCount, async () =>
        {
            // ForceList: the server has no MLSD; its LIST output (DOS format) is parsed by FluentFTP.
            var items = await session.GetListingAsync(directory, FtpListOption.ForceList, cancellationToken).ConfigureAwait(false);

            // FluentFTP 55 returns what it has read, as a success, when the data connection fails
            // while the control connection stays open, and when the final reply does not arrive in
            // time. Only a completion reply (2xx) to LIST confirms that the listing is whole.
            var last = session.LastReply;
            if (last.Type != FtpResponseType.PositiveCompletion || !last.Command.StartsWith("LIST", StringComparison.Ordinal))
            {
                throw new TransientTransportException(
                    $"FTP transport failure while {step.Description}: the server did not confirm the end of the listing (last reply: '{last.Code} {last.Message}'), so it is not used.");
            }

            return
            [
                .. items
                    .Where(item => item.Type is FtpObjectType.File or FtpObjectType.Directory)
                    .Select(item => new RemoteEntry(item.Name, item.Type == FtpObjectType.File ? item.Size : 0, item.Type == FtpObjectType.Directory)),
            ];
        }, cancellationToken);
    }

    public Task<RemoteFileInfo> GetInfoAsync(string path, CancellationToken cancellationToken) =>
        Guard(FtpStep.ReadingInfo(path, host, port), ConnectCount, async () =>
        {
            // Raw SIZE and MDTM: MDTM is UTC by definition (RFC 3659), so it is parsed here rather
            // than through FluentFTP's time-zone conversion.
            var size = await Expect(session.ExecuteAsync("SIZE " + path, cancellationToken), path).ConfigureAwait(false);
            var modified = await Expect(session.ExecuteAsync("MDTM " + path, cancellationToken), path).ConfigureAwait(false);
            return new RemoteFileInfo(
                long.Parse(size.Message.Trim(), NumberStyles.None, CultureInfo.InvariantCulture),
                DateTime.SpecifyKind(
                    DateTime.ParseExact(modified.Message.Trim().Substring(0, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture),
                    DateTimeKind.Utc));
        }, cancellationToken);

    // The client limits each read of the transfer itself (see ReadLimitStream).
    public async Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken)
    {
        var stream = await Guard(
            FtpStep.OpeningForReading(path, host, port),
            ConnectCount,
            () => session.OpenReadAsync(path, offset, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new ReadLimitStream(stream, readTimeout, Abort);
    }

    public void Dispose() => session.Dispose();

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
    // connectCount reads how many times FluentFTP has opened the control connection: it reopens it
    // by itself, in the middle of an operation, when it finds it closed.
    internal static async Task<T> Translate<T>(FtpStep step, Func<int>? connectCount, Func<Task<T>> operation)
    {
        var connectsBefore = connectCount?.Invoke() ?? 0;
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (FtpCommandException exception)
        {
            throw Classify(exception.CompletionCode, $"FTP reply while {step.Description}: {exception.Message}", exception);
        }
        catch (Exception exception) when (exception is FtpException or IOException or SocketException or TimeoutException
                                          && exception is not FileNotFoundException and not TransientTransportException
                                          and not DatasusTransferException)
        {
            var reopened = connectCount is not null && connectCount() != connectsBefore;
            var hint = ConnectionHint(step, exception, reopened);
            var message = exception.Message;
            var detail = hint.Length == 0 || (message.Length > 0 && message[message.Length - 1] is '.' or '!') ? message : message + ".";
            throw new TransientTransportException($"FTP transport failure while {step.Description}: {detail}{hint}", exception);
        }
    }

    // What is known about a failed connection attempt, without claiming which connection failed.
    private static string ConnectionHint(FtpStep step, Exception exception, bool controlReopened)
    {
        if (!IsConnectFailure(exception))
        {
            return controlReopened ? " The control connection was reopened during this step." : string.Empty;
        }

        if (!step.OpensDataConnection)
        {
            return controlReopened ? " The control connection was reopened during this step and a connection attempt failed." : string.Empty;
        }

        var possibleCause = $"a possible cause is a passive data connection being blocked (for example by a firewall or NAT that lets port {step.Port} through but not the server's passive ports)";
        return controlReopened
            ? $" The control connection was reopened during this step, so the connection attempt that failed may be the control connection or the passive data connection; {possibleCause}."
            : $" The control connection was not reopened during this step; {possibleCause}.";
    }

    // FluentFTP 55 reports a failed TCP connect, for the control or the data connection alike, as a
    // TimeoutException ("Timed out trying to connect..."), a SocketException (e.g. refused), or an
    // IOException ("Failed to connect to host.", when the name does not resolve or every address fails).
    private static bool IsConnectFailure(Exception exception) => exception switch
    {
        TimeoutException timeout => timeout.Message.StartsWith("Timed out trying to connect", StringComparison.Ordinal),
        SocketException socket => socket.SocketErrorCode is SocketError.ConnectionRefused or SocketError.TimedOut
            or SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.HostDown or SocketError.NetworkDown,
        IOException io => io.Message == "Failed to connect to host.",
        _ => false,
    };

    private int ConnectCount() => session.ConnectCount;

    // Closes the connection by force; it is never reused.
    private void Abort()
    {
        _broken = true;
        session.Abort();
    }

    // Runs a step so that the cancellation token ends it on every runtime: cancelling closes the
    // connection, which ends any pending read even where the library does not observe the token.
    private async Task<T> Guard<T>(FtpStep step, Func<int>? connectCount, Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        using (cancellationToken.Register(Abort))
        {
            try
            {
                return await Translate(step, connectCount, operation).ConfigureAwait(false);
            }
            catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
            {
                throw new OperationCanceledException($"Canceled while {step.Description}; the connection was closed.", exception, cancellationToken);
            }
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
            return new TransientTransportException(message, code, inner);
        }

        return new DatasusRequestRefusedException(message, code, inner);
    }
}
