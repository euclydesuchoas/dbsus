using System.Net.Sockets;
using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using FluentFTP.Exceptions;
using Xunit;

namespace DbSus.Client.Tests;

// These tests need the very exception types that the runtime reserves (CA2201), because those are
// what FluentFTP threw: the NullReferenceException seen in CI came from inside FluentFTP 55.0.0.
#pragma warning disable CA2201

// The boundary around the calls into FluentFTP: an exception FluentFTP is not meant to throw
// becomes a transient transport failure, retried on another connection; everything else, and
// every exception from outside that boundary, is left exactly as it is.
public sealed class FtpLibraryBoundaryTests : IDisposable
{
    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FtpStep Listing => FtpStep.Listing("/dissemin/publicos/X", "ftp.example", 21);

    public static TheoryData<Exception> UnexpectedFromTheLibrary => new()
    {
        new NullReferenceException("Object reference not set to an instance of an object."),
        new InvalidOperationException("No connection to the server exists."),
        new ObjectDisposedException("FtpSocketStream"),
        new IndexOutOfRangeException("index"),
        new FormatException("reply"),
        new NotSupportedException("stream"),
    };

    public static TheoryData<Exception> NeverTranslated => new()
    {
        new OperationCanceledException("canceled"),
        new TaskCanceledException("canceled"),
        new ArgumentException("path"),
        new ArgumentNullException("path"),
        new ArgumentOutOfRangeException("offset"),
        new FtpException("ftp"),
        new IOException("io"),
        new SocketException((int)SocketError.ConnectionReset),
        new TimeoutException("timeout"),
        new OutOfMemoryException("memory"),
    };

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [MemberData(nameof(UnexpectedFromTheLibrary))]
    public async Task InLibrary_UnexpectedException_IsMarkedAsALibraryFailure_KeepingTheOriginal(Exception original)
    {
        var asynchronous = await Assert.ThrowsAsync<FtpLibraryFailureException>(() => FtpSession.InLibraryAsync<int>(() => throw original));
        var synchronous = Assert.Throws<FtpLibraryFailureException>(() => FtpSession.InLibrary<int>(() => throw original));

        Assert.Same(original, asynchronous.InnerException);
        Assert.Same(original, synchronous.InnerException);
    }

    [Theory]
    [MemberData(nameof(NeverTranslated))]
    public async Task InLibrary_CancellationArgumentsAndKnownFailures_PassUnchanged(Exception original)
    {
        var asynchronous = await Record.ExceptionAsync(() => FtpSession.InLibraryAsync<int>(() => throw original));
        var synchronous = Record.Exception(() => FtpSession.InLibrary<int>(() => throw original));

        Assert.Same(original, asynchronous);
        Assert.Same(original, synchronous);
    }

    [Fact]
    public async Task Translate_LibraryFailure_IsATransientFailureThatNamesTheStepAndTheException()
    {
        var original = new NullReferenceException("Object reference not set to an instance of an object.");

        var transient = await Assert.ThrowsAsync<TransientTransportException>(() =>
            FtpConnection.Translate(Listing, null, () => FtpSession.InLibraryAsync<int>(() => throw original)));

        Assert.True(transient.IsLibraryFailure);
        Assert.Same(original, transient.InnerException);
        Assert.Null(transient.ReplyCode);
        Assert.Equal(
            "FTP transport failure while listing /dissemin/publicos/X on ftp.example:21: the FTP library failed with an unexpected NullReferenceException "
            + "(Object reference not set to an instance of an object.) and the connection was discarded.",
            transient.Message);
    }

    [Fact]
    public async Task Translate_SameExceptionFromOutsideTheBoundary_IsNotTranslated()
    {
        // Thrown by this library's own code around the calls, not by FluentFTP: it is a bug to see, not to retry.
        var original = new NullReferenceException("ours");
        var invalid = new InvalidOperationException("ours");

        Assert.Same(original, await Record.ExceptionAsync(() => FtpConnection.Translate<int>(Listing, null, () => throw original)));
        Assert.Same(invalid, await Record.ExceptionAsync(() => FtpConnection.Translate<int>(Listing, null, () => throw invalid)));
    }

    [Fact]
    public async Task Translate_KnownFailuresInsideTheBoundary_AreNotMarkedAsLibraryFailures()
    {
        var transient = await Assert.ThrowsAsync<TransientTransportException>(() =>
            FtpConnection.Translate(Listing, null, () => FtpSession.InLibraryAsync<int>(() => throw new IOException("reset"))));

        Assert.False(transient.IsLibraryFailure);
        Assert.IsType<IOException>(transient.InnerException);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            FtpConnection.Translate(Listing, null, () => FtpSession.InLibraryAsync<int>(() => throw new OperationCanceledException())));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            FtpConnection.Translate(Listing, null, () => FtpSession.InLibraryAsync<int>(() => throw new ArgumentException("path"))));
    }

    [Fact]
    public async Task Retries_LibraryFailureEveryTime_EndWithRetriesExhausted_WhoseInnerExceptionIsTheOriginal()
    {
        var original = new NullReferenceException("Object reference not set to an instance of an object.");
        var retried = new List<DatasusRetryInfo>();
        var policy = new RetryPolicy(2, TimeSpan.Zero, (_, _) => Task.CompletedTask, retried.Add);
        var calls = 0;

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => policy.RunAsync(
            "Listing /X",
            "/X",
            _ =>
            {
                calls++;
                return FtpConnection.Translate(Listing, null, () => FtpSession.InLibraryAsync<int>(() => throw original));
            },
            Ct));

        Assert.Equal(3, calls);
        Assert.Equal(3, exception.Attempts);
        Assert.Equal("/X", exception.RemotePath);
        Assert.Null(exception.FtpReplyCode);
        Assert.Same(original, exception.InnerException);
        Assert.StartsWith("Listing /X failed after 3 attempts: FTP transport failure while listing /dissemin/publicos/X on ftp.example:21: the FTP library failed with an unexpected NullReferenceException", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, retried.Count);
        Assert.All(retried, info => Assert.Same(original, info.Exception.InnerException));
    }

    [Fact]
    public async Task Client_ExceptionOfItsOwnConnectionCode_ReachesTheCallerUntouched_AndIsNotRetried()
    {
        // The fake connection stands for this library's own code: nothing here is a call into FluentFTP.
        var original = new NullReferenceException("ours");
        _fixture.Server.Add("/X/A.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.FailNextOperations(1, () => original);
        var client = _fixture.Client();

        var exception = await Record.ExceptionAsync(() => client.ListAsync("/X", Ct));

        Assert.Same(original, exception);
        Assert.Equal(1, _fixture.Server.ListCalls);
        Assert.Empty(_fixture.Delays);
    }
}
