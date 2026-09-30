using DbSus.Dbc;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests;

public sealed class DbSusFileTests : IDisposable
{
    private static readonly byte[] Dbf = new DbfBuilder().Field("CODE", 'C', 3).Field("QTY", 'N', 4).Record("ABC", "  12").Record("DEF", "   7").Build();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dbsus-tests", Guid.NewGuid().ToString("N"));

    public DbSusFileTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("table.dbc")]
    [InlineData("TABLE.DBC")]
    public void OpenReader_DbcExtension_DecompressesAndReads(string name)
    {
        var path = Write(name, SyntheticDbc.BuildDbc(Dbf));

        using var reader = DbSusFile.OpenReader(path);

        Assert.Equal(["ABC", "DEF"], ReadCodes(reader));
    }

    [Fact]
    public void OpenReader_DbfExtension_ReadsPlainFile()
    {
        var path = Write("table.dbf", Dbf);

        using var reader = DbSusFile.OpenReader(path);

        Assert.Equal(["ABC", "DEF"], ReadCodes(reader));
    }

    [Fact]
    public void OpenReader_UnknownExtension_ThrowsArgumentException()
    {
        var path = Write("table.bin", SyntheticDbc.BuildDbc(Dbf));

        Assert.Throws<ArgumentException>(() => DbSusFile.OpenReader(path));
    }

    [Fact]
    public void OpenDbcReader_Path_IgnoresExtension()
    {
        var path = Write("table.bin", SyntheticDbc.BuildDbc(Dbf));

        using var reader = DbSusFile.OpenDbcReader(path);

        Assert.Equal(["ABC", "DEF"], ReadCodes(reader));
    }

    [Fact]
    public void OpenDbcReader_Path_ReleasesTheFileOnDispose()
    {
        var path = Write("table.dbc", SyntheticDbc.BuildDbc(Dbf));

        using (var reader = DbSusFile.OpenDbcReader(path))
        {
            Assert.True(reader.Read());
        }

        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenDbcReader_Stream_DisposesCallerStreamUnlessLeaveOpen(bool leaveOpen)
    {
        var source = new TrickleStream(SyntheticDbc.BuildDbc(Dbf));

        using (var reader = DbSusFile.OpenDbcReader(source, dbcOptions: new DbcReadOptions { LeaveOpen = leaveOpen }))
        {
            Assert.Equal(["ABC", "DEF"], ReadCodes(reader));
        }

        Assert.Equal(!leaveOpen, source.IsDisposed);
    }

    [Fact]
    public void OpenDbcReader_Stream_DbfLeaveOpenDoesNotApply()
    {
        var source = new TrickleStream(SyntheticDbc.BuildDbc(Dbf));

        DbSusFile.OpenDbcReader(source, new DbfReadOptions { LeaveOpen = true }).Dispose();

        Assert.True(source.IsDisposed);
    }

    [Fact]
    public void OpenDbcReader_InvalidData_DoesNotDisposeCallerStream()
    {
        var dbc = SyntheticDbc.BuildDbc(Dbf);
        dbc[10] = 1; // record length shorter than the fields
        dbc[11] = 0;
        var source = new TrickleStream(dbc);

        Assert.Throws<InvalidDataException>(() => DbSusFile.OpenDbcReader(source));

        Assert.False(source.IsDisposed);
    }

    [Fact]
    public void Read_ChecksumMismatch_ThrowsOnTheFinalReadAfterAllRecords()
    {
        var dbc = SyntheticDbc.BuildDbc(Dbf, checksum: 0);
        using var reader = DbSusFile.OpenDbcReader(new MemoryStream(dbc));

        Assert.True(reader.Read());
        Assert.True(reader.Read());
        var exception = Assert.Throws<InvalidDataException>(() => reader.Read());
        Assert.Contains("checksum mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ChecksumMismatchWithVerificationDisabled_EndsNormally()
    {
        var dbc = SyntheticDbc.BuildDbc(Dbf, checksum: 0);
        using var reader = DbSusFile.OpenDbcReader(new MemoryStream(dbc), dbcOptions: new DbcReadOptions { VerifyChecksum = false });

        Assert.Equal(["ABC", "DEF"], ReadCodes(reader));
    }

    [Fact]
    public void Dispose_BeforeTheEnd_DoesNotVerifyTheChecksum()
    {
        var dbc = SyntheticDbc.BuildDbc(Dbf, checksum: 0);
        var reader = DbSusFile.OpenDbcReader(new MemoryStream(dbc));
        Assert.True(reader.Read());

        reader.Dispose();
    }

    private static List<string> ReadCodes(DbfReader reader)
    {
        var codes = new List<string>();
        var ordinal = reader.GetOrdinal("CODE");
        while (reader.Read())
        {
            codes.Add(reader.GetString(ordinal));
        }

        return codes;
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
