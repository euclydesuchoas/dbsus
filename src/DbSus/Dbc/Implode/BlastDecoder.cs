// This file is a C# port of blast.c by Mark Adler, ALTERED from the original:
//   - rewritten in C# as a pull-based decoder: output is produced on demand into the caller's
//     buffer, and decoding suspends and resumes in the middle of a length/distance copy;
//   - input is pulled from a Stream instead of a callback, and errors are reported as
//     exceptions instead of return codes;
//   - Huffman codes are decoded through lookup tables indexed by the next bits of a 64-bit bit
//     buffer, and one bit at a time when fewer bits are buffered than the longest code, instead
//     of with blast.c's bit-buffer shortcut.
// Original: https://github.com/madler/zlib, branch develop,
// commit 767c4c947852e143f582c85f14cf573411df1b35, contrib/blast/blast.c (version 1.3).
// See THIRD-PARTY-NOTICES.md.
//
// Original notice (blast.h):
//
//   Copyright (C) 2003, 2012, 2013 Mark Adler
//   version 1.3, 24 Aug 2013
//
//   This software is provided 'as-is', without any express or implied
//   warranty.  In no event will the author be held liable for any damages
//   arising from the use of this software.
//
//   Permission is granted to anyone to use this software for any purpose,
//   including commercial applications, and to alter it and redistribute it
//   freely, subject to the following restrictions:
//
//   1. The origin of this software must not be misrepresented; you must not
//      claim that you wrote the original software. If you use this software
//      in a product, an acknowledgment in the product documentation would be
//      appreciated but is not required.
//   2. Altered source versions must be plainly marked as such, and must not be
//      misrepresented as being the original software.
//   3. This notice may not be removed or altered from any source distribution.
//
//   Mark Adler    madler@alumni.caltech.edu

namespace DbSus.Dbc.Implode;

/// <summary>
/// Pull-based decoder for the PKWare Data Compression Library "implode" format (the format
/// blast.c calls "blast" and PKWare calls "explode").
/// </summary>
/// <remarks>
/// Format summary (from blast.c): the first byte is 0 when literals are stored raw or 1 when they
/// are Huffman coded; the second byte is 4, 5 or 6, the number of extra distance bits
/// (log2 of the dictionary size minus six). Then comes a sequence of items, each preceded by one
/// flag bit: 0 for a literal, 1 for a length/distance pair that copies earlier output. A length
/// of 519 ends the stream. Bits are read from the least significant bit of each byte.
/// </remarks>
internal sealed class BlastDecoder
{
    private const int MaxBits = 13;
    private const int WindowSize = 4096;
    private const int WindowMask = WindowSize - 1;
    private const int EndOfStreamLength = 519;
    private const int DefaultInputBufferSize = 64 * 1024;

    // Code lengths in blast.c's compact form: each byte is (repeat count - 1) << 4 | length.
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

    private static readonly short[] LengthBase = [3, 2, 4, 5, 6, 7, 8, 9, 10, 12, 16, 24, 40, 72, 136, 264];

