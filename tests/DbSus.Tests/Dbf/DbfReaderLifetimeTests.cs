using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbf;

public sealed class DbfReaderLifetimeTests : IDisposable
{
    private static readonly byte[] Table = new DbfBuilder().Field("CODE", 'C', 3).Record("ABC").Record("DEF").Build();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dbsus-tests", Guid.NewGuid().ToString("N"));

    public DbfReaderLifetimeTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dispose_DisposesStreamUnlessLeaveOpen(bool leaveOpen)
    {
        var source = new TrickleStream(Table);

        DbfReader.Open(source, leaveOpen: leaveOpen).Dispose();

        Assert.Equal(!leaveOpen, source.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Open_InvalidHeader_DisposesStreamUnlessLeaveOpen(bool leaveOpen)
    {
        var source = new TrickleStream(Table.Take(10).ToArray());

        Assert.Throws<InvalidDataException>(() => DbfReader.Open(source, leaveOpen: leaveOpen));

        Assert.Equal(!leaveOpen, source.IsDisposed);
    }

    [Fact]
    public void Open_Path_ReleasesTheFileOnDispose()
    {
        var path = Path.Combine(_directory, "table.dbf");
        File.WriteAllBytes(path, Table);

        using (var reader = DbfReader.Open(path))
        {
            Assert.True(reader.Read());
        }

        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Read_AfterDispose_ThrowsObjectDisposedException()
    {
        var reader = DbfReader.Open(new MemoryStream(Table));
        reader.Dispose();

        Assert.True(reader.IsClosed);
        Assert.Throws<ObjectDisposedException>(() => reader.Read());
        reader.Close();
    }

    [Fact]
    public void Read_NonSeekableInputOneByteAtATime_ReadsAllRecords()
    {
        using var reader = DbfReader.Open(new TrickleStream(Table));

        Assert.True(reader.Read());
        Assert.Equal("ABC", reader.GetString(0));
        Assert.True(reader.Read());
        Assert.Equal("DEF", reader.GetString(0));
        Assert.False(reader.Read());
    }
}
