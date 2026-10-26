using DbSus.Dbc;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbf;

// A Read that fails at the end of a part, where the checksum is checked, is never followed by a
// Read that returns false or moves on to the next part: false is the signal that the records were
// confirmed.
public sealed class DbfReaderPartEndFailureTests
{
    [Fact]
    public void ChecksumMismatchInTheOnlyPart_EveryLaterReadThrowsTheSameFailure()
    {
        using var reader = DbSusFile.OpenDbcReader(new MemoryStream(SyntheticDbc.BuildDbc(Part("A1", "A2"), checksum: 0)));
        Assert.True(reader.Read());
        Assert.True(reader.Read());

        var first = Assert.Throws<InvalidDataException>(() => reader.Read());

        for (var i = 0; i < 3; i++)
        {
            var again = Assert.Throws<InvalidDataException>(() => reader.Read());
            Assert.Equal(first.Message, again.Message);
        }
    }

    [Fact]
    public void ChecksumMismatchInAMiddlePart_LaterReadsNeverMoveToTheNextPart()
    {
        using var reader = DbSusFile.OpenDbcReader(
        [
            new DbcPart("TESTE2407a.dbc", new MemoryStream(SyntheticDbc.BuildDbc(Part("A1")))),
            new DbcPart("TESTE2407b.dbc", new MemoryStream(SyntheticDbc.BuildDbc(Part("B1"), checksum: 0))),
            new DbcPart("TESTE2407c.dbc", new MemoryStream(SyntheticDbc.BuildDbc(Part("C1", "C2")))),
        ]);
        var codes = new List<string>();
        var first = Assert.Throws<InvalidDataException>(() =>
        {
            while (reader.Read())
            {
                codes.Add(reader.GetString(0));
            }
        });

        for (var i = 0; i < 3; i++)
        {
            var again = Assert.Throws<InvalidDataException>(() =>
            {
                while (reader.Read())
                {
                    codes.Add(reader.GetString(0));
                }
            });
            Assert.Equal(first.Message, again.Message);
        }

        Assert.StartsWith("TESTE2407b.dbc: DBC checksum mismatch", first.Message, StringComparison.Ordinal);
        Assert.Equal(["A1", "B1"], codes);
        Assert.Equal(1, reader.PartIndex);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(InvalidDataException))]
    public void StreamFailsAfterTheLastRecord_EveryLaterReadThrowsTheSameFailure(Type failure)
    {
        var dbc = SyntheticDbc.BuildDbc(Part("A1", "A2", "A3"));
        var exception = failure == typeof(ObjectDisposedException)
            ? new ObjectDisposedException("transfer", "The transfer was closed.")
            : (Exception)Activator.CreateInstance(failure, "The transfer failed.")!;
        var stream = new FailingStream(dbc, failAt: dbc.Length - 1, exception);
        using var reader = DbSusFile.OpenDbcReader(stream);
        var codes = new List<string>();

        var first = Assert.Throws(failure, () =>
        {
            while (reader.Read())
            {
                codes.Add(reader.GetString(0));
            }
        });

        Assert.Equal(["A1", "A2", "A3"], codes);
        for (var i = 0; i < 3; i++)
        {
            var again = Assert.Throws(failure, () => reader.Read());
            Assert.Equal(first.Message, again.Message);
        }

        Assert.Equal(1, stream.FailedReads);
    }

    [Fact]
    public void ReaderThatFailedAtTheEndOfAPart_IsStillDisposed_AndThenThrowsObjectDisposed()
    {
        var stream = new FailingStream(SyntheticDbc.BuildDbc(Part("A1"), checksum: 0), failAt: -1, new IOException());
        var reader = DbSusFile.OpenDbcReader(stream);
        Assert.True(reader.Read());
        Assert.Throws<InvalidDataException>(() => reader.Read());

        reader.Dispose();

        Assert.True(stream.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => reader.Read());
    }

    private static byte[] Part(params string[] codes)
    {
        var builder = new DbfBuilder().Field("CODE", 'C', 2);
        foreach (var code in codes)
        {
            builder.Record(code);
        }

        return builder.Build();
    }

    // Gives one byte per Read and throws the given failure when asked for the byte at failAt, as a
    // transfer that fails after the last record.
    private sealed class FailingStream(byte[] data, int failAt, Exception failure) : Stream
    {
        private int _position;

        public bool IsDisposed { get; private set; }

        public int FailedReads { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0 || _position == data.Length)
            {
                return 0;
            }

            if (_position == failAt)
            {
                FailedReads++;
                throw failure;
            }

            buffer[offset] = data[_position++];
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
