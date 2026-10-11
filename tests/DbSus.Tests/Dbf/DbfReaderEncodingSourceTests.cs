using System.Text;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbf;

public class DbfReaderEncodingSourceTests
{
    [Theory]
    [InlineData(0x02, 850)]
    [InlineData(0x03, 1252)]
    [InlineData(0x57, 28591)]
    [InlineData(0x58, 1252)]
    public void KnownLanguageDriver_IsLanguageDriver(byte languageDriverId, int codePage)
    {
        using var reader = Open(languageDriverId);

        Assert.Equal(DbfEncodingSource.LanguageDriver, reader.EncodingSource);
        Assert.Equal(codePage, reader.Encoding.CodePage);
    }

    [Fact]
    public void KnownLanguageDriverWithFallbackEncodingSet_IsStillLanguageDriver()
    {
        using var reader = Open(0x03, new DbfReadOptions { FallbackEncoding = Cp850() });

        Assert.Equal(DbfEncodingSource.LanguageDriver, reader.EncodingSource);
        Assert.Equal(1252, reader.Encoding.CodePage);
    }

    [Theory]
    [InlineData(0x00)]    // no driver
    [InlineData(0x03)]    // a known driver: the option still wins
    [InlineData(0x35)]    // an unknown driver
    public void EncodingOption_IsOption_WhateverTheLanguageDriver(byte languageDriverId)
    {
        using var reader = Open(languageDriverId, new DbfReadOptions { Encoding = Cp850(), FallbackEncoding = Encoding.ASCII });

        Assert.Equal(DbfEncodingSource.Option, reader.EncodingSource);
        Assert.Equal(850, reader.Encoding.CodePage);
    }

