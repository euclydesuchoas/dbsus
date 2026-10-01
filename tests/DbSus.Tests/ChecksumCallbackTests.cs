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
        var options = new DbcReadOptions { ChecksumMismatch = (index, name) => calls.Add((index, name, Environment.CurrentManagedThreadId)) };
        using var stream = DbcFile.OpenAsDbf(path, options);

        Assert.Throws<InvalidDataException>(() => stream.CopyTo(Stream.Null));

        Assert.Equal([(0, "RDRR2206.dbc", Environment.CurrentManagedThreadId)], calls);
    }

    [Fact]
    public void OpenAsDbf_Stream_CallbackReceivesNullName()
    {
        string? received = "unset";
        var options = new DbcReadOptions { ChecksumMismatch = (_, name) => received = name };
        using var stream = DbcFile.OpenAsDbf(new MemoryStream(SyntheticDbc.BuildDbc(Dbf, checksum: 0)), options);

        Assert.Throws<InvalidDataException>(() => stream.CopyTo(Stream.Null));

        Assert.Null(received);
    }

    [Fact]
    public void Callback_ThatThrows_DoesNotReplaceTheChecksumException()
    {
        var options = new DbcReadOptions { ChecksumMismatch = (_, _) => throw new InvalidOperationException("callback failure") };
        using var stream = DbcFile.OpenAsDbf(new MemoryStream(SyntheticDbc.BuildDbc(Dbf, checksum: 0)), options);

        var exception = Assert.Throws<InvalidDataException>(() => stream.CopyTo(Stream.Null));

        Assert.Contains("checksum mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Callback_IsNotCalledWhenTheChecksumMatches()
    {
        var called = false;
        var options = new DbcReadOptions { ChecksumMismatch = (_, _) => called = true };
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
        using var reader = DbSusFile.OpenDbcReader([first, second], dbcOptions: new DbcReadOptions { ChecksumMismatch = (i, n) => calls.Add((i, n)) });

        var exception = Assert.Throws<InvalidDataException>(() => Drain(reader));

        Assert.Equal([(1, "PAMG2407b.dbc")], calls);
        Assert.StartsWith("PAMG2407b.dbc: DBC checksum mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenDbcReader_NamedStreams_UseTheNamesInCallbackAndMessages()
    {
        var calls = new List<(int, string?)>();
        var parts = new Stream[] { new MemoryStream(SyntheticDbc.BuildDbc(Dbf, checksum: 0)), new MemoryStream(SyntheticDbc.BuildDbc(Dbf)) };
        using var reader = DbSusFile.OpenDbcReader(parts, ["PAMG2407a.dbc", "PAMG2407b.dbc"], dbcOptions: new DbcReadOptions { ChecksumMismatch = (i, n) => calls.Add((i, n)) });

        var exception = Assert.Throws<InvalidDataException>(() => Drain(reader));

        Assert.Equal([(0, "PAMG2407a.dbc")], calls);
        Assert.StartsWith("PAMG2407a.dbc: ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenDbcReader_UnnamedStreams_UsePartNumbers()
    {
        var calls = new List<(int, string?)>();
        var parts = new Stream[] { new MemoryStream(SyntheticDbc.BuildDbc(Dbf)), new MemoryStream(SyntheticDbc.BuildDbc(Dbf, checksum: 0)) };
        using var reader = DbSusFile.OpenDbcReader(parts, dbcOptions: new DbcReadOptions { ChecksumMismatch = (i, n) => calls.Add((i, n)) });

        Assert.Throws<InvalidDataException>(() => Drain(reader));

        Assert.Equal([(1, "part 2")], calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void OpenDbcReader_NamedStreams_WrongNameCount_ThrowsArgumentException(int names)
    {
        var parts = new Stream[] { new MemoryStream(SyntheticDbc.BuildDbc(Dbf)), new MemoryStream(SyntheticDbc.BuildDbc(Dbf)) };

        Assert.Throws<ArgumentException>(() => DbSusFile.OpenDbcReader(parts, Enumerable.Repeat("x.dbc", names).ToList()));
    }

    [Fact]
    public void OpenDbcReader_NamedStreams_EmptyName_ThrowsArgumentException()
    {
        var parts = new Stream[] { new MemoryStream(SyntheticDbc.BuildDbc(Dbf)) };

        Assert.Throws<ArgumentException>(() => DbSusFile.OpenDbcReader(parts, [string.Empty]));
    }

    [Fact]
    public void ConvertToDbf_Streams_KeepsTheCallback()
    {
        var called = false;
        var options = new DbcReadOptions { ChecksumMismatch = (_, _) => called = true };

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
