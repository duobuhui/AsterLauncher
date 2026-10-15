using System.Text;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldSingleStreamPatchTests
{
    [Fact]
    public async Task CopiesOldBytesWithoutChangingSource()
    {
        var old = Encoding.ASCII.GetBytes("abcdef");
        var patch = Build(old.Length, old.Length, 1, Step([0, 0, 6], [6]));
        Assert.Equal(old, await Apply(old, patch, old.Length));
        Assert.Equal("abcdef", Encoding.ASCII.GetString(old));
    }

    [Fact]
    public async Task LiteralGapsAndFinalLiteralAreNotAddedToOldBytes()
    {
        var patch = Build(6, 6, 2, Step([1, 2, 2, 0, 2, 0], [2], Encoding.ASCII.GetBytes("++!!")));
        Assert.Equal("++bc!!", Encoding.ASCII.GetString(await Apply(Encoding.ASCII.GetBytes("abcdef"), patch, 6)));
    }

    [Fact]
    public async Task ZeroRunsAndSignedByteDeltasWrapModulo256()
    {
        var patch = Build(5, 5, 1, Step([0, 0, 5], [2, 2, 1, 255, 1]));
        Assert.Equal(new byte[] { 10, 20, 31, 39, 50 }, await Apply([10, 20, 30, 40, 50], patch, 5));
    }

    [Fact]
    public async Task CoversCarryOldAndNewPositionsAcrossStepsAndCanSeekBackwards()
    {
        var patch = Build(6, 8, 2, Step([0, 0, 4], [4]), Step([0x83, 1, 3], [3], [(byte)'X']));
        Assert.Equal("abcdXbcd", Encoding.ASCII.GetString(await Apply(Encoding.ASCII.GetBytes("abcdef"), patch, 8)));
    }

    [Fact]
    public async Task LargeCoverUsesBoundedReads()
    {
        var old = Enumerable.Range(0, 1024 * 1024 + 3).Select(i => (byte)i).ToArray();
        var cover = Join(Number(0), Number(0), Number(old.Length));
        var patchBytes = Build(old.Length, old.Length, 1, Step(cover, Number(old.Length)));
        using var oldStream = new LimitedReadStream(old);
        using var patchStream = new MemoryStream(patchBytes, false);
        using var output = new MemoryStream();
        await EndfieldSingleStreamPatch.ApplyAsync(oldStream, patchStream, output, old.Length, old.Length);
        Assert.Equal(old, output.ToArray());
        Assert.InRange(oldStream.LargestRead, 1, 128 * 1024);
    }

    [Fact]
    public async Task ActualOfficial26ByteWireSampleDecodesAgainstSyntheticSizedBase()
    {
        // Real public 1.5.3 -> 1.6.3 patch bytes; synthetic base deliberately is not an official installation.
        // The decoder handles bytes only: archive integration must check the official base and target MD5.
        var patch = Convert.FromHexString("4844494646534632302600285001070900030400002820010107");
        var old = Enumerable.Range(0, 80).Select(i => (byte)i).ToArray();
        var expected = old[..40];
        expected[32]++;
        Assert.Equal(expected, await Apply(old, patch, 40));
    }

    [Fact]
    public async Task EmptyTargetAndSourceAreAllowed()
    {
        Assert.Empty(await Apply([], Build(0, 0, 0), 0));
    }

    [Theory]
    [InlineData("HDIFF13&\0")]
    [InlineData("HDIFFSF21&\0")]
    [InlineData("HDIFFSF20&zstd\0")]
    [InlineData("HDIFFSF20&lzma\0")]
    public async Task UnsupportedFormatOrCompressionFailsBeforeOutput(string label)
    {
        var bytes = Join(Encoding.ASCII.GetBytes(label), Number(3), Number(3), Number(1), Number(4), Number(6), Number(0), [3, 1, 0, 0, 3, 3]);
        using var old = new MemoryStream([1, 2, 3], false);
        using var patch = new MemoryStream(bytes, false);
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => EndfieldSingleStreamPatch.ApplyAsync(old, patch, output, 3, 3));
        Assert.Empty(output.ToArray());
    }

    [Theory]
    [MemberData(nameof(InvalidPatches))]
    public async Task RejectsInvalidBoundsAndEncoding(byte[] patch)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Apply([1, 2, 3], patch, 3));
    }

    public static TheoryData<byte[]> InvalidPatches => new()
    {
        Build(4, 3, 1, Step([0, 0, 3], [3])), // Wrong declared source size.
        Build(3, 4, 1, Step([0, 0, 3], [3])), // Wrong declared target size.
        Build(3, 3, 0), // No output covers.
        Build(3, 3, 1, Step([0, 0, 4], [4])), // Target/source cover overflow.
        Build(3, 3, 1, Step([4, 0, 3], [3])), // Source starts beyond its end.
        Build(3, 3, 1, Step([0x81, 0, 3], [3])), // Negative old position.
        Build(3, 3, 1, Step([0, 4, 0], [], [1, 2, 3, 4])), // Literal output overflow.
        Build(3, 3, 2, Step([0, 0, 0, 0, 0, 3], [3])), // Early empty cover.
        Build(3, 3, 1, Step([0, 0, 3], [2])), // Missing RLE byte.
        Build(3, 3, 1, Step([0, 0, 3], [4])), // Excess RLE span.
        Build(3, 3, 1, Step([0, 0, 3], [0, 4, 1])), // Literal delta count exceeds payload.
        Build(3, 3, 1, Step([0, 0, 3], [3, 0])), // Extra RLE data.
        Build(3, 3, 1, Step([0, 0, 3, 0, 0, 0], [3])), // Too many covers.
        Build(3, 3, 2, Step([0, 0, 3], [3])), // Too few covers.
        Build(3, 3, 1, Step([0, 0], [3])), // Truncated cover.
        Build(3, 3, 1, Step([0, 0, 3], [3]))[..^1], // Truncated payload.
        Join(Build(3, 3, 1, Step([0, 0, 3], [3])), [0]), // Trailing data outside declared payload.
        Header(3, 3, 1, 16 * 1024 * 1024 + 1, 0), // Step memory bomb.
        Join(Header(3, 3, 1, 1, 3), [2, 0, 0]), // Step exceeds declared memory.
        Join(Encoding.ASCII.GetBytes("HDIFFSF20&\0"), Enumerable.Repeat((byte)0xff, 10).ToArray()),
        Join(Header(3, 3, 1, 16, 12), [10, 0], Enumerable.Repeat((byte)0xff, 10).ToArray()),
    };

    [Fact]
    public async Task CancellationIsObservedBeforeReadingOrWriting()
    {
        using var token = new CancellationTokenSource();
        token.Cancel();
        using var old = new MemoryStream([1, 2, 3], false);
        using var patch = new MemoryStream(Build(3, 3, 1, Step([0, 0, 3], [3])), false);
        using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EndfieldSingleStreamPatch.ApplyAsync(old, patch, output, 3, 3, token.Token));
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task CancellationDuringLargeCoverStopsFurtherWrites()
    {
        const int length = 1024 * 1024;
        using var token = new CancellationTokenSource();
        using var old = new MemoryStream(new byte[length], false);
        using var patch = new MemoryStream(Build(length, length, 1,
            Step(Join(Number(0), Number(0), Number(length)), Number(length))), false);
        using var output = new CancelAfterWriteStream(token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EndfieldSingleStreamPatch.ApplyAsync(old, patch, output, length, length, token.Token));
        Assert.Equal(128 * 1024, output.Length);
    }

    [Fact]
    public async Task RefusesInPlaceOrNonEmptyOutputAndWrongActualOldSize()
    {
        var bytes = Build(3, 3, 1, Step([0, 0, 3], [3]));
        using var old = new MemoryStream([1, 2, 3]);
        using var patch = new MemoryStream(bytes, false);
        await Assert.ThrowsAsync<InvalidDataException>(() => EndfieldSingleStreamPatch.ApplyAsync(old, patch, old, 3, 3));
        using var existing = new MemoryStream([9]);
        await Assert.ThrowsAsync<InvalidDataException>(() => EndfieldSingleStreamPatch.ApplyAsync(old, patch, existing, 3, 3));
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => EndfieldSingleStreamPatch.ApplyAsync(old, patch, output, 4, 3));
    }

    private static async Task<byte[]> Apply(byte[] old, byte[] patchBytes, long newSize)
    {
        using var source = new MemoryStream(old, false);
        using var patch = new MemoryStream(patchBytes, false);
        using var output = new MemoryStream();
        await EndfieldSingleStreamPatch.ApplyAsync(source, patch, output, old.Length, newSize);
        return output.ToArray();
    }

    private static byte[] Build(long oldSize, long newSize, long count, params byte[][] steps)
    {
        var data = Join(steps);
        return Join(Header(oldSize, newSize, count, 64, data.Length), data);
    }

    private static byte[] Header(long oldSize, long newSize, long count, long memory, long dataSize) =>
        Join(Encoding.ASCII.GetBytes("HDIFFSF20&\0"), Number(newSize), Number(oldSize), Number(count),
            Number(memory), Number(dataSize), Number(0));

    private static byte[] Step(byte[] covers, byte[] rle, params byte[][] literal) =>
        Join(Number(covers.Length), Number(rle.Length), covers, rle, Join(literal));

    private static byte[] Number(long value)
    {
        var bytes = new List<byte> { (byte)(value & 0x7f) };
        while ((value >>= 7) > 0) bytes.Insert(0, (byte)(0x80 | (value & 0x7f)));
        return bytes.ToArray();
    }
    private static byte[] Join(params byte[][] chunks) => chunks.SelectMany(c => c).ToArray();

    private sealed class LimitedReadStream(byte[] bytes) : MemoryStream(bytes, false)
    {
        public int LargestRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            LargestRead = Math.Max(LargestRead, buffer.Length);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class CancelAfterWriteStream(CancellationTokenSource token) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            token.Cancel();
        }
    }
}
