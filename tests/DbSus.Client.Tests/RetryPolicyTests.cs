using DbSus.Client.Transport;
using Xunit;

namespace DbSus.Client.Tests;

public class RetryPolicyTests
{
    [Fact]
    public async Task RunAsync_TransientFailures_AreRetriedUntilSuccess()
    {
        var delays = new List<TimeSpan>();
        var policy = new RetryPolicy(4, TimeSpan.FromSeconds(1), (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        var calls = 0;

        var result = await policy.RunAsync("op", "/x", _ => ++calls < 3 ? throw new TransientTransportException("drop") : Task.FromResult(42), CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(3, calls);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task RunAsync_RetriesExhausted_ThrowsIOExceptionWithTheLastFailure()
    {
        var policy = new RetryPolicy(2, TimeSpan.Zero, (_, _) => Task.CompletedTask);
        var calls = 0;

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() =>
            policy.RunAsync<int>("Downloading x", "/x", _ => { calls++; throw new TransientTransportException("drop", "421"); }, CancellationToken.None));

        Assert.Equal(3, calls);
        Assert.Equal(3, exception.Attempts);
        Assert.Equal("/x", exception.RemotePath);
        Assert.Equal("421", exception.FtpReplyCode);
        Assert.IsType<TransientTransportException>(exception.InnerException);
        Assert.StartsWith("Downloading x failed after 3 attempts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NoRetries_ReportsASingleAttempt()
    {
        var policy = new RetryPolicy(0, TimeSpan.Zero, (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() =>
            policy.RunAsync<int>("Downloading x", "/x", _ => throw new TransientTransportException("drop"), CancellationToken.None));

        Assert.StartsWith("Downloading x failed after 1 attempt:", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, exception.Attempts);
        Assert.Null(exception.FtpReplyCode);
    }

    [Fact]
    public async Task RunAsync_Refusal_IsNotRetriedAndCarriesPathAndAttempts()
    {
        var policy = new RetryPolicy(4, TimeSpan.Zero, (_, _) => Task.CompletedTask);
        var calls = 0;

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(() =>
            policy.RunAsync<int>("op", "/x", _ => { calls++; throw new DatasusRequestRefusedException("530 Not logged in", "530", null); }, CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.Equal(1, exception.Attempts);
        Assert.Equal("/x", exception.RemotePath);
        Assert.Equal("530", exception.FtpReplyCode);
    }

    [Fact]
    public async Task RunAsync_OnRetry_ReportsEachRetryBeforeItsWait()
    {
        var reports = new List<DatasusRetryInfo>();
        var waits = new List<TimeSpan>();
        var policy = new RetryPolicy(4, TimeSpan.FromSeconds(1), (delay, _) => { waits.Add(delay); return Task.CompletedTask; }, reports.Add);
        var calls = 0;

        await policy.RunAsync("Downloading x", "/x", _ => ++calls switch
        {
            1 => throw new TransientTransportException("drop"),
            2 => throw new TransientTransportException("421 Too many connections", "421"),
            _ => Task.FromResult(1),
        }, CancellationToken.None);

        Assert.Equal([1, 2], reports.Select(report => report.Attempt));
        Assert.Equal([null, "421"], reports.Select(report => report.FtpReplyCode));
        Assert.All(reports, report => Assert.Equal("Downloading x", report.Operation));
        Assert.All(reports, report => Assert.Equal("/x", report.RemotePath));
        Assert.All(reports, report => Assert.IsType<TransientTransportException>(report.Exception));
        Assert.Equal(waits, reports.Select(report => report.Delay));
    }

    [Fact]
    public async Task RunAsync_OnRetryThatThrows_IsIgnored()
    {
        var policy = new RetryPolicy(4, TimeSpan.Zero, (_, _) => Task.CompletedTask, _ => throw new InvalidOperationException("callback failure"));
        var calls = 0;

        var result = await policy.RunAsync("op", "/x", _ => ++calls < 2 ? throw new TransientTransportException("drop") : Task.FromResult(7), CancellationToken.None);

        Assert.Equal(7, result);
    }

    [Fact]
    public async Task RunAsync_MissingFile_IsNotRetried()
    {
        var policy = new RetryPolicy(4, TimeSpan.Zero, (_, _) => Task.CompletedTask);
        var calls = 0;

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            policy.RunAsync<int>("op", "/x", _ => { calls++; throw new FileNotFoundException("550"); }, CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_Canceled_StopsBeforeTheNextAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var policy = new RetryPolicy(4, TimeSpan.Zero, (_, _) => { cancellation.Cancel(); return Task.CompletedTask; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            policy.RunAsync<int>("op", "/x", _ => throw new TransientTransportException("drop"), cancellation.Token));
    }

    [Fact]
    public void Backoff_GrowsExponentiallyWithJitterAndIsCapped()
    {
        var policy = new RetryPolicy(10, TimeSpan.FromSeconds(1), (_, _) => Task.CompletedTask);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var delay = policy.Backoff(attempt).TotalSeconds;
            Assert.InRange(delay, Math.Pow(2, attempt) * 0.5, Math.Pow(2, attempt));
        }

        Assert.Equal(TimeSpan.FromSeconds(30), policy.Backoff(10));
    }
}
