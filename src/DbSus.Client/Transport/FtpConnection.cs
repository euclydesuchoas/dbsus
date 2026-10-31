using System.Globalization;
using System.Net.Sockets;
using FluentFTP;
using FluentFTP.Exceptions;

namespace DbSus.Client.Transport;

/// <summary>
/// FTP transport over FluentFTP. The DATASUS server is IIS FTP: anonymous, plain FTP (it lists
/// AUTH TLS but refuses it with 534, local policy, as tried in October 2026), with SIZE, MDTM and REST but no MLSD.
/// </summary>
internal sealed class FtpConnectionFactory(DatasusClientOptions options) : IDatasusConnectionFactory
{
    public async Task<IDatasusConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var connection = new FtpConnection(FtpSession.Create(options.Host, options.Port, CreateConfig(options)), options.Host, options.Port, options.ReadTimeout, options.ResumeOpenTimeout);
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

    internal static FtpConfig CreateConfig(DatasusClientOptions options) => new()
    {
        // Plain text on purpose: the server refuses AUTH TLS (534, October 2026). Integrity comes
        // from the size check and the DBC's CRC-32, which detects corruption but not tampering.
        EncryptionMode = FtpEncryptionMode.None,

        // PASV with the address of its reply ignored: every data connection goes to Host, never to
        // an address the server names. Over IPv6, FluentFTP sends EPSV in its place.
        DataConnectionType = FtpDataConnectionType.PASVEX,
        ConnectTimeout = Milliseconds(options.ConnectTimeout),
        ReadTimeout = Milliseconds(options.ReadTimeout),
        DataConnectionConnectTimeout = Milliseconds(options.ConnectTimeout),
        DataConnectionReadTimeout = Milliseconds(options.ReadTimeout),
        SocketKeepAlive = true,
    };

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

internal sealed class FtpConnection(FtpSession session, string host, int port, TimeSpan readTimeout, TimeSpan resumeOpenTimeout) : IDatasusConnection, IAbortableConnection
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
        RequireOneLine(directory);
        var step = FtpStep.Listing(directory, host, port);
        return Guard<IReadOnlyList<RemoteEntry>>(step, ConnectCount, async () =>
        {
            // ForceList: the server has no MLSD; its LIST output (DOS format) is parsed by FluentFTP.
            FtpListItem[] items;
            try
            {
                items = await session.GetListingAsync(directory, FtpListOption.ForceList, cancellationToken).ConfigureAwait(false);
            }
            catch (FtpCommandException exception) when (exception.CompletionCode is { Length: 3 } code && code[0] == '5')
            {
                throw ListingRefused(step, code, exception.Message, exception);
            }

            // FluentFTP 55 returns what it has read, as a success, when the data connection fails
            // while the control connection stays open, and when the final reply does not arrive in
            // time. Only a completion reply (2xx) to LIST confirms that the listing is whole.
            var last = session.LastReply;
            if (last.Type != FtpResponseType.PositiveCompletion || !last.Command.StartsWith("LIST", StringComparison.Ordinal))
            {
                // FluentFTP 55 also takes a 550 to LIST for an empty folder and returns no entries.
                // A permanent reply (5xx) to LIST is the server refusing the listing: asking again
                // would get the same answer.
                if (last.Type == FtpResponseType.PermanentNegativeCompletion && last.Command.StartsWith("LIST", StringComparison.Ordinal))
                {
                    throw ListingRefused(step, last.Code, last.Message, null);
                }

                throw new TransientTransportException(
                    $"FTP transport failure while {step.Description}: the server did not confirm the end of the listing (last reply: '{last.Code} {ServerText.Clean(last.Message)}'), so it is not used.");
            }

            return
            [
                .. items
                    .Where(item => item.Type is FtpObjectType.File or FtpObjectType.Directory)
                    .Select(item => new RemoteEntry(item.Name, item.Type == FtpObjectType.File ? item.Size : 0, item.Type == FtpObjectType.Directory)),
            ];
        }, cancellationToken);
    }

    public Task<RemoteFileInfo> GetInfoAsync(string path, CancellationToken cancellationToken)
    {
        RequireOneLine(path);
        return Guard(FtpStep.ReadingInfo(path, host, port), ConnectCount, async () =>
        {
            // Raw SIZE and MDTM: MDTM is UTC by definition (RFC 3659), so it is parsed here rather
            // than through FluentFTP's time-zone conversion.
            var size = await Expect(session.ExecuteAsync("SIZE " + path, cancellationToken), path).ConfigureAwait(false);
            var modified = await Expect(session.ExecuteAsync("MDTM " + path, cancellationToken), path).ConfigureAwait(false);
            return new RemoteFileInfo(ParseSize(size.Message, path), ParseModified(modified.Message, path));
        }, cancellationToken);
    }

    // A SIZE reply that is not a size (not digits, or too large for a long) is a transport failure
    // like a truncated transfer, never a parsing exception of the framework.
    internal static long ParseSize(string reply, string path) =>
        long.TryParse(reply.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            ? size
            : throw Malformed("SIZE", reply, path);

    // MDTM is yyyyMMddHHmmss, optionally followed by a fraction of a second, which is ignored.
    internal static DateTime ParseModified(string reply, string path)
    {
        var text = reply.Trim();
        return text.Length >= 14
               && DateTime.TryParseExact(text.Substring(0, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var modified)
            ? DateTime.SpecifyKind(modified, DateTimeKind.Utc)
            : throw Malformed("MDTM", reply, path);
    }

    private static TransientTransportException Malformed(string command, string reply, string path) =>
        new($"The server answered {command} for {path} with '{ServerText.Clean(reply.Trim())}', which is not a {(command == "SIZE" ? "size" : "time")}; the answer is not used.");

    // The client limits each read of the transfer itself (see ReadLimitStream). Opening a transfer
    // that resumes, from an offset beyond 0, has a limit of its own (see Resumption.OpenTimeout);
    // opening one from the start keeps the read timeout alone.
    public async Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken)
    {
        RequireOneLine(path);
        var stream = await Guard(
            FtpStep.OpeningForReading(path, host, port),
            ConnectCount,
            () => session.OpenReadAsync(path, offset, cancellationToken),
            cancellationToken,
            offset > 0 ? resumeOpenTimeout : null).ConfigureAwait(false);
        return new ReadLimitStream(stream, readTimeout, Abort);
    }

    public void Dispose() => session.Dispose();

    // Defense in depth: the paths that reach here were checked (RemotePath), but a CR, LF or NUL in
    // one would end the FTP command early and start another, so none is ever sent.
    private static void RequireOneLine(string path)
    {
        if (path.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("A path with a line break or a NUL character is never sent to the FTP server.", nameof(path));
        }
    }

    internal static async Task<FtpReply> Expect(Task<FtpReply> command, string subject)
    {
        var reply = await command.ConfigureAwait(false);
        if (reply.Success)
        {
            return reply;
        }

        throw Classify(reply.Code, $"The server answered '{reply.Code} {ServerText.Clean(reply.Message)}' for {subject}.", null);
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
        catch (FtpSanitizeException exception)
        {
            // The FTP library refuses the path itself, before sending it: asking again would get the
            // same answer, so it is never retried.
            throw new ArgumentException(
                $"The FTP library refused the path while {step.Description}: {ServerText.Clean(exception.Message)}",
                exception);
        }
        catch (FtpCommandException exception)
        {
            throw Classify(exception.CompletionCode, $"FTP reply while {step.Description}: {ServerText.Clean(exception.Message)}", exception);
        }
        catch (FtpLibraryFailureException exception)
        {
            // Worth retrying, on another connection: this one is never used again, since a
            // connection goes back to the pool only after a clean operation.
            var original = exception.InnerException!;
            throw new TransientTransportException(
                $"FTP transport failure while {step.Description}: the FTP library failed with an unexpected {original.GetType().Name} ({ServerText.Clean(original.Message)}) and the connection was discarded.",
                original)
            {
                IsLibraryFailure = true,
            };
        }
        catch (Exception exception) when (exception is FtpException or IOException or SocketException or TimeoutException
                                          && exception is not FileNotFoundException and not TransientTransportException
                                          and not DatasusTransferException)
        {
            var reopened = connectCount is not null && connectCount() != connectsBefore;
            var hint = ConnectionHint(step, exception, reopened);
            var message = ServerText.Clean(exception.Message);
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
    public void Abort()
    {
        _broken = true;
        session.Abort();
    }

    // Runs a step so that the cancellation token is meant to end it on every runtime: canceling
    // closes the connection, which ends any pending read even where the library does not observe
    // the token. A step with a limit is ended the same way when the limit runs out, and that is a
    // transient failure, never a cancellation: the caller's token alone makes one.
    private async Task<T> Guard<T>(FtpStep step, Func<int>? connectCount, Func<Task<T>> operation, CancellationToken cancellationToken, TimeSpan? limit = null)
    {
        using var expiry = limit is { } time ? new CancellationTokenSource(time) : null;
        using (cancellationToken.Register(Abort))
        using (expiry is null ? default : expiry.Token.Register(Abort))
        {
            try
            {
                var result = await Translate(step, connectCount, operation).ConfigureAwait(false);

                // The limit ran out just as the step ended: the connection is closed already.
                if (expiry is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        (result as IDisposable)?.Dispose();
                    }
                    catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or FtpException)
                    {
                    }

                    throw Expired(step, limit!.Value, null);
                }

                return result;
            }
            catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
            {
                throw new OperationCanceledException($"Canceled while {step.Description}; the connection was closed.", exception, cancellationToken);
            }
            catch (Exception exception) when (expiry is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested && exception is not TransientTransportException { IsExpiredLimit: true })
            {
                throw Expired(step, limit!.Value, exception);
            }
        }
    }

    private static TransientTransportException Expired(FtpStep step, TimeSpan limit, Exception? inner) =>
        new($"FTP transport failure while {step.Description}: the server did not open the resumed transfer within {limit.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s, so the connection was closed.", null, inner)
        {
            IsExpiredLimit = true,
        };

    // A refused listing is never a missing file: FTP uses the same 550 for a folder that does not
    // exist and for one that may not be read, and the text of the reply is not interpreted.
    private static DatasusRequestRefusedException ListingRefused(FtpStep step, string code, string reply, Exception? inner)
    {
        // FluentFTP's exception repeats the code before the text of the reply.
        var libraryPrefix = $"Code: {code} Message: ";
        var text = reply.StartsWith(libraryPrefix, StringComparison.Ordinal) ? reply.Substring(libraryPrefix.Length) : reply;
        return new DatasusRequestRefusedException(
            $"FTP reply while {step.Description}: the server refused the listing ('{code} {ServerText.Clean(text.Trim())}'). FTP uses 550 both for a folder that does not exist and for one that may not be read.",
            code,
            inner);
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
