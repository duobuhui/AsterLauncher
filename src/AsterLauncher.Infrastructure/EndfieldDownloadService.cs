using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

/// <summary>Content-addressed, resumable downloader. Only validated MD5 objects enter the shared cache.</summary>
public sealed class EndfieldDownloadService
{
    private readonly HttpClient _http;
    private readonly string _cacheRoot;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _objectLocks = new();

    public EndfieldDownloadService(HttpClient http, string dataRoot)
    {
        _http = http;
        _cacheRoot = SafeGamePath.Resolve(dataRoot, "endfield/objects");

    }

    public static Uri FileUri(Uri fileBase, string relative)
    {
        SafeGamePath.ValidateRelative(relative);
        var builder = new UriBuilder(fileBase)
        {
            Path = fileBase.AbsolutePath.TrimEnd('/') + "/" +
                string.Join("/", relative.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString)),
            Fragment = ""
        };
        return EndfieldDistributionProvider.TrustedUri(builder.Uri.AbsoluteUri);
    }

    public async Task<string> DownloadAsync(Uri initialUri, string md5, long size,
        Func<CancellationToken, Task<Uri>>? refreshUri = null,
        IProgress<long>? byteProgress = null, CancellationToken cancellationToken = default)
    {
        if (md5.Length != 32 || !md5.All(Uri.IsHexDigit) || size < 0)
            throw new ArgumentException("Invalid content identity.");
        using var operation = LocalStorageGate.BeginOperation(Path.GetDirectoryName(Path.GetDirectoryName(_cacheRoot))!);
        EndfieldDistributionProvider.TrustedUri(initialUri.ToString());
        var key = md5.ToLowerInvariant() + "-" + size;
        var gate = _objectLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_cacheRoot);
            var final = SafeGamePath.Resolve(_cacheRoot, key);
            if (await MatchesAsync(final, size, md5, cancellationToken))
            { byteProgress?.Report(size); return final; }
            if (File.Exists(final)) File.Delete(final);
            var partial = SafeGamePath.Resolve(_cacheRoot, key + ".part");
            if (File.Exists(partial) && WindowsHardLink.Identity(partial).LinkCount > 1) File.Delete(partial);
            var uri = initialUri;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await TransferAttemptAsync(uri, partial, size, byteProgress, cancellationToken);
                    if (await MatchesAsync(partial, size, md5, cancellationToken))
                    {
                        File.Move(partial, final, overwrite: true);
                        return final;
                    }
                    if (File.Exists(partial)) File.Delete(partial);
                    throw new InvalidDataException("Downloaded object hash mismatch.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // Keep .part for a later resume.
                }
                catch (Exception exception) when (attempt < 3
                    && exception is HttpRequestException or IOException or InvalidDataException)
                {
                    if (refreshUri is not null)
                    {
                        uri = await refreshUri(cancellationToken);
                        EndfieldDistributionProvider.TrustedUri(uri.ToString());
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, 1 << attempt)), cancellationToken);
                }
            }
            throw new IOException("Download retries exhausted.");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task TransferAttemptAsync(Uri uri, string partial, long expected,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (existing > expected)
        {
            File.Delete(partial);
            existing = 0;
        }
        if (existing == expected) { progress?.Report(existing); return; }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw new IOException("Server rejected saved range; retrying from the beginning.");
        }
        response.EnsureSuccessStatusCode();
        var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From != existing || range.Length != expected)
            {
                if (File.Exists(partial)) File.Delete(partial);
                throw new InvalidDataException("Unexpected server Content-Range.");
            }
        }
        else if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidDataException("Unexpected download status.");
        }
        if (response.Content.Headers.ContentLength is { } length
            && length != expected - (append ? existing : 0))
            throw new InvalidDataException("Unexpected download length.");

        await using var network = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(partial, append ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        progress?.Report(target.Length);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = await network.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report(target.Length);
            if (target.Length > expected) throw new InvalidDataException("Download exceeded expected size.");
        }
        await target.FlushAsync(cancellationToken);
        target.Flush(true);
        if (target.Length != expected) throw new IOException("Download ended before expected size.");
    }

    public static async Task<bool> MatchesAsync(string path, long size, string md5,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        await using var stream = File.OpenRead(path);
        var actual = await MD5.HashDataAsync(stream, cancellationToken);
        return actual.AsSpan().SequenceEqual(Convert.FromHexString(md5));
    }
}
