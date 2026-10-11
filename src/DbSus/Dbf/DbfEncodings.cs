using System.Text;

namespace DbSus.Dbf;

/// <summary>
/// Maps the header's language driver ID (LDID) to a code page. Code pages come from Microsoft's
/// Visual FoxPro code page table; 0x57, 0x58 and 0x59 (dBASE "ANSI" drivers) from GDAL's LDID
/// table, with 0x58 also confirmed by the MiraMon documentation.
/// </summary>
internal static class DbfEncodings
{
    private const int DefaultCodePage = 1252;

    private static readonly Dictionary<byte, int> CodePageByLanguageDriver = new()
    {
        [0x01] = 437,
        [0x02] = 850,
        [0x03] = 1252,
        [0x04] = 10000,
        [0x57] = 28591,
        [0x58] = 1252,
        [0x59] = 1252,
        [0x64] = 852,
        [0x65] = 866,
        [0x66] = 865,
        [0x67] = 861,
        [0x6A] = 737,
        [0x6B] = 857,
        [0x78] = 950,
        [0x79] = 949,
        [0x7A] = 936,
        [0x7B] = 932,
        [0x7C] = 874,
        [0x7D] = 1255,
        [0x7E] = 1256,
        [0x96] = 10007,
        [0x97] = 10029,
        [0x98] = 10006,
        [0xC8] = 1250,
        [0xC9] = 1251,
        [0xCA] = 1254,
        [0xCB] = 1253,
    };

    public static Encoding Resolve(byte languageDriverId, DbfReadOptions options, out DbfEncodingSource source) =>
        Resolve(languageDriverId, options, TryGet, out source);

    // The lookup is a parameter so that a test can stand for a runtime without a code page.
    internal static Encoding Resolve(byte languageDriverId, DbfReadOptions options, Func<int, Encoding?> lookup, out DbfEncodingSource source)
    {
        if (options.Encoding is not null)
        {
            source = DbfEncodingSource.Option;
            return options.Encoding;
        }

        if (CodePageByLanguageDriver.TryGetValue(languageDriverId, out var codePage) && lookup(codePage) is { } encoding)
        {
            source = DbfEncodingSource.LanguageDriver;
            return encoding;
        }

        source = DbfEncodingSource.Fallback;
        return options.FallbackEncoding ?? lookup(DefaultCodePage)!;
    }

    // On .NET Framework every Windows code page is built in. Elsewhere, code-page encodings come
    // from the provider directly, without Encoding.RegisterProvider, so the library has no
    // process-wide side effect.
    private static Encoding? TryGet(int codePage)
    {
        try
        {
#if NETFRAMEWORK
            return Encoding.GetEncoding(codePage);
#else
            return CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.GetEncoding(codePage);
#endif
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
