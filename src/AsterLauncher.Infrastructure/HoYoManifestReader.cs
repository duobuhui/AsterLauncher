using System.Text;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

/// <summary>Bounded protobuf wire reader for the publisher's chunk manifest, without generated upstream code.</summary>
public static class HoYoManifestReader
{
    public static IReadOnlyList<HoYoFile> Parse(byte[] bytes, Func<string, Uri> chunkUri, int compression)
    {
        if (bytes.Length is 0 or > 64 * 1024 * 1024) throw new InvalidDataException("官方文件清单大小无效。");
        var files = new List<HoYoFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = new Wire(bytes);
        while (root.Next())
        {
            if (root.Field != 1 || root.Kind != 2) continue;
            var file = new Wire(root.Bytes); string path = "", md5 = ""; long size = 0; bool folder = false;
            var chunks = new List<HoYoChunk>();
            while (file.Next())
            {
                switch (file.Field)
                {
                    case 1: path = file.Text(); break;
                    case 2:
                        var c = new Wire(file.Bytes); string id = "", hash = "", compressedHash = "";
                        long offset = 0, length = 0, compressed = 0;
                        while (c.Next())
                            switch (c.Field)
                            {
                                case 1: id = c.Text(); break;
                                case 2: hash = c.Text(); break;
                                case 3: offset = c.Length(); break;
                                case 4: compressed = c.Length(); break;
                                case 5: length = c.Length(); break;
                                case 7: compressedHash = c.Text(); break;
                            }
                        SafeGamePath.ValidateRelative(id);
                        if (id.Contains('/') || id.Contains('\\') || !ValidMd5(hash) || !ValidMd5(compressedHash)
                            || length <= 0 || length > 64 * 1024 * 1024 || compressed <= 0 || compressed > 64 * 1024 * 1024)
                            throw new InvalidDataException("官方分块标识无效。");
                        chunks.Add(new(id, hash, compressedHash, offset, length, compressed, chunkUri(id), compression));
                        break;
                    case 3: folder = file.Length() != 0; break;
                    case 4: size = file.Length(); break;
                    case 5: md5 = file.Text(); break;
                }
            }
            path = path.Replace('\\', '/');
            SafeGamePath.ValidateRelative(path);
            if (!names.Add(path)) throw new InvalidDataException("官方清单包含重复路径。");
            if (folder) continue;
            if (!ValidMd5(md5)) throw new InvalidDataException("官方文件哈希无效。");
            long end = 0;
            foreach (var chunk in chunks.OrderBy(c => c.Offset))
            {
                if (chunk.Offset != end) throw new InvalidDataException("官方分块存在重叠或缺口。");
                end = checked(end + chunk.Size);
            }
            if (end != size) throw new InvalidDataException("官方分块总大小与文件不符。");
            files.Add(new(path, size, md5, chunks.OrderBy(c => c.Offset).ToArray()));
            if (files.Count > 250000) throw new InvalidDataException("官方清单文件数量过多。");
        }
        if (files.Count == 0) throw new InvalidDataException("官方文件清单为空。");
        return files;
    }
    public static bool ValidMd5(string value) => value.Length == 32 && value.All(Uri.IsHexDigit);

    private ref struct Wire(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _data = data;
        private int _offset;
        public int Field { get; private set; }
        public int Kind { get; private set; }
        public ReadOnlySpan<byte> Bytes { get; private set; }
        private ulong _number;
        public bool Next()
        {
            if (_offset == _data.Length) return false;
            var tag = Varint();
            Field = checked((int)(tag >> 3)); Kind = (int)(tag & 7);
            if (Field == 0) throw new InvalidDataException("Protobuf field zero.");
            Bytes = default; _number = 0;
            switch (Kind)
            {
                case 0: _number = Varint(); break;
                case 1: Take(8); break;
                case 2: Bytes = Take(checked((int)Varint())); break;
                case 5: Take(4); break;
                default: throw new InvalidDataException("Unsupported protobuf wire type.");
            }
            return true;
        }
        private ReadOnlySpan<byte> Take(int length)
        {
            if (length < 0 || length > _data.Length - _offset) throw new InvalidDataException("Truncated protobuf.");
            var value = _data.Slice(_offset, length); _offset += length; return value;
        }
        private ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                if (_offset >= _data.Length) throw new InvalidDataException("Truncated protobuf integer.");
                var b = _data[_offset++];
                if (shift == 63 && b > 1) throw new InvalidDataException("Protobuf integer overflow.");
                value |= (ulong)(b & 127) << shift;
                if ((b & 128) == 0) return value;
            }
            throw new InvalidDataException("Protobuf integer overflow.");
        }
        public string Text() => Kind == 2 ? new UTF8Encoding(false, true).GetString(Bytes) : throw new InvalidDataException("Expected text.");
        public long Length() => Kind == 0 ? checked((long)_number) : throw new InvalidDataException("Expected size.");
    }
}