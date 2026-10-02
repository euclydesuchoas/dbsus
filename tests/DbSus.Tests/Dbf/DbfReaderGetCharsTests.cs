using System.Text;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbf;

public class DbfReaderGetCharsTests
{
    private const int Width = 40;

    // Accented text, a leading space (kept) and trailing spaces (trimmed), as GetString returns it.
    private static readonly string[] Values = [" São João da Ação ÇÃÕÜ", "x", "Ñandú é ò ì"];

    public static TheoryData<byte, int> LanguageDrivers => new()
    {
        { 0x02, 850 },
        { 0x03, 1252 },
        { 0x57, 28591 },
    };

    [Theory]
    [MemberData(nameof(LanguageDrivers))]
    public void GetChars_SingleByteEncodings_MatchesGetString(byte languageDriver, int codePage)
    {
        using var reader = Open(CodePage(codePage), languageDriver);
        Assert.Equal(codePage, reader.Encoding.CodePage);

        AssertMatchesGetString(reader);
    }

    [Fact]
    public void GetChars_MultiByteEncodingOverride_MatchesGetString()
    {
        using var reader = Open(Encoding.UTF8, 0x03, new DbfReadOptions { Encoding = Encoding.UTF8 });

        AssertMatchesGetString(reader);
    }

    [Fact]
    public void GetChars_BlankValueReadAsNull_ThrowsInvalidCastLikeGetString()
    {
        var dbf = new DbfBuilder().Field("NAME", 'C', 4).Record("    ").Build();
        using var reader = DbfReader.Open(new MemoryStream(dbf), new DbfReadOptions { BlankStringsAsNull = true });
        Assert.True(reader.Read());

        Assert.Throws<InvalidCastException>(() => reader.GetString(0));
        Assert.Throws<InvalidCastException>(() => reader.GetChars(0, 0, new char[4], 0, 4));
    }

    [Fact]
    public void GetChars_NonCharacterField_ThrowsInvalidCastException()
    {
        var dbf = new DbfBuilder().Field("QTY", 'N', 3).Record(" 12").Build();
        using var reader = DbfReader.Open(new MemoryStream(dbf));
        Assert.True(reader.Read());

        Assert.Throws<InvalidCastException>(() => reader.GetChars(0, 0, new char[3], 0, 3));
    }

    private static void AssertMatchesGetString(DbfReader reader)
    {
        var records = 0;
        while (reader.Read())
        {
            records++;
            var expected = reader.GetString(0);

            Assert.Equal(expected.Length, reader.GetChars(0, 0, null, 0, 0));

            foreach (var dataOffset in new long[] { 0, 1, 3, expected.Length - 1, expected.Length, expected.Length + 2 })
            {
                foreach (var length in new[] { 0, 1, 3, Width, Width * 2 })
                {
                    var buffer = Enumerable.Repeat('#', (Width * 2) + 2).ToArray();
                    var count = reader.GetChars(0, dataOffset, buffer, 2, length);

                    var expectedCount = (int)Math.Max(0, Math.Min(length, expected.Length - dataOffset));
                    Assert.Equal(expectedCount, count);
                    var expectedChars = expectedCount == 0 ? string.Empty : expected.Substring((int)dataOffset, expectedCount);
                    Assert.Equal(expectedChars, new string(buffer, 2, (int)count));
                    Assert.Equal("##", new string(buffer, 0, 2)); // nothing written before bufferOffset
                }
            }
        }

        Assert.Equal(Values.Length, records);
    }

    private static DbfReader Open(Encoding encoding, byte languageDriver, DbfReadOptions? options = null)
    {
        var builder = new DbfBuilder { Encoding = encoding, LanguageDriverId = languageDriver }.Field("NAME", 'C', Width);
        foreach (var value in Values)
        {
            var bytes = encoding.GetBytes(value);
            builder.Record(bytes.Concat(Enumerable.Repeat((byte)' ', Width - bytes.Length)).ToArray());
        }

        return DbfReader.Open(new MemoryStream(builder.Build()), options);
    }

    private static Encoding CodePage(int codePage) =>
#if NET
        CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.GetEncoding(codePage);
#else
        Encoding.GetEncoding(codePage);
#endif
}