    [Theory]
    [InlineData(0x00)]    // no driver
    [InlineData(0x35)]    // not in the map (seen in SIM files)
    public void NoneOrUnknownLanguageDriver_IsFallback_WithCodePage1252(byte languageDriverId)
    {
        using var reader = Open(languageDriverId);

        Assert.Equal(DbfEncodingSource.Fallback, reader.EncodingSource);
        Assert.Equal(1252, reader.Encoding.CodePage);
        Assert.Equal(languageDriverId, reader.Header.LanguageDriverId);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x35)]
    public void NoneOrUnknownLanguageDriverWithFallbackEncodingSet_IsFallback_WithThatEncoding(byte languageDriverId)
    {
        using var reader = Open(languageDriverId, new DbfReadOptions { FallbackEncoding = Cp850() });

        Assert.Equal(DbfEncodingSource.Fallback, reader.EncodingSource);
        Assert.Equal(850, reader.Encoding.CodePage);
    }

    // No code page of the map is missing on the runtimes tested, so the lookup stands for a runtime
    // that does not have the one the driver declares.
    [Fact]
    public void KnownLanguageDriverWhoseCodePageTheRuntimeLacks_IsFallback_WithCodePage1252()
    {
        var encoding = DbfEncodings.Resolve(0x02, new DbfReadOptions(), Without(850), out var source);

        Assert.Equal(DbfEncodingSource.Fallback, source);
        Assert.Equal(1252, encoding.CodePage);
    }

    [Fact]
    public void KnownLanguageDriverWhoseCodePageTheRuntimeLacks_IsFallback_WithFallbackEncoding()
    {
        var encoding = DbfEncodings.Resolve(0x02, new DbfReadOptions { FallbackEncoding = Encoding.ASCII }, Without(850), out var source);

        Assert.Equal(DbfEncodingSource.Fallback, source);
        Assert.Same(Encoding.ASCII, encoding);
    }

    [Fact]
    public void KnownLanguageDriverWhoseCodePageTheRuntimeLacks_WithEncodingOption_IsOption()
    {
        var encoding = DbfEncodings.Resolve(0x02, new DbfReadOptions { Encoding = Encoding.ASCII }, Without(850), out var source);

        Assert.Equal(DbfEncodingSource.Option, source);
        Assert.Same(Encoding.ASCII, encoding);
    }

    [Fact]
    public void Lookup_WithTheRuntimesCodePages_GivesTheSameAsTheReader()
    {
        var encoding = DbfEncodings.Resolve(0x02, new DbfReadOptions(), out var source);

        Assert.Equal(DbfEncodingSource.LanguageDriver, source);
        Assert.Equal(850, encoding.CodePage);
    }

    [Fact]
    public void Parts_TheFirstPartDecides()
    {
        // Both parts select code page 1252: the first by its driver, the second by the fallback.
        var first = new DbfBuilder { LanguageDriverId = 0x03 }.Field("CODE", 'C', 2).Record("A1").Build();
        var second = new DbfBuilder { LanguageDriverId = 0x00 }.Field("CODE", 'C', 2).Record("B1").Build();

        using var reader = DbfReader.Open([new MemoryStream(first), new MemoryStream(second)]);

        Assert.Equal(DbfEncodingSource.LanguageDriver, reader.EncodingSource);
        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(0x00, reader.Header.LanguageDriverId);   // the header of the current part
        Assert.Equal(DbfEncodingSource.LanguageDriver, reader.EncodingSource);
        Assert.Equal(1252, reader.Encoding.CodePage);
    }

    [Fact]
    public void Parts_FirstPartWithoutDriver_IsFallback()
    {
        var first = new DbfBuilder { LanguageDriverId = 0x00 }.Field("CODE", 'C', 2).Record("A1").Build();
        var second = new DbfBuilder { LanguageDriverId = 0x03 }.Field("CODE", 'C', 2).Record("B1").Build();

        using var reader = DbfReader.Open([new MemoryStream(first), new MemoryStream(second)]);

        Assert.Equal(DbfEncodingSource.Fallback, reader.EncodingSource);
    }

    [Theory]
    [InlineData(0x03, DbfEncodingSource.LanguageDriver)]
    [InlineData(0x35, DbfEncodingSource.Fallback)]
    public void Dbc_HasTheSourceOfItsDbf(byte languageDriverId, DbfEncodingSource expected)
    {
        var dbf = new DbfBuilder { LanguageDriverId = languageDriverId }.Field("CODE", 'C', 2).Record("A1").Build();

        using var reader = DbSusFile.OpenDbcReader(new MemoryStream(SyntheticDbc.BuildDbc(dbf)));

        Assert.Equal(expected, reader.EncodingSource);
        Assert.True(reader.Read());
        Assert.False(reader.Read());
        Assert.Equal(expected, reader.EncodingSource);
    }

    [Fact]
    public void Dbc_EncodingOption_IsOption()
    {
        var dbf = new DbfBuilder { LanguageDriverId = 0x03 }.Field("CODE", 'C', 2).Record("A1").Build();

        using var reader = DbSusFile.OpenDbcReader(new MemoryStream(SyntheticDbc.BuildDbc(dbf)), new DbfReadOptions { Encoding = Cp850() });

        Assert.Equal(DbfEncodingSource.Option, reader.EncodingSource);
    }

    private static DbfReader Open(byte languageDriverId, DbfReadOptions? options = null)
    {
        var dbf = new DbfBuilder { LanguageDriverId = languageDriverId }.Field("TEXT", 'C', 1).Record("A").Build();
        return DbfReader.Open(new MemoryStream(dbf), options);
    }

    // The runtime's own code pages, except one.
    private static Func<int, Encoding?> Without(int missing) => codePage => codePage == missing ? null : CodePage(codePage);

#if NETFRAMEWORK
    private static Encoding CodePage(int codePage) => Encoding.GetEncoding(codePage);
#else
    private static Encoding CodePage(int codePage) => CodePagesEncodingProvider.Instance.GetEncoding(codePage)!;
#endif

    private static Encoding Cp850() => CodePage(850);
}
