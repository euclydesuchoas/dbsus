using DbSus.Dbc;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests;

public sealed class ChecksumCallbackTests : IDisposable
{
    private static readonly byte[] Dbf = new DbfBuilder().Field("CODE", 'C', 2).Record("A1").Build();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dbsus-tests", Guid.NewGuid().ToString("N"));

    public ChecksumCallbackTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void OpenAsDbf_Path_CallbackReceivesIndexZeroAndFileNameBeforeTheException()
    {
        var path = Write("RDRR2206.dbc", SyntheticDbc.BuildDbc(Dbf, checksum: 0));
        var calls = new List<(int Index, string? Name, int Thread)>();
        var options = new DbcReadOptions { ChecksumMismatch = mismatch => calls.Add((mismatch.PartIndex, mismatch.PartName, Environment.CurrentManagedThreadId)) };
        using var stream = DbcFile.OpenAsDbf(path, options);

        Assert.Throws<InvalidDataException>(() => stream.CopyTo(Stream.Null));

        Assert.Equal([(0, "RDRR2206.dbc", Environment.CurrentManagedThreadId)], calls);
    }

    [Fact]
    public void OpenAsDbf_Stream_CallbackReceivesNullName()
    {
        string? received = "unset";
        var options = new DbcReadOptions { ChecksumMismatch = mismatch => received = mismatch.PartName };
        using var stream = DbcFile.OpenAsDbf(new MemoryStream(SyntheticDbc.BuildDbc(Dbf, checksum: 0)), options);

        Assert.Throws<InvalidDataException>(() => stream.CopyTo(Stream.Null));

        Assert.Null(received);
    }

    [Fact]
    public void Callback_ReceivesTheStoredAndTheComputedChecksums()
    {
        DbcChecksumMismatch? received = null;
        var options = new DbcReadOptions { ChecksumMismatch = mismatch => received = mismatch };
        using var stream = DbcFile.OpenAsDbf(new MemoryStream(SyntheticDbc.BuildDbc(Dbf, checksum: 0x12345678)), options);

        Assert.Throws<InvalidDataException>(() => stream.CopyTo(Stream.Null));

        Assert.NotNull(received);
        Assert.Equal(0x12345678u, received.ExpectedChecksum);
        Assert.Equal(ReferenceCrc32.Compute(Dbf), received.ActualChecksum);
        Assert.Equal(0, received.PartIndex);
        Assert.Null(received.PartName);
    }

    [Fact]
    public void Callback_ThatThrows_DoesNotReplaceTheChecksumException()
    {
        var options = new DbcReadOptions { ChecksumMismatch = _ => throw new InvalidOperationException("callback failure") };
        using var stream = DbcFile.OpenAsDbf(new MemoryStream(SyntheticDbc.BuildDbc(Dbf, checksum: 0)), options);

        var exception = Assert.Throws<InvalidDataException>(() => stream.CopyTo(Stream.Null));

        Assert.Contains("checksum mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Callback_IsNotCalledWhenTheChecksumMatches()
    {
        var called = false;
        var options = new DbcReadOptions { ChecksumMismatch = _ => called = true };
        using var stream = DbcFile.OpenAsDbf(new MemoryStream(SyntheticDbc.BuildDbc(Dbf)), options);

        stream.CopyTo(Stream.Null);

        Assert.False(called);
    }

    [Fact]
    public void OpenDbcReader_Paths_CallbackReceivesThePartIndexAndFileName()
    {
        var first = Write("PAMG2407a.dbc", SyntheticDbc.BuildDbc(Dbf));
        var second = Write("PAMG2407b.dbc", SyntheticDbc.BuildDbc(Dbf, checksum: 0));
        var calls = new List<(int, string?)>();
        using var reader = DbSusFile.OpenDbcReader([first, second], dbcOptions: new DbcReadOptions { ChecksumMismatch = mismatch => calls.Add((mismatch.PartIndex, mismatch.PartName)) });

        var exception = Assert.Throws<InvalidDataException>(() => Drain(reader));

        Assert.Equal([(1, "PAMG2407b.dbc")], calls);
        Assert.StartsWith("PAMG2407b.dbc: DBC checksum mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenDbcReader_NamedParts_UseTheNamesInCallbackAndMessages()
    {
        var calls = new List<(int, string?)>();
        DbcPart[] parts = [new("PAMG2407a.dbc", new MemoryStream(SyntheticDbc.BuildDbc(Dbf, checksum: 0))), new("PAMG2407b.dbc", new MemoryStream(SyntheticDbc.BuildDbc(Dbf)))];
        using var reader = DbSusFile.OpenDbcReader(parts, dbcOptions: new DbcReadOptions { ChecksumMismatch = mismatch => calls.Add((mismatch.PartIndex, mismatch.PartName)) });

        var exception = Assert.Throws<InvalidDataException>(() => Drain(reader));

        Assert.Equal([(0, "PAMG2407a.dbc")], calls);
        Assert.StartsWith("PAMG2407a.dbc: ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenDbcReader_UnnamedParts_UsePartNumbers()
    {
        var calls = new List<(int, string?)>();
        DbcPart[] parts = [new(null, new MemoryStream(SyntheticDbc.BuildDbc(Dbf))), new(null, new MemoryStream(SyntheticDbc.BuildDbc(Dbf, checksum: 0)))];
        using var reader = DbSusFile.OpenDbcReader(parts, dbcOptions: new DbcReadOptions { ChecksumMismatch = mismatch => calls.Add((mismatch.PartIndex, mismatch.PartName)) });

        var exception = Assert.Throws<InvalidDataException>(() => Drain(reader));

        Assert.Equal([(1, "part 2")], calls);
        Assert.StartsWith("part 2: ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenDbcReader_SingleUnnamedPart_HasNoName()
    {
        string? received = "unset";
        using var reader = DbSusFile.OpenDbcReader([new DbcPart(null, new MemoryStream(SyntheticDbc.BuildDbc(Dbf, checksum: 0)))], dbcOptions: new DbcReadOptions { ChecksumMismatch = mismatch => received = mismatch.PartName });

        Assert.Throws<InvalidDataException>(() => Drain(reader));

        Assert.Null(received);
    }

    [Fact]
    public void DbcPart_EmptyName_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() => new DbcPart(string.Empty, new MemoryStream()));

        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void DbcPart_NullStream_ThrowsArgumentNullException()
    {
        Assert.Equal("stream", Assert.Throws<ArgumentNullException>(() => new DbcPart("a.dbc", null!)).ParamName);
    }

    [Fact]
    public void DbcPart_StreamOnly_HasNoName()
    {
        var stream = new MemoryStream();

        var part = new DbcPart(stream);

        Assert.Null(part.Name);
        Assert.Same(stream, part.Stream);
        Assert.Equal("stream", Assert.Throws<ArgumentNullException>(() => new DbcPart(null!)).ParamName);
    }

    [Fact]
    public void ConvertToDbf_Streams_KeepsTheCallback()
    {
        var called = false;
        var options = new DbcReadOptions { ChecksumMismatch = _ => called = true };

        Assert.Throws<InvalidDataException>(() => DbcFile.ConvertToDbf(new MemoryStream(SyntheticDbc.BuildDbc(Dbf, checksum: 0)), new MemoryStream(), options));

        Assert.True(called);
    }

    private static void Drain(DbfReader reader)
    {
        while (reader.Read())
        {
        }
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
