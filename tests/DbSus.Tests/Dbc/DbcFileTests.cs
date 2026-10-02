using DbSus.Dbc;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbc;

public sealed class DbcFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dbsus-tests", Guid.NewGuid().ToString("N"));

    public DbcFileTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OpenAsDbf_SyntheticDbc_ReturnsDbfBytesUnchanged(bool withEndOfFileMarker)
    {
        var dbf = SyntheticDbc.BuildDbf(withEndOfFileMarker);
        using var stream = DbcFile.OpenAsDbf(new MemoryStream(SyntheticDbc.BuildDbc(dbf)));

        var output = ReadAll(stream, chunkSize: 4096);

        Assert.Equal(dbf, output);
        Assert.Equal(withEndOfFileMarker, output[output.Length - 1] == 0x1A);
    }

    [Fact]
    public void OpenAsDbf_OneByteReads_ReturnsDbfBytesUnchanged()
    {
        var dbf = SyntheticDbc.BuildDbf(withEndOfFileMarker: true);
        using var stream = DbcFile.OpenAsDbf(new MemoryStream(SyntheticDbc.BuildDbc(dbf)));

        var output = ReadAll(stream, chunkSize: 1);

        Assert.Equal(dbf, output);
    }

    [Fact]
    public void OpenAsDbf_NonSeekableInputDeliveringOneByteAtATime_ReturnsDbfBytesUnchanged()
    {
        var dbf = SyntheticDbc.BuildDbf(withEndOfFileMarker: true);
        using var stream = DbcFile.OpenAsDbf(new TrickleStream(SyntheticDbc.BuildDbc(dbf)));

        var output = ReadAll(stream, chunkSize: 3);

        Assert.Equal(dbf, output);
    }

    [Fact]
    public void OpenAsDbf_ReturnedStream_IsReadOnlyAndForwardOnly()
    {
        var dbc = SyntheticDbc.BuildDbc(SyntheticDbc.BuildDbf(withEndOfFileMarker: true));
        using var stream = DbcFile.OpenAsDbf(new MemoryStream(dbc));

        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
    }

    [Fact]
    public void OpenAsDbf_ChecksumMismatch_ThrowsOnlyAfterEveryByteIsReturned()
    {
        var dbf = SyntheticDbc.BuildDbf(withEndOfFileMarker: true);
        var wrongChecksum = ReferenceCrc32.Compute(dbf) ^ 1;
        using var stream = DbcFile.OpenAsDbf(new MemoryStream(SyntheticDbc.BuildDbc(dbf, wrongChecksum)));
        var output = new MemoryStream();
        var buffer = new byte[5];

        var exception = Assert.Throws<InvalidDataException>(() =>
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
            }
        });

        Assert.Equal(dbf, output.ToArray());
        Assert.Contains("checksum mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenAsDbf_ChecksumMismatchWithVerificationDisabled_ReturnsDbfBytes()
    {
        var dbf = SyntheticDbc.BuildDbf(withEndOfFileMarker: true);
        var dbc = SyntheticDbc.BuildDbc(dbf, checksum: 0);
        using var stream = DbcFile.OpenAsDbf(new MemoryStream(dbc), new DbcReadOptions { VerifyChecksum = false });

        var output = ReadAll(stream, chunkSize: 4096);

        Assert.Equal(dbf, output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(-5)] // inside the stored CRC-32
    [InlineData(-1)] // last byte of the compressed body
    public void OpenAsDbf_TruncatedDbc_ThrowsInvalidDataException(int cut)
    {
        var dbf = SyntheticDbc.BuildDbf(withEndOfFileMarker: true);
        var dbc = SyntheticDbc.BuildDbc(dbf);
        var headerLength = dbf[8] | (dbf[9] << 8);
        var length = cut switch
        {
            -5 => headerLength + 2,
            -1 => dbc.Length - 1,
            _ => cut,
        };

        Assert.Throws<InvalidDataException>(() =>
        {
            using var stream = DbcFile.OpenAsDbf(new MemoryStream(dbc, 0, length));
            ReadAll(stream, chunkSize: 4096);
        });
    }

    [Fact]
    public void OpenAsDbf_HeaderLengthBelowMinimum_ThrowsInvalidDataException()
    {
        var dbc = SyntheticDbc.BuildDbc(SyntheticDbc.BuildDbf(withEndOfFileMarker: true));
        dbc[8] = 32;
        dbc[9] = 0;

        Assert.Throws<InvalidDataException>(() => DbcFile.OpenAsDbf(new MemoryStream(dbc)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenAsDbf_Dispose_DisposesSourceUnlessLeaveOpen(bool leaveOpen)
    {
        var source = new TrickleStream(SyntheticDbc.BuildDbc(SyntheticDbc.BuildDbf(withEndOfFileMarker: true)));

        DbcFile.OpenAsDbf(source, leaveOpen: leaveOpen).Dispose();

        Assert.Equal(!leaveOpen, source.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenAsDbf_InvalidData_DisposesSourceUnlessLeaveOpen(bool leaveOpen)
    {
        var source = new TrickleStream(SyntheticDbc.BuildDbc(SyntheticDbc.BuildDbf(withEndOfFileMarker: true)).Take(5).ToArray());

        Assert.Throws<InvalidDataException>(() => DbcFile.OpenAsDbf(source, leaveOpen: leaveOpen));

        Assert.Equal(!leaveOpen, source.IsDisposed);
    }

    [Fact]
    public void ConvertToDbf_InvalidData_LeavesTheStreamsOpen()
    {
        var source = new TrickleStream(SyntheticDbc.BuildDbc(SyntheticDbc.BuildDbf(withEndOfFileMarker: true)).Take(5).ToArray());
        var destination = new MemoryStream();

        Assert.Throws<InvalidDataException>(() => DbcFile.ConvertToDbf(source, destination));

        Assert.False(source.IsDisposed);
        Assert.True(destination.CanWrite);
    }

    [Fact]
    public void OpenAsDbf_AfterDispose_ReadThrowsObjectDisposedException()
    {
        var stream = DbcFile.OpenAsDbf(new MemoryStream(SyntheticDbc.BuildDbc(SyntheticDbc.BuildDbf(withEndOfFileMarker: true))));
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[4], 0, 4));
    }

    [Fact]
    public void OpenAsDbf_FilePath_ReturnsDbfBytesUnchanged()
    {
        var dbf = SyntheticDbc.BuildDbf(withEndOfFileMarker: false);
        var dbcPath = WriteFile("sample.dbc", SyntheticDbc.BuildDbc(dbf));
        using var stream = DbcFile.OpenAsDbf(dbcPath);

        Assert.Equal(dbf, ReadAll(stream, chunkSize: 4096));
    }

    [Fact]
    public void ConvertToDbf_Streams_WritesDbfAndLeavesStreamsOpen()
    {
        var dbf = SyntheticDbc.BuildDbf(withEndOfFileMarker: true);
        var source = new TrickleStream(SyntheticDbc.BuildDbc(dbf));
        var destination = new MemoryStream();

        DbcFile.ConvertToDbf(source, destination);

        Assert.Equal(dbf, destination.ToArray());
        Assert.False(source.IsDisposed);
        Assert.True(destination.CanWrite);
    }

    [Fact]
    public void ConvertToDbf_Paths_WritesDbf()
    {
        var dbf = SyntheticDbc.BuildDbf(withEndOfFileMarker: true);
        var dbcPath = WriteFile("sample.dbc", SyntheticDbc.BuildDbc(dbf));
        var dbfPath = Path.Combine(_directory, "sample.dbf");

        DbcFile.ConvertToDbf(dbcPath, dbfPath);

        Assert.Equal(dbf, File.ReadAllBytes(dbfPath));
        Assert.Equal(["sample.dbc", "sample.dbf"], ListDirectory());
    }

    [Fact]
    public void ConvertToDbf_ExistingDestination_IsReplaced()
    {
        var dbf = SyntheticDbc.BuildDbf(withEndOfFileMarker: false);
        var dbcPath = WriteFile("sample.dbc", SyntheticDbc.BuildDbc(dbf));
        var dbfPath = WriteFile("sample.dbf", [1, 2, 3]);

        DbcFile.ConvertToDbf(dbcPath, dbfPath);

        Assert.Equal(dbf, File.ReadAllBytes(dbfPath));
    }

    [Fact]
    public void ConvertToDbf_CorruptDbc_LeavesNoPartialFile()
    {
        var dbf = SyntheticDbc.BuildDbf(withEndOfFileMarker: true);
        var dbcPath = WriteFile("sample.dbc", SyntheticDbc.BuildDbc(dbf, checksum: 0));
        var dbfPath = Path.Combine(_directory, "sample.dbf");

        Assert.Throws<InvalidDataException>(() => DbcFile.ConvertToDbf(dbcPath, dbfPath));

        Assert.Equal(["sample.dbc"], ListDirectory());
    }

    [Fact]
    public void ConvertToDbf_CorruptDbc_KeepsExistingDestinationUntouched()
    {
        var dbf = SyntheticDbc.BuildDbf(withEndOfFileMarker: true);
        var dbcPath = WriteFile("sample.dbc", SyntheticDbc.BuildDbc(dbf, checksum: 0));
        var dbfPath = WriteFile("sample.dbf", [1, 2, 3]);

        Assert.Throws<InvalidDataException>(() => DbcFile.ConvertToDbf(dbcPath, dbfPath));

        Assert.Equal([1, 2, 3], File.ReadAllBytes(dbfPath));
        Assert.Equal(["sample.dbc", "sample.dbf"], ListDirectory());
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private string[] ListDirectory() =>
        [.. Directory.GetFiles(_directory).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal)!];

    private static byte[] ReadAll(Stream stream, int chunkSize)
    {
        var output = new MemoryStream();
        var buffer = new byte[chunkSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}
