using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// A listing that the server refuses with a permanent reply (5xx) is a refusal, reported once with
// the reply, and never a transport failure to retry. FluentFTP 55 takes a 550 to LIST for an empty
// folder and returns no entries, so the client has to look at the reply itself. FTP uses the same
// 550 for a folder that does not exist and for one that may not be read, so a refused listing is
// never reported as a missing file either.
public sealed class ListingRefusedTests : IDisposable
{
    private const string Denied = "550 Access is denied.";
    private const string Missing = "550 The system cannot find the file specified.";
    private const string Root = "/dissemin/publicos";
    private const string SimFinal = "/SIM/CID10/DORES";
    private const string SimPreliminary = "/SIM/PRELIM/DORES";
    private readonly LoopbackFtpServer _server = new() { AdvertiseEpsv = false };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _server.Dispose();

    // The reply in place of the 150, and after the 150 and an empty data connection; with no
    // retries and with the default number of them.
    [Theory(Timeout = 30_000)]
    [InlineData(Denied, false, false)]
    [InlineData(Denied, false, true)]
    [InlineData(Denied, true, false)]
    [InlineData(Denied, true, true)]
    [InlineData(Missing, false, false)]
    [InlineData(Missing, false, true)]
    [InlineData(Missing, true, false)]
    [InlineData(Missing, true, true)]
    public async Task ListAsync_ListingRefusedWith550_IsARefusal_AskedOnce(string reply, bool afterOpening, bool defaultRetries)
    {
        _server.ListRefusal = reply;
        _server.RefuseListAfterOpening = afterOpening;
        using var client = Client(defaultRetries);

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(() => client.ListAsync("/X", TestContext.Current.CancellationToken));

        Assert.Equal("550", exception.FtpReplyCode);
        Assert.Equal(1, exception.Attempts);
        Assert.Equal("/X", exception.RemotePath);
        Assert.Contains($"the server refused the listing ('{reply}')", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, _server.Commands.Count(command => command == "LIST"));
        Assert.Equal(1, _server.ControlConnections);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAsync_ListingRefusedWithAnother5xx_IsARefusalWithItsCode(bool afterOpening)
    {
        _server.ListRefusal = "501 Syntax error in parameters.";
        _server.RefuseListAfterOpening = afterOpening;
        using var client = Client(defaultRetries: true);

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(() => client.ListAsync("/X", TestContext.Current.CancellationToken));

        Assert.Equal("501", exception.FtpReplyCode);
        Assert.Contains("the server refused the listing ('501 Syntax error in parameters.')", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, exception.Attempts);
        Assert.Equal(1, _server.Commands.Count(command => command == "LIST"));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAsync_ListingEndedWith4xx_IsStillRetried(bool afterOpening)
    {
        _server.ListRefusal = "451 Requested action aborted.";
        _server.RefuseListAfterOpening = afterOpening;
        using var client = Client(defaultRetries: true);

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.ListAsync("/X", TestContext.Current.CancellationToken));

        Assert.Equal("451", exception.FtpReplyCode);
        Assert.Equal(5, exception.Attempts);
        Assert.Equal(5, _server.Commands.Count(command => command == "LIST"));
    }

    [Fact(Timeout = 30_000)]
    public async Task ListAsync_RefusedListing_IsNotCached_AndTheClientGoesOn()
    {
        _server.ListRefusal = Denied;
        using var client = Client(defaultRetries: false);
        await Assert.ThrowsAsync<DatasusRequestRefusedException>(() => client.ListAsync("/X", TestContext.Current.CancellationToken));

        _server.ListRefusal = null;
        _server.Listing = [LoopbackFtpServer.FileLine("PAMG2407.dbc", 10)];
        var entries = await client.ListAsync("/X", TestContext.Current.CancellationToken);

        Assert.Equal(["PAMG2407.dbc"], entries.Select(entry => entry.Name));
        Assert.Equal(2, _server.Commands.Count(command => command == "LIST"));
    }

    // Whatever lists a folder first lets the refusal through: nothing else is asked, and the
    // refusal is not turned into "no such table".
    [Fact(Timeout = 30_000)]
    public async Task FindPartsAsync_FolderListingRefused_ThrowsTheRefusal_AndAsksNothingElse()
    {
        _server.ListRefusal = Missing;
        using var client = Client(defaultRetries: true);

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(() => client.FindPartsAsync("/X/PAMG2407.dbc", TestContext.Current.CancellationToken));

        Assert.Equal("550", exception.FtpReplyCode);
        Assert.Equal("/X", exception.RemotePath);
        AssertOneListingAndNothingElse();
    }

    [Fact(Timeout = 30_000)]
    public async Task OpenReaderAsync_Path_FolderListingRefused_ThrowsTheRefusal_AndAsksNothingElse()
    {
        _server.ListRefusal = Denied;
        using var client = Client(defaultRetries: true);

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(() => client.OpenReaderAsync("/X/PAMG2407.dbc", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("/X", exception.RemotePath);
        AssertOneListingAndNothingElse();
    }

    [Fact(Timeout = 30_000)]
    public async Task FindTableAsync_FinalFolderRefused_ThrowsTheRefusal_NotAMissingTable()
    {
        _server.ListRefusal = Missing;
        using var client = Client(defaultRetries: true);

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(
            () => client.FindTableAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, DatasusPeriod.OfMonth(2024, 7), TestContext.Current.CancellationToken));

        Assert.Equal("/SIASUS/200801_/Dados", exception.RemotePath);
        AssertOneListingAndNothingElse();
    }

    [Fact(Timeout = 30_000)]
    public async Task ListTablesAsync_FinalFolderRefused_ThrowsTheRefusal()
    {
        _server.ListRefusal = Denied;
        using var client = Client(defaultRetries: true);

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(
            () => client.ListTablesAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2024), DatasusPeriod.OfYear(2025), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(SimFinal, exception.RemotePath);
        AssertOneListingAndNothingElse();
    }

    // The folder of preliminary data is optional: a source may stop having one. A 550 to its
    // listing, and only a 550, is taken as "there is no such folder". FTP uses the same reply for
    // a folder that may not be read, so the message says that the folder could not be listed.
    [Theory(Timeout = 30_000)]
    [InlineData(Missing, false)]
    [InlineData(Missing, true)]
    [InlineData(Denied, false)]
    [InlineData(Denied, true)]
    public async Task FindTableAsync_PreliminaryFolderRefusedWith550_IsAMissingTable_AndTheMessageSaysTheFolderWasNotListed(string reply, bool afterOpening)
    {
        ServeTheFinalFolderWithoutTheTable(reply, afterOpening);
        using var client = Client(defaultRetries: true);

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => client.FindTableAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2025), TestContext.Current.CancellationToken));

