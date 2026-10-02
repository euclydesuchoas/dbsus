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

        var result = await policy.RunAsync("op", _ => ++calls < 3 ? throw new TransientTransportException("drop") : Task.FromResult(42), CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(3, calls);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task RunAsync_RetriesExhausted_ThrowsIOExceptionWithTheLastFailure()
    {
        var policy = new RetryPolicy(2, TimeSpan.Zero, (_, _) => Task.CompletedTask);
        var calls = 0;

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            policy.RunAsync<int>("Downloading x", _ => { calls++; throw new TransientTransportException("drop"); }, CancellationToken.None));

        Assert.Equal(3, calls);
        Assert.IsType<TransientTransportException>(exception.InnerException);
        Assert.StartsWith("Downloading x failed after 3 attempts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NoRetries_ReportsASingleAttempt()
    {
        var policy = new RetryPolicy(0, TimeSpan.Zero, (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            policy.RunAsync<int>("Downloading x", _ => throw new TransientTransportException("drop"), CancellationToken.None));

        Assert.StartsWith("Downloading x failed after 1 attempt:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MissingFile_IsNotRetried()
    {
        var policy = new RetryPolicy(4, TimeSpan.Zero, (_, _) => Task.CompletedTask);
        var calls = 0;

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            policy.RunAsync<int>("op", _ => { calls++; throw new FileNotFoundException("550"); }, CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_Canceled_StopsBeforeTheNextAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var policy = new RetryPolicy(4, TimeSpan.Zero, (_, _) => { cancellation.Cancel(); return Task.CompletedTask; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            policy.RunAsync<int>("op", _ => throw new TransientTransportException("drop"), cancellation.Token));
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
