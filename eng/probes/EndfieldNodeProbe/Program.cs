using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

if (args.Length != 2) throw new ArgumentException("runtime directory and isolated E data directory required");
var runtime = Path.GetFullPath(args[0]);
var data = Path.GetFullPath(args[1]);
if (!data.StartsWith("E:\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("E drive data required");
Directory.CreateDirectory(data);
using var client = new HttpClient(new EndfieldNodeHttpHandler(runtime)) { Timeout = TimeSpan.FromMinutes(3) };
var provider = new EndfieldDistributionProvider(client);
var download = new EndfieldDownloadService(client, data);
foreach (var channel in new[] { EndfieldChannel.Official, EndfieldChannel.Bilibili })
{
    var package = await provider.GetPackageAsync(channel, null);
    var config = package.Files.Single(f => f.Path.Equals("config.ini", StringComparison.OrdinalIgnoreCase));
    var cfg = await download.DownloadAsync(EndfieldDownloadService.FileUri(package.FileBaseUri, config.Path), config.Md5, config.Size);
    _ = EndfieldManifestDecoder.Decrypt(await File.ReadAllBytesAsync(cfg));
    var small = package.Files.Single(file => file.Path.Equals("marquee_config.bin", StringComparison.OrdinalIgnoreCase));
    var cached = await download.DownloadAsync(EndfieldDownloadService.FileUri(package.FileBaseUri, small.Path), small.Md5, small.Size);
    var valid = await EndfieldDownloadService.MatchesAsync(cached, small.Size, small.Md5);
    if (!valid) throw new InvalidDataException("Official small file failed MD5");
    var firstPack = package.Packs!.First();
    using var request = new HttpRequestMessage(HttpMethod.Get, firstPack.Uri);
    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
    using var rangeResponse = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    var supportsRange = rangeResponse.StatusCode == System.Net.HttpStatusCode.PartialContent
        && rangeResponse.Content.Headers.ContentRange?.From == 0
        && rangeResponse.Content.Headers.ContentRange?.Length == firstPack.Size;
    if (!supportsRange) throw new InvalidDataException("Official volume did not honor a bounded Range probe");
    await using var rangeStream = await rangeResponse.Content.ReadAsStreamAsync();
    var header = new byte[1024];
    await rangeStream.ReadExactlyAsync(header);
    Console.WriteLine($"{channel}: official-volume-range=206, sampled={header.Length}, leading-bytes={Convert.ToHexString(header.AsSpan(0, 4))}");
    Console.WriteLine($"{channel}: version={package.Version}, manifest={package.Files.Count}, sample={small.Size} bytes, md5-ok={valid}, preload-open={package.PrePatch is not null}");
}
