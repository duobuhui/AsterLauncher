using System.Text;

namespace AsterLauncher.Infrastructure;

/// <summary>Independent uncompressed HDIFFSF20 decoder. Caller verifies old/new hashes and commits private output.</summary>
public static class EndfieldSingleStreamPatch
{
    private const int MaximumStepMemory = 16 * 1024 * 1024;
    private const int TransferSize = 128 * 1024;

    public static async Task ApplyAsync(Stream oldFile, Stream patch, Stream output,
        long expectedOldSize, long expectedNewSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReferenceEquals(oldFile, output) || ReferenceEquals(patch, output)
            || !oldFile.CanRead || !oldFile.CanSeek || !patch.CanRead || !patch.CanSeek
            || !output.CanWrite || expectedOldSize < 0 || expectedNewSize < 0
            || oldFile.Length != expectedOldSize || patch.Position != 0
            || (output.CanSeek && (output.Position != 0 || output.Length != 0)))
            throw new InvalidDataException("差分输入、大小或独立输出流无效。");
        var input = new PatchReader(patch, cancellationToken);
        if (await input.ReadLabelAsync((byte)'&') != "HDIFFSF20")
            throw new InvalidDataException("不支持此差分格式。");
        var compression = await input.ReadLabelAsync(0);
        var newSize = await input.ReadNumberAsync();
        var oldSize = await input.ReadNumberAsync();
        var coversRemaining = await input.ReadNumberAsync();
        var stepMemory = await input.ReadNumberAsync();
        var dataSize = await input.ReadNumberAsync();
        var compressedSize = await input.ReadNumberAsync();
        if (compression.Length != 0 || compressedSize != 0)
            throw new InvalidDataException("尚不支持此差分内部压缩方式。");
        if (newSize != expectedNewSize || oldSize != expectedOldSize
            || stepMemory > MaximumStepMemory || dataSize != input.Remaining
            || (coversRemaining > 0 && stepMemory == 0)
            || coversRemaining > newSize && coversRemaining - newSize > 1)
            throw new InvalidDataException("差分头大小、覆盖数量或内存预算无效。");
        var step = new byte[(int)stepMemory];
        var transfer = new byte[TransferSize];
        long oldEnd = 0, newEnd = 0;
        while (coversRemaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var coverSize = await input.ReadNumberAsync();
            var rleSize = await input.ReadNumberAsync();
            if (coverSize == 0 || coverSize > stepMemory || rleSize > stepMemory - coverSize)
                throw new InvalidDataException("差分步骤超过内存预算。");
            await input.ReadExactlyAsync(step.AsMemory(0, (int)(coverSize + rleSize)));
            var covers = new BlockReader(step, 0, (int)coverSize);
            var rle = new DeltaReader(new BlockReader(step, (int)coverSize, (int)rleSize));
            while (covers.Remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (coversRemaining == 0) throw new InvalidDataException("差分覆盖数量不一致。");
                var (backwards, distance) = covers.ReadSignedDistance();
                var gap = covers.ReadNumber();
                var length = covers.ReadNumber();
                var oldPosition = backwards ? Subtract(oldEnd, distance) : Add(oldEnd, distance);
                if (gap > newSize - newEnd || oldPosition > oldSize || length > oldSize - oldPosition
                    || length > newSize - newEnd - gap)
                    throw new InvalidDataException("差分覆盖超出源文件或目标文件。");
                if (length == 0 && coversRemaining != 1)
                    throw new InvalidDataException("差分包含非末尾空覆盖。");
                await CopyLiteralAsync(input, output, transfer, gap, cancellationToken);
                newEnd += gap;
                oldFile.Position = oldPosition;
                long left = length;
                while (left > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(left, transfer.Length);
                    await oldFile.ReadExactlyAsync(transfer.AsMemory(0, count), cancellationToken);
                    rle.AddTo(transfer.AsSpan(0, count), cancellationToken);
                    await output.WriteAsync(transfer.AsMemory(0, count), cancellationToken);
                    left -= count;
                }
                oldEnd = oldPosition + length;
                newEnd += length;
                coversRemaining--;
            }
            rle.EnsureFinished();
        }
        if (newEnd != newSize || input.Remaining != 0)
            throw new InvalidDataException("差分未完整生成目标或存在多余数据。");
        await output.FlushAsync(cancellationToken);
    }

    private static long Add(long first, long second) => second > long.MaxValue - first
        ? throw new InvalidDataException("差分整数溢出。") : first + second;
    private static long Subtract(long first, long second) => second > first
        ? throw new InvalidDataException("差分源文件位置为负数。") : first - second;

    private static async Task CopyLiteralAsync(PatchReader input, Stream output, byte[] buffer,
        long length, CancellationToken cancellationToken)
    {
        while (length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(length, buffer.Length);
            await input.ReadExactlyAsync(buffer.AsMemory(0, count));
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            length -= count;
        }
    }

    private sealed class PatchReader(Stream stream, CancellationToken cancellationToken)
    {
        private readonly byte[] _buffer = new byte[TransferSize];
        private int _position, _available;
        private long _consumed;
        public long Remaining => stream.Length - _consumed;

        public async ValueTask<byte> ReadByteAsync()
        {
            if (_position == _available)
            {
                _available = await stream.ReadAsync(_buffer, cancellationToken);
                _position = 0;
                if (_available == 0) throw new InvalidDataException("差分数据提前结束。");
            }
            _consumed++;
            return _buffer[_position++];
        }

        public async ValueTask<string> ReadLabelAsync(byte terminator)
        {
            var bytes = new List<byte>();
            while (bytes.Count < 32)
            {
                var value = await ReadByteAsync();
                if (value == terminator) return Encoding.ASCII.GetString(bytes.ToArray());
                if (value is < 0x20 or > 0x7e) throw new InvalidDataException("差分格式名称无效。");
                bytes.Add(value);
            }
            throw new InvalidDataException("差分格式名称过长。");
        }

        public async ValueTask<long> ReadNumberAsync()
        {
            long value = 0;
            for (var i = 0; i < 9; i++)
            {
                var code = await ReadByteAsync();
                if (value > (long.MaxValue >> 7)) throw new InvalidDataException("差分整数溢出。");
                value = (value << 7) | (uint)(code & 0x7f);
                if ((code & 0x80) == 0) return value;
            }
            throw new InvalidDataException("差分整数过长。");
        }

        public async ValueTask ReadExactlyAsync(Memory<byte> destination)
        {
            if (destination.Length > Remaining) throw new InvalidDataException("差分数据提前结束。");
            while (destination.Length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_position == _available)
                {
                    _available = await stream.ReadAsync(_buffer, cancellationToken);
                    _position = 0;
                    if (_available == 0) throw new InvalidDataException("差分数据提前结束。");
                }
                var count = Math.Min(_available - _position, destination.Length);
                _buffer.AsMemory(_position, count).CopyTo(destination);
                destination = destination[count..];
                _position += count;
                _consumed += count;
            }
        }
    }

    private sealed class BlockReader(byte[] buffer, int start, int count)
    {
        private int _position = start;
        private readonly int _end = start + count;
        public int Remaining => _end - _position;
        public byte ReadByte() => _position < _end ? buffer[_position++]
            : throw new InvalidDataException("差分步骤数据提前结束。");
        public long ReadNumber() => ReadNumber(ReadByte(), 0);

        public (bool Backwards, long Distance) ReadSignedDistance()
        {
            var first = ReadByte();
            return ((first & 0x80) != 0, ReadNumber(first, 1));
        }

        private long ReadNumber(byte first, int tagBits)
        {
            long value = first & (0x7f >> tagBits);
            var code = first;
            var continuation = 0x80 >> tagBits;
            for (var i = 0; (code & continuation) != 0; i++)
            {
                if (i >= 9 || value > (long.MaxValue >> 7)) throw new InvalidDataException("差分整数溢出。");
                code = ReadByte();
                continuation = 0x80;
                value = (value << 7) | (uint)(code & 0x7f);
            }
            return value;
        }
    }

    private sealed class DeltaReader(BlockReader input)
    {
        private long _zeroRemaining, _literalRemaining;
        private bool _nextIsZero = true;

        public void AddTo(Span<byte> data, CancellationToken cancellationToken)
        {
            while (!data.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_zeroRemaining > 0)
                {
                    var count = (int)Math.Min(_zeroRemaining, data.Length);
                    _zeroRemaining -= count;
                    data = data[count..];
                }
                else if (_literalRemaining > 0)
                {
                    var count = (int)Math.Min(_literalRemaining, data.Length);
                    for (var i = 0; i < count; i++) data[i] = unchecked((byte)(data[i] + input.ReadByte()));
                    _literalRemaining -= count;
                    data = data[count..];
                }
                else
                {
                    var count = input.ReadNumber();
                    if (_nextIsZero) _zeroRemaining = count;
                    else
                    {
                        if (count > input.Remaining) throw new InvalidDataException("差分增量超出步骤数据。");
                        _literalRemaining = count;
                    }
                    _nextIsZero = !_nextIsZero;
                }
            }
        }

        public void EnsureFinished()
        {
            if (input.Remaining != 0 || _zeroRemaining != 0 || _literalRemaining != 0)
                throw new InvalidDataException("差分增量数据与覆盖长度不一致。");
        }
    }
}
