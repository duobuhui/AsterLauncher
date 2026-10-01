#if DEBUG
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.App.Services;

// Isolated UI acceptance fixture; omitted from every Release build.
internal sealed class EndfieldMaintenanceUiFixture : HttpMessageHandler,IEndfieldDistributionProvider
{
    private readonly byte[] _exe=new byte[1024*1024];
    private readonly byte[] _resource=new byte[12*1024*1024];
    public static bool Enabled=>Environment.GetEnvironmentVariable("ASTERLAUNCHER_UI_TEST_MAINTENANCE")=="1"
        && LauncherDataPaths.ResolveDataDirectory().StartsWith(@"E:\Study\Launcher\.artifacts\ui-refresh\v0.1.5-maintenance",StringComparison.OrdinalIgnoreCase);
    public Task<string> GetLatestVersionAsync(EndfieldChannel channel,string? installedVersion,CancellationToken token=default)=>Task.FromResult("fixture-1");
    public Task<EndfieldPackage> GetPackageAsync(EndfieldChannel channel,string? installedVersion,CancellationToken token=default)=>Task.FromResult(new EndfieldPackage(channel,"fixture-1",new("https://beyond.hycdn.cn/ui-fixture/"),new string('0',32),
        [Entry("Endfield.exe",_exe),Entry("Endfield_Data/StreamingAssets/VFS/0123ABCD/0123456789ABCDEF0123456789ABCDEF.chk",_resource)],null,null));
    private static EndfieldManifestFile Entry(string path,byte[] bytes)=>new(path,bytes.Length,Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant());
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        var bytes=request.RequestUri!.AbsolutePath.EndsWith("Endfield.exe",StringComparison.Ordinal)?_exe:_resource;
        var offset=(int)(request.Headers.Range?.Ranges.Single().From??0);
        var response=new HttpResponseMessage(offset>0?HttpStatusCode.PartialContent:HttpStatusCode.OK){Content=new StreamContent(new SlowStream(bytes,offset))};
        response.Content.Headers.ContentLength=bytes.Length-offset;
        if(offset>0)response.Content.Headers.ContentRange=new ContentRangeHeaderValue(offset,bytes.Length-1,bytes.Length);
        return Task.FromResult(response);
    }
    private sealed class SlowStream(byte[] bytes,int offset):Stream
    {
        private int _position=offset;
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>bytes.Length;public override long Position{get=>_position;set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {
            await Task.Delay(45,token);var count=Math.Min(64*1024,Math.Min(buffer.Length,bytes.Length-_position));
            bytes.AsMemory(_position,count).CopyTo(buffer);_position+=count;return count;
        }
        public override int Read(byte[] buffer,int offset,int count)=>ReadAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
#endif