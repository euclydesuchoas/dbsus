namespace DbSus.Client.Tests.Fakes;

/// <summary>
/// The times a test gives the client against a server on the loopback interface. A runner that is
/// slow or busy must never reach them, so a test fails only for what it tests:
/// <list type="bullet">
/// <item><description>a connect or read timeout of 1 second or less only in a test of that timeout;</description></item>
/// <item><description>two times that race each other (a server's delay and the client's limit, say) at least <see cref="Margin"/> times apart, checked with <see cref="Apart"/>.</description></item>
/// </list>
/// </summary>
internal static class TestTimes
{
    /// <summary>The least ratio between two times that race each other in a test.</summary>
    public const int Margin = 5;

    /// <summary>A connect timeout that a slow runner does not reach.</summary>
    public static readonly TimeSpan Connect = TimeSpan.FromSeconds(10);

    /// <summary>A read timeout that a slow runner does not reach.</summary>
    public static readonly TimeSpan Read = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gives back <paramref name="longer"/> after checking that it is at least <see cref="Margin"/>
    /// times <paramref name="shorter"/>, so that a test whose two times are too close fails at once,
    /// for that reason, rather than now and then on a busy runner.
    /// </summary>
    public static TimeSpan Apart(TimeSpan shorter, TimeSpan longer) =>
        longer.Ticks >= shorter.Ticks * Margin
            ? longer
            : throw new ArgumentException($"{longer} is less than {Margin} times {shorter}: a slow runner may swap them.", nameof(longer));

    /// <summary>Options for a client of the loopback server at <paramref name="port"/>, with the times above.</summary>
    public static DatasusClientOptions Loopback(int port) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        ConnectTimeout = Connect,
        ReadTimeout = Read,
    };
}
