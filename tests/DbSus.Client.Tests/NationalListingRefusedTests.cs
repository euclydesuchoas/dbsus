using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// The table for the whole country when the server refuses a listing, against a real FTP dialogue
// on the loopback: the folder of final data is not optional, and the folder of preliminary data
// is, exactly as for the table of a state (see ListingRefusedTests).
public sealed class NationalListingRefusedTests : IDisposable
{
    private const string Denied = "550 Access is denied.";
    private const string Missing = "550 The system cannot find the file specified.";
    private const string Root = "/dissemin/publicos";
    private const string SimFinal = "/SIM/CID10/DORES";
    private const string SimPreliminary = "/SIM/PRELIM/DORES";
    private readonly LoopbackFtpServer _server = new() { AdvertiseEpsv = false };

    public void Dispose() => _server.Dispose();

    [Theory(Timeout = 30_000)]
    [InlineData(Missing)]
    [InlineData(Denied)]
    public async Task FindNationalTableAsync_FinalFolderRefused_ThrowsTheRefusal_NotAMissingTable(string reply)
    {
        _server.ListRefusal = reply;
        using var client = Client();

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(
            () => client.FindNationalTableAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2024), TestContext.Current.CancellationToken));

        Assert.Equal("550", exception.FtpReplyCode);
        Assert.Equal(SimFinal, exception.RemotePath);
        AssertOneListingAndNothingElse();
    }

    [Fact(Timeout = 30_000)]
    public async Task ListNationalTablesAsync_FinalFolderRefused_ThrowsTheRefusal()
    {
        _server.ListRefusal = Denied;
        using var client = Client();

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(
            () => client.ListNationalTablesAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2024), DatasusPeriod.OfYear(2025), TestContext.Current.CancellationToken));

        Assert.Equal(SimFinal, exception.RemotePath);
        AssertOneListingAndNothingElse();
    }

    [Theory(Timeout = 30_000)]
    [InlineData(Missing, false)]
    [InlineData(Missing, true)]
    [InlineData(Denied, false)]
    [InlineData(Denied, true)]
    public async Task FindNationalTableAsync_PreliminaryFolderRefusedWith550_IsAMissingTable_AndTheMessageSaysTheFolderWasNotListed(string reply, bool afterOpening)
    {
        ServeTheFinalFolderWithoutTheTable(reply, afterOpening);
        using var client = Client();

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => client.FindNationalTableAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2025), TestContext.Current.CancellationToken));

        AssertSaysThePreliminaryFolderWasNotListed(exception, reply);
        Assert.Equal(2, _server.Commands.Count(command => command == "LIST"));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(Missing)]
    [InlineData(Denied)]
    public async Task OpenNationalReaderAsync_PreliminaryFolderRefusedWith550_IsAMissingTable_AndTheMessageSaysTheFolderWasNotListed(string reply)
    {
        ServeTheFinalFolderWithoutTheTable(reply);
        using var client = Client();

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => client.OpenNationalReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2025), cancellationToken: TestContext.Current.CancellationToken));

        AssertSaysThePreliminaryFolderWasNotListed(exception, reply);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(Missing, false)]
    [InlineData(Denied, true)]
    public async Task ListNationalTablesAsync_PreliminaryFolderRefusedWith550_ReturnsTheTablesOfTheFinalFolder(string reply, bool afterOpening)
    {
        ServeTheFinalFolderWithoutTheTable(reply, afterOpening);
        using var client = Client();

        var tables = await client.ListNationalTablesAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2024), DatasusPeriod.OfYear(2025), TestContext.Current.CancellationToken);

        var table = Assert.Single(tables);
        Assert.Equal(SimFinal + "/DOBR2024.dbc", table.Path);
        Assert.Null(table.Uf);
        Assert.False(table.IsPreliminary);
        Assert.Equal(2, _server.Commands.Count(command => command == "LIST"));
    }

    // Found in the folder of final data: the preliminary one is not asked at all.
    [Fact(Timeout = 30_000)]
    public async Task FindNationalTableAsync_TableInTheFinalFolder_DoesNotListThePreliminaryOne()
    {
        ServeTheFinalFolderWithoutTheTable(Denied);
        using var client = Client();

        var table = await client.FindNationalTableAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2024), TestContext.Current.CancellationToken);

        Assert.False(table.IsPreliminary);
        Assert.Null(table.Uf);
        Assert.Equal(SimFinal + "/DOBR2024.dbc", table.Path);
        Assert.Equal(1, _server.Commands.Count(command => command == "LIST"));
    }

    // Any other permanent reply to the listing of the preliminary folder is still a refusal.
    [Fact(Timeout = 30_000)]
    public async Task FindNationalTableAsync_PreliminaryFolderRefusedWithAnother5xx_ThrowsTheRefusal()
    {
        ServeTheFinalFolderWithoutTheTable("530 Not logged in.");
        using var client = Client();

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(
            () => client.FindNationalTableAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2025), TestContext.Current.CancellationToken));

        Assert.Equal("530", exception.FtpReplyCode);
        Assert.Equal(SimPreliminary, exception.RemotePath);
        Assert.Equal(2, _server.Commands.Count(command => command == "LIST"));
    }

    private static void AssertSaysThePreliminaryFolderWasNotListed(FileNotFoundException exception, string reply)
    {
        Assert.Equal(SimFinal + "/DOBR2025.dbc", exception.FileName);
        Assert.Contains("The server has no final SIM-DO table of the whole country for 2025: " + SimFinal + "/DOBR2025.dbc does not exist, whole or in parts.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("The folder of preliminary data, " + SimPreliminary + ", was not searched, because it could not be listed", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"the server refused the listing ('{reply}')", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FTP uses 550 both for a folder that does not exist and for one that may not be read.", exception.Message, StringComparison.Ordinal);
        var refusal = Assert.IsType<DatasusRequestRefusedException>(exception.InnerException);
        Assert.Equal("550", refusal.FtpReplyCode);
        Assert.Equal(SimPreliminary, refusal.RemotePath);
    }

    // The final folder lists the file for the whole country of another year, and the server answers
    // 550 for any other file; only the listing of the preliminary folder gets the reply.
    private void ServeTheFinalFolderWithoutTheTable(string preliminaryReply, bool afterOpening = false)
    {
        _server.Listing = [LoopbackFtpServer.FileLine("DOBR2024.dbc", 10)];
        _server.Files[Root + SimFinal + "/DOBR2024.dbc"] = new byte[10];
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

    // The default number of retries; only the waits are shortened.
    private DatasusClient Client() => new(new DatasusClientOptions
    {
        Host = "127.0.0.1",
        Port = _server.Port,
        InitialRetryDelay = TimeSpan.FromMilliseconds(10),
        ConnectTimeout = TimeSpan.FromSeconds(1),
        ReadTimeout = TimeSpan.FromSeconds(1),
    });
}