        AssertSaysThePreliminaryFolderWasNotListed(exception, reply);
        Assert.Equal(2, _server.Commands.Count(command => command == "LIST"));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(Missing)]
    [InlineData(Denied)]
    public async Task OpenReaderAsync_ByCriteria_PreliminaryFolderRefusedWith550_IsAMissingTable_AndTheMessageSaysTheFolderWasNotListed(string reply)
    {
        ServeTheFinalFolderWithoutTheTable(reply);
        using var client = Client(defaultRetries: true);

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => client.OpenReaderAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2025), cancellationToken: TestContext.Current.CancellationToken));

        AssertSaysThePreliminaryFolderWasNotListed(exception, reply);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(Missing, false)]
    [InlineData(Missing, true)]
    [InlineData(Denied, false)]
    [InlineData(Denied, true)]
    public async Task ListTablesAsync_PreliminaryFolderRefusedWith550_ReturnsTheTablesOfTheFinalFolder(string reply, bool afterOpening)
    {
        ServeTheFinalFolderWithoutTheTable(reply, afterOpening);
        using var client = Client(defaultRetries: true);

        var tables = await client.ListTablesAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2024), DatasusPeriod.OfYear(2025), cancellationToken: TestContext.Current.CancellationToken);

        var table = Assert.Single(tables);
        Assert.Equal(SimFinal + "/DOAC2024.dbc", table.Path);
        Assert.False(table.IsPreliminary);
        Assert.Equal(2, _server.Commands.Count(command => command == "LIST"));
    }

    // The refused folder is asked again the next time: a refusal is not kept as an empty listing.
    [Fact(Timeout = 30_000)]
    public async Task ListTablesAsync_PreliminaryFolderRefusedWith550_IsAskedAgainTheNextTime()
    {
        ServeTheFinalFolderWithoutTheTable(Missing);
        using var client = Client(defaultRetries: true);
        Assert.Single(await client.ListTablesAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2024), DatasusPeriod.OfYear(2025), cancellationToken: TestContext.Current.CancellationToken));

        _server.ListRefusal = null;
        var tables = await client.ListTablesAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2024), DatasusPeriod.OfYear(2025), cancellationToken: TestContext.Current.CancellationToken);

        // The final folder comes from the cache; the preliminary one, now listed, has the same file.
        Assert.Single(tables);
        Assert.Equal(3, _server.Commands.Count(command => command == "LIST"));
    }

    // Found in the folder of final data: the preliminary one is not asked at all.
    [Fact(Timeout = 30_000)]
    public async Task FindTableAsync_TableInTheFinalFolder_DoesNotListThePreliminaryOne()
    {
        ServeTheFinalFolderWithoutTheTable(Denied);
        using var client = Client(defaultRetries: true);

        var table = await client.FindTableAsync(DatasusCatalog.Sim.DO, DatasusUf.AC, DatasusPeriod.OfYear(2024), TestContext.Current.CancellationToken);

        Assert.False(table.IsPreliminary);
        Assert.Equal(1, _server.Commands.Count(command => command == "LIST"));
    }

    // Any other permanent reply to the listing of the preliminary folder is still a refusal.
    [Theory(Timeout = 30_000)]
    [InlineData("530 Not logged in.", "530")]
    [InlineData("501 Syntax error in parameters.", "501")]
    public async Task FindTableAsync_PreliminaryFolderRefusedWithAnother5xx_ThrowsTheRefusal(string reply, string code)
    {
        ServeTheFinalFolderWithoutTheTable(reply);
        using var client = Client(defaultRetries: true);

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(
            () => client.FindTableAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2025), TestContext.Current.CancellationToken));

        Assert.Equal(code, exception.FtpReplyCode);
        Assert.Equal(SimPreliminary, exception.RemotePath);
        Assert.Equal(2, _server.Commands.Count(command => command == "LIST"));
    }

    [Fact(Timeout = 30_000)]
    public async Task OpenReaderAsync_ByCriteria_PreliminaryFolderRefusedWith530_ThrowsTheRefusal()
    {
        ServeTheFinalFolderWithoutTheTable("530 Not logged in.");
        using var client = Client(defaultRetries: true);

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(
            () => client.OpenReaderAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2025), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("530", exception.FtpReplyCode);
        Assert.Equal(SimPreliminary, exception.RemotePath);
    }

    [Fact(Timeout = 30_000)]
    public async Task ListTablesAsync_PreliminaryFolderRefusedWith530_ThrowsTheRefusal_AndNoPartialResult()
    {
        ServeTheFinalFolderWithoutTheTable("530 Not logged in.");
        using var client = Client(defaultRetries: true);

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(
            () => client.ListTablesAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2024), DatasusPeriod.OfYear(2025), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("530", exception.FtpReplyCode);
        Assert.Equal(SimPreliminary, exception.RemotePath);
        Assert.Equal(2, _server.Commands.Count(command => command == "LIST"));
    }

    // The folder of final data is not optional: a 550 to its listing is a refusal, also for a
    // source that has a folder of preliminary data.
    [Theory(Timeout = 30_000)]
    [InlineData(Missing)]
    [InlineData(Denied)]
    public async Task FindTableAsync_FinalFolderOfASourceWithPreliminaryData_RefusedWith550_ThrowsTheRefusal(string reply)
    {
        _server.Listing = [LoopbackFtpServer.FileLine("DORR2025.dbc", 10)];
        _server.ListRefusal = reply;
        _server.RefusedFolder = SimFinal;
        using var client = Client(defaultRetries: true);

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(
            () => client.FindTableAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2025), TestContext.Current.CancellationToken));

        Assert.Equal("550", exception.FtpReplyCode);
        Assert.Equal(SimFinal, exception.RemotePath);
        AssertOneListingAndNothingElse();
    }

    [Fact(Timeout = 30_000)]
    public async Task ListTablesAsync_OnlyTheFinalFolderRefusedWith550_ThrowsTheRefusal()
    {
        _server.Listing = [LoopbackFtpServer.FileLine("DORR2025.dbc", 10)];
        _server.ListRefusal = Missing;
        _server.RefusedFolder = SimFinal;
        using var client = Client(defaultRetries: true);

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(
            () => client.ListTablesAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2024), DatasusPeriod.OfYear(2025), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(SimFinal, exception.RemotePath);
        AssertOneListingAndNothingElse();
    }

    private static void AssertSaysThePreliminaryFolderWasNotListed(FileNotFoundException exception, string reply)
    {
        Assert.Equal(SimFinal + "/DORR2025.dbc", exception.FileName);
        Assert.Contains("The server has no final SIM-DO table of RR for 2025: " + SimFinal + "/DORR2025.dbc does not exist, whole or in parts.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("The folder of preliminary data, " + SimPreliminary + ", was not searched, because it could not be listed", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"the server refused the listing ('{reply}')", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FTP uses 550 both for a folder that does not exist and for one that may not be read.", exception.Message, StringComparison.Ordinal);
        var refusal = Assert.IsType<DatasusRequestRefusedException>(exception.InnerException);
        Assert.Equal("550", refusal.FtpReplyCode);
        Assert.Equal(SimPreliminary, refusal.RemotePath);
    }

    // The final folder lists one file of another state and year, and the server answers 550 for
    // any other file; only the listing of the preliminary folder gets the reply.
    private void ServeTheFinalFolderWithoutTheTable(string preliminaryReply, bool afterOpening = false)
    {
        _server.Listing = [LoopbackFtpServer.FileLine("DOAC2024.dbc", 10)];
        _server.Files[Root + SimFinal + "/DOAC2024.dbc"] = new byte[10];
        _server.ListRefusal = preliminaryReply;
        _server.RefuseListAfterOpening = afterOpening;
        _server.RefusedFolder = SimPreliminary;
    }

    private void AssertOneListingAndNothingElse()
    {
        Assert.Equal(1, _server.Commands.Count(command => command == "LIST"));
        Assert.DoesNotContain("SIZE", _server.Commands);
        Assert.DoesNotContain("MDTM", _server.Commands);
        Assert.DoesNotContain("RETR", _server.Commands);
        Assert.Equal(1, _server.ControlConnections);
    }

    // With the default number of retries (4), only the waits are shortened.
    private DatasusClient Client(bool defaultRetries)
    {
        var options = new DatasusClientOptions
        {
            Host = "127.0.0.1",
            Port = _server.Port,
            InitialRetryDelay = TimeSpan.FromMilliseconds(10),
            ConnectTimeout = TimeSpan.FromSeconds(1),
            ReadTimeout = TimeSpan.FromSeconds(1),
        };
        if (!defaultRetries)
        {
            options.MaxRetries = 0;
        }

        return options.MaxRetries == (defaultRetries ? 4 : 0) ? new DatasusClient(options) : throw new InvalidOperationException("The default number of retries is no longer 4.");
    }
}
