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
}

/// <summary>
/// A minimal FTP server on the loopback interface, enough for FluentFTP to log in, so the real
/// transport can be tested without any network. Without a <see cref="Listing"/>, its passive
/// replies point at a closed port, so every data connection fails.
/// </summary>
internal sealed class LoopbackFtpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

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

    /// <summary>Gets the port that the passive replies point at when there is no listing; nothing listens on it.</summary>
    public int PassivePort { get; } = ClosedPort();

    /// <summary>
    /// Gets or sets the lines LIST sends (DOS format), over a real passive data connection; when
    /// <see langword="null"/>, the data connection cannot be opened.
    /// </summary>
    public IReadOnlyList<string>? Listing { get; set; }

    /// <summary>Gets or sets how a listing ends.</summary>
    public ListingEnd ListingEnd { get; set; }

    /// <summary>Gets the commands received, in order, without arguments.</summary>
    public ConcurrentQueue<string> Commands { get; } = new();

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
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = ServeAsync(client);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            TcpListener? data = null;
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 Loopback FTP").ConfigureAwait(false);
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    var command = line.Split(' ')[0].ToUpperInvariant();
                    Commands.Enqueue(command);
                    if (Listing is not null && (command == "PASV" || (command == "EPSV" && !RefuseEpsv)))
                    {
                        data?.Stop();
                        data = new TcpListener(IPAddress.Loopback, 0);
                        data.Start();
                        var port = ((IPEndPoint)data.LocalEndpoint).Port;
                        await writer.WriteLineAsync(command == "EPSV"
                            ? $"229 Entering Extended Passive Mode (|||{port}|)"
                            : $"227 Entering Passive Mode (127,0,0,1,{port >> 8},{port & 0xFF})").ConfigureAwait(false);
                        continue;
                    }

                    if (command == "LIST" && data is not null)
                    {
                        await writer.WriteLineAsync("150 Opening ASCII mode data connection.").ConfigureAwait(false);
                        await SendListingAsync(data, writer).ConfigureAwait(false);
                        data.Stop();
                        data = null;
                        continue;
                    }

                    await writer.WriteLineAsync(Reply(command)).ConfigureAwait(false);
                    if (command == "QUIT")
                    {
                        return;
                    }

                    if (command == "TYPE" && DropControlConnectionAfterLogin)
                    {
                        _listener.Stop();
                        await writer.WriteLineAsync("421 Closing the control connection.").ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
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

    private string Reply(string command) => command switch
    {
        "USER" => "331 Password required.",
        "PASS" => "230 Logged in.",
        "FEAT" => AdvertiseEpsv ? "211-Features:\r\n EPSV\r\n SIZE\r\n MDTM\r\n211 End" : "211-Features:\r\n SIZE\r\n MDTM\r\n211 End",
        "SYST" => "215 Windows_NT",
        "PWD" => "257 \"/\" is the current directory.",
        "TYPE" => "200 Type set.",
        "OPTS" or "NOOP" => "200 OK.",
        "SIZE" => "213 10",
        "MDTM" => "213 20250808120000",
        "EPSV" => RefuseEpsv ? "500 EPSV not understood." : $"229 Entering Extended Passive Mode (|||{PassivePort}|)",
        "PASV" => $"227 Entering Passive Mode (127,0,0,1,{PassivePort >> 8},{PassivePort & 0xFF})",
        "QUIT" => "221 Bye.",
        _ => "502 Command not implemented.",
    };
}
