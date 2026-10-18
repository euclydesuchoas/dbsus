using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DbSus.Client.Tests.Fakes;

/// <summary>How the loopback server ends a listing it sends.</summary>
internal enum ListingEnd
{
    /// <summary>Closes the data connection, then replies 226.</summary>
    Complete,

    /// <summary>Closes the data connection normally and never sends the final reply.</summary>
    CloseWithoutReply,

    /// <summary>Resets the data connection and never sends the final reply.</summary>
    ResetWithoutReply,

    /// <summary>Resets the data connection, then replies 426.</summary>
    ResetThenAbort,

    /// <summary>Opens the data connection and sends nothing, keeping both connections open and silent.</summary>
    Stall,
}

/// <summary>
/// A minimal FTP server on the loopback interface, enough for FluentFTP to log in, list and
/// download, so the real transport can be tested without any network. Without a
/// <see cref="Listing"/> or <see cref="Files"/>, its passive replies point at a closed port, so
/// every data connection fails.
/// </summary>
internal sealed class LoopbackFtpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<TcpClient> _connections = [];
    private int _controlConnections;
    private int _controlClosedByClient;
    private int _dataClosedByClient;
    private long _extraBytesSent;
    private readonly TaskCompletionSource<bool> _closedSilently = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _silentCloses;

    public LoopbackFtpServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public int Port { get; }

    /// <summary>Gets or sets whether FEAT lists EPSV.</summary>
    public bool AdvertiseEpsv { get; set; } = true;

    /// <summary>Gets or sets whether EPSV is answered with 500 instead of a port.</summary>
    public bool RefuseEpsv { get; set; }

    /// <summary>
    /// Gets or sets whether the first control connection is closed right after TYPE I, and no other
    /// connection is accepted, so FluentFTP must reopen it, and fails, during the next operation.
    /// </summary>
    public bool DropControlConnectionAfterLogin { get; set; }

    /// <summary>
    /// Gets or sets a command after whose reply the server closes the control connection without
    /// a word, once (on the first connection that sends it), as a server that drops an idle or
    /// unwanted connection. It goes on accepting new connections.
    /// </summary>
    public string? CloseControlConnectionSilentlyAfter { get; set; }

    /// <summary>Gets a task that completes when the server has closed a control connection as <see cref="CloseControlConnectionSilentlyAfter"/> asks.</summary>
    public Task ControlConnectionClosedSilently => _closedSilently.Task;

    /// <summary>Gets the port that the passive replies point at when there is no data to serve; nothing listens on it.</summary>
    public int PassivePort { get; } = ClosedPort();

    /// <summary>
    /// Gets or sets the lines LIST sends (DOS format), over a real passive data connection; when
    /// <see langword="null"/>, LIST gets no data connection.
    /// </summary>
    public IReadOnlyList<string>? Listing { get; set; }

    /// <summary>Gets or sets how a listing ends.</summary>
    public ListingEnd ListingEnd { get; set; }

    /// <summary>
    /// Gets or sets a reply that LIST gets in place of a listing, for example
    /// <c>550 Access is denied.</c>; <see langword="null"/> for none.
    /// </summary>
    public string? ListRefusal { get; set; }

    /// <summary>
    /// Gets or sets whether <see cref="ListRefusal"/> comes after the 150 reply and an empty data
    /// connection, in place of the 226, and not in place of the 150.
    /// </summary>
    public bool RefuseListAfterOpening { get; set; }

    /// <summary>
    /// Gets or sets the folder whose listing gets <see cref="ListRefusal"/> (the end of the path
    /// sent with LIST); <see langword="null"/> for every folder.
    /// </summary>
    public string? RefusedFolder { get; set; }

    /// <summary>Gets the files served by SIZE, MDTM and RETR, by full path on the server.</summary>
    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets a number of bytes after which RETR stops sending, keeping both connections
    /// open and silent, as a server that hangs in the middle of a transfer.
    /// </summary>
    public long? StallTransferAfter { get; set; }

    /// <summary>
    /// Gets or sets the address PASV announces, as four comma-separated numbers; the data listener
    /// is on the loopback interface whatever the address.
    /// </summary>
    public string PassiveAddress { get; set; } = "127,0,0,1";

    /// <summary>
    /// Gets or sets how long a stalled transfer waits, after the 150 reply, before it touches the
    /// data connection, so that a client closing it right away does so first.
    /// </summary>
    public TimeSpan DelayBeforeStalledData { get; set; }

    /// <summary>Gets or sets a command the server stops answering (it keeps the connection open).</summary>
    public string? SilentOn { get; set; }

    /// <summary>Gets or sets the whole reply to every <c>SIZE</c>, e.g. <c>213 abc</c>; <see langword="null"/> for the normal one.</summary>
    public string? SizeReply { get; set; }

    /// <summary>Gets or sets the whole reply to <c>PASS</c>, e.g. a 530; <see langword="null"/> for the normal one.</summary>
    public string? PassReply { get; set; }

    /// <summary>Gets or sets the whole reply to every <c>MDTM</c>; <see langword="null"/> for the normal one.</summary>
    public string? MdtmReply { get; set; }

    /// <summary>
    /// Gets or sets a number of bytes RETR sends after the end of the file, more than its SIZE
    /// announces; <see cref="long.MaxValue"/> sends without end, until the client closes the data
    /// connection. When the client closes it first, the reply is 426.
    /// </summary>
    public long? ExtraBytesAfterFile { get; set; }

    /// <summary>Gets the number of bytes sent after the end of a file (see <see cref="ExtraBytesAfterFile"/>).</summary>
    public long ExtraBytesSent => Interlocked.Read(ref _extraBytesSent);

    /// <summary>Gets the number of control connections accepted.</summary>
    public int ControlConnections => Volatile.Read(ref _controlConnections);

    /// <summary>Gets the number of control connections the client closed or reset.</summary>
    public int ControlConnectionsClosedByClient => Volatile.Read(ref _controlClosedByClient);

    /// <summary>Gets the number of stalled transfers whose data connection the client closed or reset.</summary>
    public int DataConnectionsClosedByClient => Volatile.Read(ref _dataClosedByClient);

    /// <summary>Gets the commands received, in order, without arguments.</summary>
    public ConcurrentQueue<string> Commands { get; } = new();

    private bool ServesData => Listing is not null || ListRefusal is not null || !Files.IsEmpty;

    public static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A DOS-format listing line for a file, as IIS FTP sends it.</summary>
    public static string FileLine(string name, long size) => $"08-08-25  12:00PM {size,20} {name}";

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        foreach (var connection in _connections)
        {
            connection.Dispose();
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                _connections.Add(client);
                Interlocked.Increment(ref _controlConnections);
                _ = ServeAsync(client);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            TcpListener? data = null;
            long restart = 0;
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 Loopback FTP").ConfigureAwait(false);
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    var command = line.Split(' ')[0].ToUpperInvariant();
                    var argument = line.Length > command.Length ? line.Substring(command.Length + 1) : string.Empty;
                    Commands.Enqueue(command);
                    if (command == SilentOn)
                    {
                        continue;
                    }

                    if (ServesData && (command == "PASV" || (command == "EPSV" && !RefuseEpsv)))
                    {
                        data?.Stop();
                        data = new TcpListener(IPAddress.Loopback, 0);
                        data.Start();
                        var port = ((IPEndPoint)data.LocalEndpoint).Port;
                        await writer.WriteLineAsync(command == "EPSV"
                            ? $"229 Entering Extended Passive Mode (|||{port}|)"
                            : $"227 Entering Passive Mode ({PassiveAddress},{port >> 8},{port & 0xFF})").ConfigureAwait(false);
                        continue;
                    }

                    if (command == "LIST" && ListRefusal is not null && data is not null
                        && (RefusedFolder is null || argument.EndsWith(RefusedFolder, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (RefuseListAfterOpening)
                        {
                            await writer.WriteLineAsync("150 Opening ASCII mode data connection.").ConfigureAwait(false);
                            using var connection = await data.AcceptTcpClientAsync().ConfigureAwait(false);
                            connection.Close();
                        }

                        data.Stop();
                        data = null;
                        await writer.WriteLineAsync(ListRefusal).ConfigureAwait(false);
                        continue;
                    }

                    if (command == "LIST" && Listing is not null && data is not null)
                    {
                        await writer.WriteLineAsync("150 Opening ASCII mode data connection.").ConfigureAwait(false);
                        await SendListingAsync(data, writer).ConfigureAwait(false);
                        data.Stop();
                        data = null;
                        continue;
                    }

                    if (command == "REST")
                    {
                        restart = long.Parse(argument, System.Globalization.CultureInfo.InvariantCulture);
                        await writer.WriteLineAsync($"350 Restarting at {restart}.").ConfigureAwait(false);
                        continue;
                    }

                    if (command == "RETR" && data is not null && Files.TryGetValue(argument, out var file))
                    {
                        var completed = await SendFileAsync(data, writer, file, restart).ConfigureAwait(false);
                        restart = 0;
                        data.Stop();
                        data = null;
                        if (completed)
                        {
                            await writer.WriteLineAsync("226 Transfer complete.").ConfigureAwait(false);
                        }

                        // A stalled transfer gets no final reply; the server goes on reading commands.
                        continue;
                    }

                    if (command == "TYPE" && DropControlConnectionAfterLogin)
                    {
                        // The reply and the notice go out in one write, after the listener has
                        // stopped: when the client has read the reply, the notice is already there
                        // for it to find before its next command, and the port already refuses.
                        _listener.Stop();
                        await writer.WriteAsync(Reply(command, argument) + "\r\n421 Closing the control connection.\r\n").ConfigureAwait(false);
                        return;
                    }

                    await writer.WriteLineAsync(Reply(command, argument)).ConfigureAwait(false);
                    if (command == "QUIT")
                    {
                        return;
                    }

                    if (command == CloseControlConnectionSilentlyAfter && Interlocked.Exchange(ref _silentCloses, 1) == 0)
                    {
                        client.Close();
                        _closedSilently.TrySetResult(true);
                        return;
                    }
                }

                ClosedByClient(ref _controlClosedByClient);
            }
            catch (IOException)
            {
                ClosedByClient(ref _controlClosedByClient);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                data?.Stop();
            }
        }
    }

    private async Task SendListingAsync(TcpListener data, StreamWriter control)
    {
        using var connection = await data.AcceptTcpClientAsync().ConfigureAwait(false);
        if (ListingEnd == ListingEnd.Stall)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return;
        }

        var bytes = Encoding.ASCII.GetBytes(string.Concat(Listing!.Select(line => line + "\r\n")));
        connection.GetStream().Write(bytes, 0, bytes.Length);
        if (ListingEnd is ListingEnd.ResetWithoutReply or ListingEnd.ResetThenAbort)
        {
            connection.Client.LingerState = new LingerOption(true, 0);
        }

        connection.Close();
        switch (ListingEnd)
        {
            case ListingEnd.Complete:
                await control.WriteLineAsync("226 Transfer complete.").ConfigureAwait(false);
                break;
            case ListingEnd.ResetThenAbort:
                await control.WriteLineAsync("426 Connection closed; transfer aborted.").ConfigureAwait(false);
                break;
        }
    }

    // Sends the file from the offset; false when it stalled, holding the data connection until the
    // client closes it (or the server stops). The data connection is accepted before the 150 reply,
    // as servers do in passive mode (the client connects before sending RETR): a client that
    // closes it right after the reply must find it already accepted, or Windows drops it from the
    // backlog and the server would never notice.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1835", Justification = "Shared with net481, which has no Memory-based overload.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "Any failure of the probe means the client closed or reset the connection.")]
    private async Task<bool> SendFileAsync(TcpListener data, StreamWriter control, byte[] file, long offset)
    {
        using var connection = await data.AcceptTcpClientAsync().ConfigureAwait(false);
        await control.WriteLineAsync("150 Opening BINARY mode data connection.").ConfigureAwait(false);
        var end = StallTransferAfter is { } stall ? Math.Min(file.Length, offset + stall) : file.Length;
        var stream = connection.GetStream();
        if (StallTransferAfter is null && ExtraBytesAfterFile is { } extra)
        {
            return await SendWithExtraBytesAsync(stream, control, file, offset, extra).ConfigureAwait(false);
        }

        if (StallTransferAfter is null)
        {
            stream.Write(file, (int)offset, (int)(end - offset));
            return true;
        }

        // From here on, any failure of the data connection is the client closing or resetting it,
        // whether it happens while writing or while waiting; it must not be taken for the control
        // connection's.
        try
        {
            await Task.Delay(DelayBeforeStalledData, _stop.Token).ConfigureAwait(false);
            if (end > offset)
            {
                stream.Write(file, (int)offset, (int)(end - offset));
            }

            var probe = new byte[1];
            if (await stream.ReadAsync(probe, 0, 1, _stop.Token).ConfigureAwait(false) == 0)
            {
                ClosedByClient(ref _dataClosedByClient);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ClosedByClient(ref _dataClosedByClient);
        }

        return false;
    }

    // The file and then bytes it does not have; false, with a 426, when the client closes the data
    // connection before the server has sent them all.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1835", Justification = "Shared with net481, which has no Memory-based overload.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "Any failure of the write means the client closed or reset the connection.")]
    private async Task<bool> SendWithExtraBytesAsync(NetworkStream stream, StreamWriter control, byte[] file, long offset, long extra)
    {
        try
        {
            await stream.WriteAsync(file, (int)offset, file.Length - (int)offset, _stop.Token).ConfigureAwait(false);
            var junk = Enumerable.Repeat((byte)0xEE, 64 * 1024).ToArray();
            for (var left = extra; left > 0; )
            {
                var count = (int)Math.Min(junk.Length, left);
                await stream.WriteAsync(junk, 0, count, _stop.Token).ConfigureAwait(false);
                Interlocked.Add(ref _extraBytesSent, count);
                left = extra == long.MaxValue ? long.MaxValue : left - count;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            ClosedByClient(ref _dataClosedByClient);
            await control.WriteLineAsync("426 Connection closed; transfer aborted.").ConfigureAwait(false);
            return false;
        }
    }

    private void ClosedByClient(ref int counter)
    {
        if (!_stop.IsCancellationRequested)
        {
            Interlocked.Increment(ref counter);
        }
    }

    private string Reply(string command, string argument) => command switch
    {
        "USER" => "331 Password required.",
        "PASS" => PassReply ?? "230 Logged in.",
        "FEAT" => AdvertiseEpsv ? "211-Features:\r\n EPSV\r\n SIZE\r\n MDTM\r\n REST STREAM\r\n211 End" : "211-Features:\r\n SIZE\r\n MDTM\r\n REST STREAM\r\n211 End",
        "SYST" => "215 Windows_NT",
        "PWD" => "257 \"/\" is the current directory.",
        "TYPE" => "200 Type set.",
        "OPTS" or "NOOP" => "200 OK.",
        "SIZE" when SizeReply is not null => SizeReply,
        "MDTM" when MdtmReply is not null => MdtmReply,
        "SIZE" when Files.IsEmpty => "213 10",
        "SIZE" => Files.TryGetValue(argument, out var file) ? $"213 {file.Length}" : "550 The system cannot find the file specified.",
        "MDTM" => Files.IsEmpty || Files.ContainsKey(argument) ? "213 20250808120000" : "550 The system cannot find the file specified.",
        "EPSV" => RefuseEpsv ? "500 EPSV not understood." : $"229 Entering Extended Passive Mode (|||{PassivePort}|)",
        "PASV" => $"227 Entering Passive Mode (127,0,0,1,{PassivePort >> 8},{PassivePort & 0xFF})",
        "QUIT" => "221 Bye.",
        _ => "502 Command not implemented.",
    };
}
