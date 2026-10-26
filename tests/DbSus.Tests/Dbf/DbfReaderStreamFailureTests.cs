using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbf;

// A Read whose stream fails in the middle of a record is never followed by a Read that goes on
// from there, even when the stream works again: every later Read throws the same failure, so no
// record is ever returned out of place. A field whose content does not parse is no failure of the
// stream, and the reader goes on after it.
public sealed class DbfReaderStreamFailureTests
{
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(OperationCanceledException))]
    public void StreamFailsInTheMiddleOfARecord_AndWorksAgain_EveryLaterReadThrowsTheSameFailure(Type failure)
    {
        var dbf = Table(["A001", "A002", "A003", "A004"]);
        var stream = new FlakyStream(dbf, failAt: HeaderLength(dbf) + RecordLength(dbf) + 2, Failure(failure));
        using var reader = DbfReader.Open(stream);
        Assert.True(reader.Read());
        Assert.Equal("A001", reader.GetString(0));

        var first = Assert.Throws(failure, () => reader.Read());

        Assert.Throws<InvalidOperationException>(() => reader.GetString(0));
        for (var i = 0; i < 3; i++)
        {
            var again = Assert.Throws(failure, () => reader.Read());
            Assert.Equal(first.Message, again.Message);
        }

        Assert.Equal(1, stream.FailedReads);
    }

    [Fact]
    public void DbcStreamFailsInTheMiddle_AndWorksAgain_NoRecordComesOutOfPlace_AndEveryLaterReadThrowsTheSameFailure()
    {
        string[] codes = [.. Enumerable.Range(0, 3000).Select(i => i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture))];
        var dbc = SyntheticDbc.BuildDbc(Table(codes));
        var stream = new FlakyStream(dbc, failAt: dbc.Length / 2, new IOException("The transfer failed."));
        using var reader = DbSusFile.OpenDbcReader(stream);
        var read = new List<string>();

        var first = Assert.Throws<IOException>(() =>
        {
            while (reader.Read())
            {
                read.Add(reader.GetString(0));
            }
        });

        Assert.Equal(codes.Take(read.Count), read);
        Assert.InRange(read.Count, 1, codes.Length - 1);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(first.Message, Assert.Throws<IOException>(() => reader.Read()).Message);
        }

        Assert.Equal(1, stream.FailedReads);
    }

    [Fact]
    public void FieldThatDoesNotParse_TheNextReadGoesOn()
    {
        var dbf = new DbfBuilder().Field("N", 'N', 4).Record("  12").Record("  x1").Record("  34").Build();
        using var reader = DbfReader.Open(new MemoryStream(dbf));
        Assert.True(reader.Read());
        Assert.Equal(12, reader.GetInt32(0));
        Assert.True(reader.Read());

        Assert.Throws<FormatException>(() => reader.GetInt32(0));

        Assert.True(reader.Read());
        Assert.Equal(34, reader.GetInt32(0));
        Assert.False(reader.Read());
    }

    private static byte[] Table(string[] codes)
    {
        var builder = new DbfBuilder().Field("CODE", 'C', 4);
        foreach (var code in codes)
        {
            builder.Record(code);
        }

        return builder.Build();
    }

    private static int HeaderLength(byte[] dbf) => dbf[8] | (dbf[9] << 8);

    private static int RecordLength(byte[] dbf) => dbf[10] | (dbf[11] << 8);

    private static Exception Failure(Type failure) => failure == typeof(ObjectDisposedException)
        ? new ObjectDisposedException("transfer", "The transfer was closed.")
        : (Exception)Activator.CreateInstance(failure, "The transfer failed.")!;

    // Gives one byte per Read, throws the given failure once when asked for the byte at failAt, and
    // then works again from that byte on, as a transport that fails once and recovers.
    private sealed class FlakyStream(byte[] data, int failAt, Exception failure) : Stream
    {
        private int _position;
        private bool _failed;

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

            if (_position == failAt && !_failed)
            {
                _failed = true;
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
    }
}
