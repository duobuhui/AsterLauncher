using System.Diagnostics;
namespace AsterLauncher.Infrastructure;

public sealed class HoYoContentCodec(string runtimeDirectory)
{
    public async Task<byte[]> DecodeAsync(byte[] compressed, long expectedSize, int compression, CancellationToken token = default)
    {
        if (expectedSize is < 0 or > 64 * 1024 * 1024 || compressed.Length > 64 * 1024 * 1024)
            throw new InvalidDataException("官方分块大小超出限制。");
        if (compression == 0)
            return compressed.LongLength == expectedSize ? compressed : throw new InvalidDataException("未压缩分块大小不符。");
        if (compression != 1) throw new InvalidDataException("暂不支持此官方压缩格式。");
        var start = new ProcessStartInfo(Path.Combine(runtimeDirectory, "node.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = runtimeDirectory
        };
        start.Environment.Remove("NODE_OPTIONS"); start.Environment.Remove("NODE_PATH");
        start.ArgumentList.Add(Path.Combine(runtimeDirectory, "hoyo-zstd.js"));
        start.ArgumentList.Add(expectedSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new IOException("无法启动分块解压程序。");
        using var cancel = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var error = process.StandardError.ReadToEndAsync(token);
        var writing = Task.Run(async () => { await process.StandardInput.BaseStream.WriteAsync(compressed, token); process.StandardInput.Close(); }, token);
        using var output = new MemoryStream();
        var buffer = new byte[128 * 1024]; int read;
        while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + read > expectedSize) throw new InvalidDataException("分块解压超出大小限制。");
            output.Write(buffer, 0, read);
        }
        await writing; await process.WaitForExitAsync(token); await error;
        if (process.ExitCode != 0 || output.Length != expectedSize) throw new InvalidDataException("官方分块解压失败。");
        return output.ToArray();
    }
}