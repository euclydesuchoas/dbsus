using DbSus.Dbc.Implode;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbc.Implode;

/// <summary>
/// The production decoder against <see cref="ReferenceBlastDecoder"/>, the decoder of version
/// 0.1.0-preview.1, run side by side: after every read, both must have returned the same bytes or
/// thrown the same exception with the same message, and must agree on the bytes consumed and
/// produced and on the end of the stream. Failure messages give positions and counts, never data.
/// </summary>
public class BlastDecoderDifferentialTests
{
    // zlib contrib/blast test/test.pk; see BlastDecoderTests.
    private static readonly byte[] UpstreamVector = [0x00, 0x04, 0x82, 0x24, 0x25, 0x8f, 0x80, 0x7f];

    private static readonly int[] BoundaryLengths =
        [2, 3, 4, 5, 8, 9, 10, 11, 12, 15, 16, 23, 24, 39, 40, 71, 72, 135, 136, 263, 264, 517, 518];

    public static TheoryData<bool, int> Modes() => new()
    {
        { false, 4 }, { false, 5 }, { false, 6 },
        { true, 4 }, { true, 5 }, { true, 6 },
    };

    public static TheoryData<string> Samples()
    {
        var data = new TheoryData<string>();
        foreach (var entry in ExpectedSamples.All)
        {
            data.Add(entry.File);
        }

        return data;
    }

