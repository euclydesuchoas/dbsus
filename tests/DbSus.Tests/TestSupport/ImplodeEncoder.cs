namespace DbSus.Tests.TestSupport;

/// <summary>One item of an implode stream: a literal byte, or a copy of earlier output.</summary>
/// <remarks>A struct rather than a record: records need IsExternalInit, which net481 lacks.</remarks>
internal readonly struct ImplodeToken(byte literal, int length, int distance)
{
    public byte Literal { get; } = literal;

    public int Length { get; } = length;

    public int Distance { get; } = distance;

    public bool IsCopy => Length > 0;

    public static ImplodeToken Of(byte literal) => new(literal, 0, 0);

    public static ImplodeToken Copy(int length, int distance) => new(0, length, distance);
}

/// <summary>
/// Writes PKWare DCL implode streams from a list of tokens, for tests only: any literal coding,
/// any dictionary size, any copy the format can express. It does not search for matches; the
/// caller chooses every token.
/// </summary>
/// <remarks>
/// The code tables are the ones blast.c gives, in its compact form, and the canonical codes are
/// assigned here (shorter codes first, then by symbol), independently of the decoder. A code is
/// written from its most significant bit, each bit inverted, as blast.c reads it.
/// </remarks>
internal static class ImplodeEncoder
{
    public const int EndOfStreamLength = 519;

    private static readonly byte[] CompactLiteralLengths =
    [
        11, 124, 8, 7, 28, 7, 188, 13, 76, 4, 10, 8, 12, 10, 12, 10, 8, 23, 8,
        9, 7, 6, 7, 8, 7, 6, 55, 8, 23, 24, 12, 11, 7, 9, 11, 12, 6, 7, 22, 5,
        7, 24, 6, 11, 9, 6, 7, 22, 7, 11, 38, 7, 9, 8, 25, 11, 8, 11, 9, 12,
        8, 12, 5, 38, 5, 38, 5, 11, 7, 5, 6, 21, 6, 10, 53, 8, 7, 24, 10, 27,
        44, 253, 253, 253, 252, 252, 252, 13, 12, 45, 12, 45, 12, 61, 12, 45,
        44, 173,
    ];

    private static readonly byte[] CompactLengthLengths = [2, 35, 36, 53, 38, 23];

    private static readonly byte[] CompactDistanceLengths = [2, 20, 53, 230, 247, 151, 248];

    private static readonly int[] LengthBase = [3, 2, 4, 5, 6, 7, 8, 9, 10, 12, 16, 24, 40, 72, 136, 264];

    private static readonly int[] LengthExtraBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8];

    private static readonly (int Code, int Length)[] LiteralCodes = CanonicalCodes(CompactLiteralLengths);
    private static readonly (int Code, int Length)[] LengthCodes = CanonicalCodes(CompactLengthLengths);
    private static readonly (int Code, int Length)[] DistanceCodes = CanonicalCodes(CompactDistanceLengths);

    /// <summary>The largest distance a copy of <paramref name="length"/> can have.</summary>
    public static int MaxDistance(int length, int dictionaryBits) => 64 << (length == 2 ? 2 : dictionaryBits);

    /// <summary>
    /// Encodes the tokens. <paramref name="dictionaryBits"/> is 4, 5 or 6 (1, 2 or 4 KiB). The end
    /// code follows unless <paramref name="withEndCode"/> is false; the last byte is padded with
    /// ones or zeros.
    /// </summary>
    public static byte[] Encode(IEnumerable<ImplodeToken> tokens, bool codedLiterals, int dictionaryBits, bool padWithOnes = false, bool withEndCode = true)
    {
        var writer = new BitWriter();
        writer.Write(codedLiterals ? 1 : 0, 8);
        writer.Write(dictionaryBits, 8);
        foreach (var token in tokens)
        {
            if (!token.IsCopy)
            {
                writer.Write(0, 1);
                if (codedLiterals)
                {
                    WriteCode(writer, LiteralCodes[token.Literal]);
                }
                else
                {
                    writer.Write(token.Literal, 8);
                }

                continue;
            }

            if (token.Length is < 2 or > 518)
            {
                throw new ArgumentOutOfRangeException(nameof(tokens), $"Copy length {token.Length} is outside 2 to 518.");
            }

            if (token.Distance < 1 || token.Distance > MaxDistance(token.Length, dictionaryBits))
            {
                throw new ArgumentOutOfRangeException(nameof(tokens), $"Copy distance {token.Distance} is outside the dictionary.");
            }

            writer.Write(1, 1);
            WriteLength(writer, token.Length);
            var extraBits = token.Length == 2 ? 2 : dictionaryBits;
            var value = token.Distance - 1;
            WriteCode(writer, DistanceCodes[value >> extraBits]);
            writer.Write(value & ((1 << extraBits) - 1), extraBits);
        }

        if (withEndCode)
        {
            writer.Write(1, 1);
            WriteLength(writer, EndOfStreamLength);
        }

        return writer.ToArray(padWithOnes);
    }

    /// <summary>The output the tokens describe, computed without any decoder.</summary>
    public static byte[] Expand(IEnumerable<ImplodeToken> tokens)
    {
        var output = new List<byte>();
        foreach (var token in tokens)
        {
            if (!token.IsCopy)
            {
                output.Add(token.Literal);
                continue;
            }

            if (token.Distance > output.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(tokens), "A copy reaches before the start of the output.");
            }

            for (var i = 0; i < token.Length; i++)
            {
                output.Add(output[output.Count - token.Distance]);
            }
        }

        return [.. output];
    }

    private static void WriteLength(BitWriter writer, int length)
    {
        for (var symbol = 0; symbol < LengthBase.Length; symbol++)
        {
            var extra = length - LengthBase[symbol];
            if (extra >= 0 && extra < 1 << LengthExtraBits[symbol])
            {
                WriteCode(writer, LengthCodes[symbol]);
                writer.Write(extra, LengthExtraBits[symbol]);
                return;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(length));
    }

    private static void WriteCode(BitWriter writer, (int Code, int Length) code)
    {
        for (var bit = code.Length - 1; bit >= 0; bit--)
        {
            writer.Write(((code.Code >> bit) & 1) ^ 1, 1);
        }
    }

    private static (int Code, int Length)[] CanonicalCodes(byte[] compact)
    {
        var lengths = new List<int>();
        foreach (var entry in compact)
        {
            for (var i = 0; i <= entry >> 4; i++)
            {
                lengths.Add(entry & 15);
            }
        }

        var codes = new (int Code, int Length)[lengths.Count];
        var next = 0;
        for (var length = 1; length <= 13; length++)
        {
            for (var symbol = 0; symbol < lengths.Count; symbol++)
            {
                if (lengths[symbol] == length)
                {
                    codes[symbol] = (next++, length);
                }
            }

            next <<= 1;
        }

        return codes;
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _buffer;
        private int _count;

        // Writes the low `count` bits of `value`, least significant bit first.
        public void Write(int value, int count)
        {
            for (var i = 0; i < count; i++)
            {
                _buffer |= ((value >> i) & 1) << _count;
                if (++_count == 8)
                {
                    _bytes.Add((byte)_buffer);
                    _buffer = 0;
                    _count = 0;
                }
            }
        }

        public byte[] ToArray(bool padWithOnes)
        {
            var result = new List<byte>(_bytes);
            if (_count > 0)
            {
                result.Add((byte)(padWithOnes ? _buffer | (0xFF << _count) : _buffer));
            }

            return [.. result];
        }
    }
}