    private static readonly byte[] LengthExtraBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8];

    private static readonly HuffmanTable LiteralCode = HuffmanTable.FromCompactLengths(CompactLiteralLengths);
    private static readonly HuffmanTable LengthCode = HuffmanTable.FromCompactLengths(CompactLengthLengths);
    private static readonly HuffmanTable DistanceCode = HuffmanTable.FromCompactLengths(CompactDistanceLengths);

    private readonly Stream _source;
    private readonly byte[] _input;
    private int _inputPosition;
    private int _inputCount;

    // Bits taken from _input but not yet decoded, least significant first. Whole bytes move from
    // _input into it ahead of need, but the source is read only when a bit is needed and _input is
    // empty, as when bits were taken one byte at a time.
    private ulong _bitBuffer;
    private int _bitCount;
    private long _bytesTakenIntoBitBuffer;

    private bool _headerRead;
    private bool _literalsCoded;
    private int _dictionaryBits;

    // Sliding window holding the last 4 KiB of output, the only history a distance can reach.
    private readonly byte[] _window = new byte[WindowSize];

    // A copy that did not fit in the caller's buffer; it resumes on the next Read.
    private int _pendingLength;
    private int _pendingDistance;

    public BlastDecoder(Stream source)
        : this(source, DefaultInputBufferSize)
    {
    }

    public BlastDecoder(Stream source, int inputBufferSize)
    {
        Guard.NotNull(source, nameof(source));
        Guard.Positive(inputBufferSize, nameof(inputBufferSize));

        _source = source;
        _input = new byte[inputBufferSize];
    }

    /// <summary>Gets a value indicating whether the end-of-stream code has been decoded.</summary>
    public bool IsFinished { get; private set; }

    /// <summary>
    /// Gets the number of compressed bytes consumed so far: the bytes holding at least one decoded
    /// bit. Bytes read ahead from the source into the internal buffers but not yet decoded are not
    /// counted.
    /// </summary>
    /// <remarks>
    /// The bit buffer holds fewer than eight bits of a partly decoded byte, so the whole bytes it
    /// holds are the ones not yet decoded.
    /// </remarks>
    public long InputBytesConsumed => _bytesTakenIntoBitBuffer - (_bitCount >> 3);

    /// <summary>Gets the number of decompressed bytes produced so far.</summary>
    public long OutputBytesProduced { get; private set; }

    /// <summary>
    /// Decompresses into <paramref name="destination"/> and returns the number of bytes written,
    /// which is 0 only when the destination is empty or the end of the stream has been reached.
    /// </summary>
    /// <exception cref="InvalidDataException">The compressed data is corrupt or truncated.</exception>
    public int Read(Span<byte> destination)
    {
        if (destination.IsEmpty || IsFinished)
        {
            return 0;
        }

        if (!_headerRead)
        {
            ReadHeader();
        }

        var written = 0;
        while (written < destination.Length)
        {
            if (_pendingLength > 0)
            {
                written += CopyPending(destination.Slice(written));
                continue;
            }

            if (ReadBits(1) == 0)
            {
                var literal = _literalsCoded ? Decode(LiteralCode) : ReadBits(8);
                destination[written++] = Emit((byte)literal);
                continue;
            }

            var symbol = Decode(LengthCode);
            var length = LengthBase[symbol] + ReadBits(LengthExtraBits[symbol]);
            if (length == EndOfStreamLength)
            {
                IsFinished = true;
                break;
            }

            var distanceExtraBits = length == 2 ? 2 : _dictionaryBits;
            var distance = (Decode(DistanceCode) << distanceExtraBits) + ReadBits(distanceExtraBits) + 1;
            if (distance > OutputBytesProduced)
            {
                throw new InvalidDataException(
                    $"Compressed data refers {distance} bytes back, but only {OutputBytesProduced} bytes have been produced.");
            }

            _pendingLength = length;
            _pendingDistance = distance;
        }

        return written;
    }

    private void ReadHeader()
    {
        var literalFlag = ReadBits(8);
        if (literalFlag > 1)
        {
            throw new InvalidDataException($"Invalid literal coding flag {literalFlag}; expected 0 or 1.");
        }

        var dictionaryBits = ReadBits(8);
        if (dictionaryBits is < 4 or > 6)
        {
            throw new InvalidDataException($"Invalid dictionary size code {dictionaryBits}; expected 4, 5 or 6.");
        }

        _literalsCoded = literalFlag == 1;
        _dictionaryBits = dictionaryBits;
        _headerRead = true;
    }

    private int CopyPending(Span<byte> destination)
    {
        var count = Math.Min(_pendingLength, destination.Length);

        // Byte by byte on purpose: when the distance is shorter than the length, the copy reads
        // bytes it has just written (e.g. distance 1 repeats the last byte).
        var from = (int)((OutputBytesProduced - _pendingDistance) & WindowMask);
        for (var i = 0; i < count; i++)
        {
            destination[i] = Emit(_window[from]);
            from = (from + 1) & WindowMask;
        }

        _pendingLength -= count;
        return count;
    }

    private byte Emit(byte value)
    {
        _window[OutputBytesProduced & WindowMask] = value;
        OutputBytesProduced++;
        return value;
    }

    // Returns the next `need` bits (at most 8), least significant bit first.
    private int ReadBits(int need)
    {
        if (_bitCount < need)
        {
            Refill(need);
        }

        var value = (int)((uint)_bitBuffer & ((1u << need) - 1));
        _bitBuffer >>= need;
        _bitCount -= need;
        return value;
    }

    // With enough bits buffered for the longest code, one lookup gives the symbol and its length.
    // Otherwise, or for bits that start no code, decoding goes one bit at a time, which reads the
    // source only as far as each bit requires and fails where decoding bit by bit always failed.
    private int Decode(HuffmanTable table)
    {
        if (_bitCount < table.LookupBits)
        {
            TakeBufferedBytes();
            if (_bitCount < table.LookupBits)
            {
                return DecodeBitByBit(table);
            }
        }

        int entry = table.Lookup[(int)((uint)_bitBuffer & table.LookupMask)];
        if (entry == 0)
        {
            return DecodeBitByBit(table);
        }

        var length = entry & 15;
        _bitBuffer >>= length;
        _bitCount -= length;
        return entry >> 4;
    }

    // Canonical Huffman decoding as in blast.c: codes are stored bit-reversed and inverted, so each
    // bit read is inverted and appended to the code; the first code of the shortest length is
    // all ones in the stream.
    private int DecodeBitByBit(HuffmanTable table)
    {
        var code = 0;
        var first = 0;
        var index = 0;
        for (var length = 1; length <= MaxBits; length++)
        {
            code |= ReadBits(1) ^ 1;
            int count = table.Counts[length];
            if (code < first + count)
            {
                return table.Symbols[index + (code - first)];
            }

            index += count;
            first += count;
            first <<= 1;
            code <<= 1;
        }

        throw new InvalidDataException("Invalid Huffman code in compressed data.");
    }

    // Makes at least `need` bits available, reading the source only once _input is empty.
    private void Refill(int need)
    {
        TakeBufferedBytes();
        while (_bitCount < need)
        {
            _inputCount = _source.Read(_input, 0, _input.Length);
            _inputPosition = 0;
            if (_inputCount <= 0)
            {
                _inputCount = 0;
                throw new InvalidDataException("Unexpected end of compressed data.");
            }

            TakeBufferedBytes();
        }
    }

    // Moves whole bytes from _input into the bit buffer while they fit, without reading the source.
    private void TakeBufferedBytes()
    {
        var buffer = _bitBuffer;
        var count = _bitCount;
        var position = _inputPosition;
        var end = _inputCount;
        var input = _input;
        while (count <= 56 && position < end)
        {
            buffer |= (ulong)input[position++] << count;
            count += 8;
        }

        _bytesTakenIntoBitBuffer += position - _inputPosition;
        _inputPosition = position;
        _bitBuffer = buffer;
        _bitCount = count;
    }

    private sealed class HuffmanTable
    {
        private HuffmanTable(short[] counts, short[] symbols)
        {
            Counts = counts;
            Symbols = symbols;

            var longest = MaxBits;
            while (counts[longest] == 0)
            {
                longest--;
            }

            LookupBits = longest;
            LookupMask = (1u << longest) - 1;
            Lookup = CreateLookup(counts, symbols, longest);
        }

        // Counts[n] is the number of codes of length n; Symbols lists the symbols in canonical order.
        public short[] Counts { get; }

        public short[] Symbols { get; }

        // The length of the longest code: the number of stream bits each lookup takes.
        public int LookupBits { get; }

        public uint LookupMask { get; }

        // Indexed by the next LookupBits bits of the stream, least significant first: the symbol
        // whose code they start with, shifted left by 4, plus the length of that code; 0 when they
        // start no code.
        public ushort[] Lookup { get; }

        public static HuffmanTable FromCompactLengths(byte[] compact)
        {
            var lengths = new List<int>(256);
            foreach (var entry in compact)
            {
                var repeat = (entry >> 4) + 1;
                var length = entry & 15;
                for (var i = 0; i < repeat; i++)
                {
                    lengths.Add(length);
                }
            }

            var counts = new short[MaxBits + 1];
            foreach (var length in lengths)
            {
                counts[length]++;
            }

            // The built-in tables are complete codes; anything else is a transcription error.
            var left = 1;
            for (var length = 1; length <= MaxBits; length++)
            {
                left = (left << 1) - counts[length];
                if (left < 0)
                {
                    throw new InvalidOperationException("Over-subscribed Huffman code table.");
                }
            }

            var offsets = new short[MaxBits + 1];
            for (var length = 1; length < MaxBits; length++)
            {
                offsets[length + 1] = (short)(offsets[length] + counts[length]);
            }

            var symbols = new short[lengths.Count - counts[0]];
            for (var symbol = 0; symbol < lengths.Count; symbol++)
            {
                if (lengths[symbol] != 0)
                {
                    symbols[offsets[lengths[symbol]]++] = (short)symbol;
                }
            }

            return new HuffmanTable(counts, symbols);
        }

        // Each entry is what DecodeBitByBit returns for those bits, found the same way.
        private static ushort[] CreateLookup(short[] counts, short[] symbols, int bits)
        {
            var lookup = new ushort[1 << bits];
            for (var value = 0; value < lookup.Length; value++)
            {
                var code = 0;
                var first = 0;
                var index = 0;
                for (var length = 1; length <= bits; length++)
                {
                    code |= ((value >> (length - 1)) & 1) ^ 1;
                    int count = counts[length];
                    if (code < first + count)
                    {
                        lookup[value] = (ushort)((symbols[index + (code - first)] << 4) | length);
                        break;
                    }

                    index += count;
                    first += count;
                    first <<= 1;
                    code <<= 1;
                }
            }

            return lookup;
        }
    }
}