    [Fact]
    public void Encoder_ReproducesTheUpstreamVector()
    {
        // Matching the vector bit for bit, its zero padding bit included, checks the test encoder.
        ImplodeToken[] tokens = [ImplodeToken.Of((byte)'A'), ImplodeToken.Of((byte)'I'), ImplodeToken.Copy(11, 2)];

        var encoded = ImplodeEncoder.Encode(tokens, codedLiterals: false, dictionaryBits: 4);

        Assert.Equal(UpstreamVector, encoded);
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(2, 3, false)]
    [InlineData(5, 4096, false)]
    [InlineData(13, 2, true)]
    [InlineData(4096, 4096, false)]
    public void UpstreamVector_SameAsReference(int chunkSize, int inputBufferSize, bool trickle)
    {
        var result = Lockstep(UpstreamVector, Fixed(chunkSize), inputBufferSize, trickle);

        Assert.True(result.Finished);
        Assert.Equal(13, result.Output.Length);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void BoundaryCopies_MatchTheTokensAndTheReference(bool codedLiterals, int dictionaryBits)
    {
        var tokens = BoundaryTokens(dictionaryBits);
        var input = ImplodeEncoder.Encode(tokens, codedLiterals, dictionaryBits);
        var expected = ImplodeEncoder.Expand(tokens);

        foreach (var chunkSize in new[] { 1, 5, 517, 518, 519, 4095, 4096, 4097, 81920 })
        {
            var result = Lockstep(input, Fixed(chunkSize), inputBufferSize: 65536, trickle: false);

            Assert.True(result.Finished);
            AssertSameBytes(expected, result.Output, $"chunk size {chunkSize}");
        }

        var random = Lockstep(input, RandomChunks(new Random(dictionaryBits * 2 + (codedLiterals ? 1 : 0))), inputBufferSize: 3, trickle: false);
        AssertSameBytes(expected, random.Output, "random chunk sizes, 3-byte input buffer");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void RandomTokens_MatchTheTokensAndTheReference(bool codedLiterals, int dictionaryBits)
    {
        var random = new Random(1000 + (dictionaryBits * 2) + (codedLiterals ? 1 : 0));
        for (var round = 0; round < 40; round++)
        {
            var tokens = RandomTokens(random, dictionaryBits, count: random.Next(1, 3000));
            var input = ImplodeEncoder.Encode(tokens, codedLiterals, dictionaryBits, padWithOnes: random.Next(2) == 0);
            var expected = ImplodeEncoder.Expand(tokens);
            var inputBufferSize = new[] { 1, 2, 7, 8, 9, 64, 65536 }[random.Next(7)];
            var trickle = random.Next(4) == 0;

            var result = Lockstep(input, RandomChunks(new Random(round)), inputBufferSize, trickle);

            Assert.True(result.Finished, $"round {round}");
            AssertSameBytes(expected, result.Output, $"round {round}");
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void EveryLiteralValue_MatchesTheReference(bool codedLiterals, int dictionaryBits)
    {
        // Every literal code, the longest (13 bits) included, at every bit alignment.
        var tokens = new List<ImplodeToken>();
        for (var shift = 0; shift < 8; shift++)
        {
            tokens.AddRange(Enumerable.Repeat(ImplodeToken.Of(0x20), shift));
            tokens.AddRange(Enumerable.Range(0, 256).Select(value => ImplodeToken.Of((byte)value)));
        }

        var input = ImplodeEncoder.Encode(tokens, codedLiterals, dictionaryBits);

        foreach (var inputBufferSize in new[] { 1, 2, 65536 })
        {
            var result = Lockstep(input, Fixed(7), inputBufferSize, trickle: false);
            AssertSameBytes(ImplodeEncoder.Expand(tokens), result.Output, $"input buffer {inputBufferSize}");
        }
    }

    [Fact]
    public void TruncatedAtEveryByte_FailsLikeTheReference()
    {
        foreach (var (name, stream) in SmallStreams())
        {
            for (var length = 0; length <= stream.Length; length++)
            {
                var input = stream.AsSpan(0, length).ToArray();
                foreach (var (chunkSize, inputBufferSize, trickle) in new[] { (1, 1, true), (3, 2, false), (4096, 65536, false) })
                {
                    var result = Lockstep(input, Fixed(chunkSize), inputBufferSize, trickle);
                    Assert.True(result.Finished == (length == stream.Length) || result.Exception is not null, $"{name}, {length} bytes");
                }
            }
        }
    }

    [Fact]
    public void OneByteChanged_FailsOrSucceedsLikeTheReference()
    {
        foreach (var (name, stream) in SmallStreams())
        {
            for (var position = 0; position < stream.Length; position++)
            {
                foreach (var mask in new byte[] { 0x01, 0x10, 0x80, 0xFF })
                {
                    var input = (byte[])stream.Clone();
                    input[position] ^= mask;
                    foreach (var (chunkSize, inputBufferSize) in new[] { (1, 1), (7, 65536), (4096, 3) })
                    {
                        Lockstep(input, Fixed(chunkSize), inputBufferSize, trickle: false, label: $"{name}, byte {position} ^ 0x{mask:X2}");
                    }
                }
            }
        }
    }

    [Fact]
    public void EmptyReads_ConsumeNothingLikeTheReference()
    {
        var input = ImplodeEncoder.Encode(BoundaryTokens(6), codedLiterals: true, dictionaryBits: 6);
        var random = new Random(77);

        var result = Lockstep(input, () => random.Next(3) == 0 ? 0 : random.Next(1, 600), inputBufferSize: 5, trickle: false);

        Assert.True(result.Finished);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void RealSample_SameAsReference(string file)
    {
        var expected = ExpectedSamples.Get(file);
        var path = new[] { RepositoryPaths.Fixtures, RepositoryPaths.LocalSamples }
            .Select(directory => Path.Combine(directory, file))
            .FirstOrDefault(File.Exists);
        if (path is null)
        {
            Assert.Skip($"{file} is not in tests/data/fixtures or tests/data/local; run tools/fetch-samples.");
        }

        if (ExpectedSamples.Sha256OfFile(path) != expected.DbcSha256)
        {
            Assert.Skip($"{file} on disk is not the version pinned in expected.json.");
        }

        // The implode stream starts after the DBF header and the stored CRC-32.
        var start = new byte[10];
        using (var file1 = File.OpenRead(path))
        {
            Assert.Equal(start.Length, file1.Read(start, 0, start.Length));
        }

        var offset = (start[8] | (start[9] << 8)) + 4;
        using var actualSource = File.OpenRead(path);
        using var referenceSource = File.OpenRead(path);
        actualSource.Position = offset;
        referenceSource.Position = offset;

        var result = Lockstep(new BlastDecoder(actualSource), new ReferenceBlastDecoder(referenceSource), RandomChunks(new Random(file.Length)), keepOutput: false, label: file);

        Assert.True(result.Finished);
        Assert.Equal(expected.DbfSize - offset + 4, result.Produced);
    }

    private static void AssertSameBytes(byte[] expected, byte[] actual, string label)
    {
        Assert.True(expected.Length == actual.Length, $"{label}: {actual.Length} bytes instead of {expected.Length}.");
        var mismatch = FirstMismatch(expected, actual);
        Assert.True(mismatch < 0, $"{label}: first different byte at position {mismatch}.");
    }

    private static int FirstMismatch(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                return i;
            }
        }

        return -1;
    }

    private static Func<int> Fixed(int chunkSize) => () => chunkSize;

    // Mostly small reads, some large: sizes spread evenly on a logarithmic scale up to 128 KiB.
    private static Func<int> RandomChunks(Random random) => () => (int)Math.Pow(2, random.NextDouble() * 17);

    private static LockstepResult Lockstep(byte[] input, Func<int> nextChunk, int inputBufferSize, bool trickle, string label = "")
    {
        Stream Source() => trickle ? new TrickleStream(input) : new MemoryStream(input, writable: false);

        return Lockstep(new BlastDecoder(Source(), inputBufferSize), new ReferenceBlastDecoder(Source(), inputBufferSize), nextChunk, keepOutput: true, label);
    }

    private static LockstepResult Lockstep(BlastDecoder actual, ReferenceBlastDecoder reference, Func<int> nextChunk, bool keepOutput, string label)
    {
        var output = new MemoryStream();
        var actualBuffer = new byte[1 << 17];
        var referenceBuffer = new byte[1 << 17];
        for (var call = 0; ; call++)
        {
            var size = Math.Min(nextChunk(), actualBuffer.Length);
            var actualOutcome = Outcome.Of(() => actual.Read(actualBuffer.AsSpan(0, size)));
            var referenceOutcome = Outcome.Of(() => reference.Read(referenceBuffer.AsSpan(0, size)));
            var where = $"{label} call {call} (destination of {size} bytes, {reference.OutputBytesProduced} bytes produced)";

            Assert.True(
                actualOutcome.ExceptionType == referenceOutcome.ExceptionType,
                $"{where}: {actualOutcome.ExceptionType?.Name ?? "no exception"} instead of {referenceOutcome.ExceptionType?.Name ?? "no exception"}.");
            Assert.True(actualOutcome.Message == referenceOutcome.Message, $"{where}: the exception messages differ.");
            Assert.True(actualOutcome.Count == referenceOutcome.Count, $"{where}: {actualOutcome.Count} bytes instead of {referenceOutcome.Count}.");
            var mismatch = FirstMismatch(referenceBuffer.AsSpan(0, referenceOutcome.Count), actualBuffer.AsSpan(0, actualOutcome.Count));
            Assert.True(mismatch < 0, $"{where}: first different byte at position {mismatch} of the read.");
            Assert.True(actual.InputBytesConsumed == reference.InputBytesConsumed, $"{where}: {actual.InputBytesConsumed} bytes consumed instead of {reference.InputBytesConsumed}.");
            Assert.True(actual.OutputBytesProduced == reference.OutputBytesProduced, $"{where}: {actual.OutputBytesProduced} bytes produced instead of {reference.OutputBytesProduced}.");
            Assert.True(actual.IsFinished == reference.IsFinished, $"{where}: IsFinished is {actual.IsFinished} instead of {reference.IsFinished}.");

            if (keepOutput)
            {
                output.Write(actualBuffer, 0, actualOutcome.Count);
            }

            if (referenceOutcome.ExceptionType is not null)
            {
                // Once more after the failure: both must still behave alike.
                var retryActual = Outcome.Of(() => actual.Read(actualBuffer.AsSpan(0, 64)));
                var retryReference = Outcome.Of(() => reference.Read(referenceBuffer.AsSpan(0, 64)));
                Assert.True(retryActual.ExceptionType == retryReference.ExceptionType && retryActual.Message == retryReference.Message && retryActual.Count == retryReference.Count, $"{where}: the read after the failure differs.");
                Assert.True(actual.InputBytesConsumed == reference.InputBytesConsumed, $"{where}: after the failure, {actual.InputBytesConsumed} bytes consumed instead of {reference.InputBytesConsumed}.");
                return new LockstepResult(output.ToArray(), false, reference.OutputBytesProduced, referenceOutcome.ExceptionType);
            }

            if (size > 0 && referenceOutcome.Count == 0)
            {
                return new LockstepResult(output.ToArray(), reference.IsFinished, reference.OutputBytesProduced, null);
            }
        }
    }

    // A long run of literals (so that copies can reach the whole dictionary), then copies of
    // every boundary length at the boundary distances: 1, around the length, the largest of a
    // two-byte copy (256) and the largest of the dictionary; then one copy of each distance symbol.
    private static List<ImplodeToken> BoundaryTokens(int dictionaryBits)
    {
        var tokens = new List<ImplodeToken>();
        var random = new Random(dictionaryBits);
        var maxDictionary = 64 << dictionaryBits;
        for (var i = 0; i < maxDictionary + 10; i++)
        {
            tokens.Add(ImplodeToken.Of((byte)random.Next(256)));
        }

        foreach (var length in BoundaryLengths)
        {
            var max = ImplodeEncoder.MaxDistance(length, dictionaryBits);
            foreach (var distance in new[] { 1, 2, 3, length - 1, length, length + 1, 255, 256, max - 1, max })
            {
                if (distance >= 1 && distance <= max)
                {
                    tokens.Add(ImplodeToken.Copy(length, distance));
                    tokens.Add(ImplodeToken.Of((byte)random.Next(256)));
                }
            }
        }

        for (var symbol = 0; symbol < 64; symbol++)
        {
            tokens.Add(ImplodeToken.Copy(3, (symbol << dictionaryBits) + 1));
            tokens.Add(ImplodeToken.Copy(7, (symbol << dictionaryBits) + (1 << dictionaryBits)));
            tokens.Add(ImplodeToken.Copy(2, (symbol << 2) + 1 + (symbol % 4)));
        }

        return tokens;
    }

    private static List<ImplodeToken> RandomTokens(Random random, int dictionaryBits, int count)
    {
        var tokens = new List<ImplodeToken>(count);
        long produced = 0;
        for (var i = 0; i < count; i++)
        {
            if (produced == 0 || random.Next(3) == 0)
            {
                tokens.Add(ImplodeToken.Of((byte)(random.Next(4) == 0 ? random.Next(256) : random.Next(0x20, 0x40))));
                produced++;
                continue;
            }

            var length = random.Next(4) switch
            {
                0 => BoundaryLengths[random.Next(BoundaryLengths.Length)],
                1 => random.Next(2, 519),
                _ => random.Next(2, 30),
            };
            var max = (int)Math.Min(produced, ImplodeEncoder.MaxDistance(length, dictionaryBits));
            var distance = random.Next(3) switch
            {
                0 => random.Next(1, Math.Min(max, length) + 1),
                1 => max,
                _ => random.Next(1, max + 1),
            };
            tokens.Add(ImplodeToken.Copy(length, distance));
            produced += length;
        }

        return tokens;
    }

    private static IEnumerable<(string Name, byte[] Stream)> SmallStreams()
    {
        yield return ("upstream vector", UpstreamVector);
        var random = new Random(5);
        foreach (var codedLiterals in new[] { false, true })
        {
            foreach (var dictionaryBits in new[] { 4, 5, 6 })
            {
                var tokens = RandomTokens(random, dictionaryBits, count: 60);
                tokens.Add(ImplodeToken.Copy(518, 1));
                tokens.Add(ImplodeToken.Copy(2, Math.Min(256, tokens.Count)));
                yield return ($"tokens, coded {codedLiterals}, dictionary {dictionaryBits}", ImplodeEncoder.Encode(tokens, codedLiterals, dictionaryBits));
            }
        }
    }

    private sealed class LockstepResult(byte[] output, bool finished, long produced, Type? exception)
    {
        public byte[] Output { get; } = output;

        public bool Finished { get; } = finished;

        public long Produced { get; } = produced;

        public Type? Exception { get; } = exception;
    }

    private readonly struct Outcome(int count, Type? exceptionType, string? message)
    {
        public int Count { get; } = count;

        public Type? ExceptionType { get; } = exceptionType;

        public string? Message { get; } = message;

        public static Outcome Of(Func<int> read)
        {
            try
            {
                return new Outcome(read(), null, null);
            }
            catch (Exception exception)
            {
                return new Outcome(0, exception.GetType(), exception.Message);
            }
        }
    }
}
