using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DbSus.Client.Tests.Fakes;

/// <summary>
/// A minimal FTP server on the loopback interface, enough for FluentFTP to log in, so the real
/// transport can be tested without any network: its passive replies point at a closed port, so
/// every data connection fails.
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

    /// <summary>Gets the port that the passive replies point at; nothing listens on it.</summary>
    public int PassivePort { get; } = ClosedPort();

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
        }
    }

    private string Reply(string command) => command switch
    {
        "USER" => "331 Password required.",
        "PASS" => "230 Logged in.",
        "FEAT" => AdvertiseEpsv ? "211-Features:\r\n EPSV\r\n SIZE\r\n MDTM\r\n211 End" : "211-Features:\r\n SIZE\r\n MDTM\r\n211 End",
        "SYST" => "215 Windows_NT",
        "PWD" => "257 \"/\" is the current directory.",
        "TYPE" => "200 Type set to I.",
        "OPTS" or "NOOP" => "200 OK.",
        "SIZE" => "213 10",
        "MDTM" => "213 20250808120000",
        "EPSV" => RefuseEpsv ? "500 EPSV not understood." : $"229 Entering Extended Passive Mode (|||{PassivePort}|)",
        "PASV" => $"227 Entering Passive Mode (127,0,0,1,{PassivePort >> 8},{PassivePort & 0xFF})",
        "QUIT" => "221 Bye.",
        _ => "502 Command not implemented.",
    };
}
