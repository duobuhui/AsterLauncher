using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AsterLauncher.Infrastructure;

/// <summary>
/// Isolated OpenSSL HTTPS process for the publisher's public distribution hosts.
/// Windows Schannel fails before HTTP on some installations; this handler keeps signed URLs
/// on stdin, streams response bodies, and never accepts cookies or arbitrary hosts.
/// </summary>
public sealed class EndfieldNodeHttpHandler : HttpMessageHandler
{
    private readonly string _nodePath;
    private readonly string _scriptPath;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public EndfieldNodeHttpHandler(string runtimeDirectory)
    {
        _nodePath = Path.Combine(runtimeDirectory, "node.exe");
        _scriptPath = Path.Combine(runtimeDirectory, "endfield-https.js");
        if (!File.Exists(_nodePath) || !File.Exists(_scriptPath))
            throw new FileNotFoundException("终末地官方网络运行文件缺失，请重新解压完整发布包。", runtimeDirectory);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null || !Allowed(request.RequestUri))
            throw new InvalidOperationException("终末地下载请求的目标地址未获允许。");
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Post)
            throw new InvalidOperationException("终末地官方网络请求方法无效。");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "Range", "Accept", "User-Agent" })
            if (request.Headers.TryGetValues(name, out var values)) headers[name] = string.Join(", ", values);
        if (request.Content?.Headers.ContentType is { } type) headers["Content-Type"] = type.ToString();
        var body = request.Content is null ? null : Convert.ToBase64String(
            await request.Content.ReadAsByteArrayAsync(cancellationToken));
        var payload = JsonSerializer.Serialize(new NodeRequest(
            request.RequestUri.AbsoluteUri, request.Method.Method, headers, body), JsonOptions);
        var start = new ProcessStartInfo(_nodePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_scriptPath)!
        };
        start.ArgumentList.Add(_scriptPath);
        start.Environment.Remove("NODE_OPTIONS");
        start.Environment.Remove("NODE_PATH");
        var process = Process.Start(start) ?? throw new IOException("无法启动官方网络传输进程。");
        process.ErrorDataReceived += (_, _) => { /* Never log URLs or transport stderr. */ };
        process.BeginErrorReadLine();
        var cancellation = cancellationToken.Register(() => Kill(process));
        try
        {
            await process.StandardInput.WriteAsync(payload);
            process.StandardInput.Close();
            var headerBytes = new List<byte>();
            var one = new byte[1];
            while (headerBytes.Count < 16384)
            {
                var count = await process.StandardOutput.BaseStream.ReadAsync(one, cancellationToken);
                if (count == 0) throw new IOException("官方网络传输未返回响应头。");
                if (one[0] == (byte)'\n') break;
                headerBytes.Add(one[0]);
            }
            if (headerBytes.Count >= 16384) throw new InvalidDataException("官方网络响应头过长。");
            var header = JsonSerializer.Deserialize<NodeHeader>(Encoding.UTF8.GetString(headerBytes.ToArray()), JsonOptions)
                ?? throw new InvalidDataException("官方网络响应头无效。");
            if (!string.IsNullOrWhiteSpace(header.Error))
                throw new HttpRequestException("官方网络请求失败：" + header.Error);
            if (header.Status is < 100 or > 599)
                throw new InvalidDataException("官方网络响应状态无效。");
            var response = new HttpResponseMessage((HttpStatusCode)header.Status)
            {
                RequestMessage = request,
                Content = new StreamContent(new ProcessStream(process, cancellation))
            };
            foreach (var (name, value) in header.Headers ?? [])
            {
                if (name.Equals("content-length", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("content-range", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("content-type", StringComparison.OrdinalIgnoreCase))
                    response.Content.Headers.TryAddWithoutValidation(name, value);
                else response.Headers.TryAddWithoutValidation(name, value);
            }
            return response;
        }
        catch
        {
            cancellation.Dispose();
            Kill(process);
            process.Dispose();
            throw;
        }
    }

    private static bool Allowed(Uri uri)
    {
        var host = uri.Host;
        return uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && (uri.IsDefaultPort || uri.Port == 443)
            && (host.Equals("launcher.hypergryph.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".hycdn.cn", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".hypergryph.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".gryphline.com", StringComparison.OrdinalIgnoreCase));
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private sealed record NodeRequest(string Url, string Method, Dictionary<string, string> Headers, string? Body);
    private sealed record NodeHeader(int Status, Dictionary<string, string>? Headers, string? Error);

    private sealed class ProcessStream : Stream
    {
        private readonly Process _process;
        private readonly CancellationTokenRegistration _cancellation;
        public ProcessStream(Process process, CancellationTokenRegistration cancellation)
        {
            _process = process;
            _cancellation = cancellation;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken);
            if (read != 0) return read;
            await _process.WaitForExitAsync(cancellationToken);
            if (_process.ExitCode != 0) throw new IOException("官方网络响应在传输中断开。");
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cancellation.Dispose();
                Kill(_process);
                _process.Dispose();
            }
            base.Dispose(disposing);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
