using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

/// <summary>Local channel identification requires publisher appcode, channel pair and actual entry hash.</summary>
public static class EndfieldInstallationInfo
{
    public static async Task<(EndfieldChannel Channel, string Version)?> ReadAsync(string root,
        CancellationToken token = default)
    {
        var config=SafeGamePath.Resolve(root,"config.ini");
        var entry=SafeGamePath.Resolve(root,"Endfield.exe");
        if (!File.Exists(config) || !File.Exists(entry) || new FileInfo(config).Length > 65536) return null;
        var bytes=await File.ReadAllBytesAsync(config,token);
        string text;
        try { text=new UTF8Encoding(false,true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            try { text=new UTF8Encoding(false,true).GetString(EndfieldManifestDecoder.Decrypt(bytes)); }
            catch (Exception e) when (e is CryptographicException or DecoderFallbackException) { return null; }
        }
        var values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var gameSection=false;
        foreach (var raw in text.Split('\n'))
        {
            var line=raw.Trim().TrimStart('\uFEFF');
            if (line.StartsWith('[')) { gameSection=line.Equals("[Game]",StringComparison.OrdinalIgnoreCase);continue; }
            if (!gameSection || !line.Contains('=')) continue;
            var parts=line.Split('=',2);
            if (!values.TryAdd(parts[0].Trim(),parts[1].Trim())) return null;
        }
        if (values.GetValueOrDefault("appcode")!="6LL0KJuqHBVz33WK"
            || !string.Equals(values.GetValueOrDefault("entry"),"Endfield.exe",StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(values.GetValueOrDefault("version", ""),@"^\d+\.\d+\.\d+(?:[._-]\w+)?$")
            || !Regex.IsMatch(values.GetValueOrDefault("entry_md5", ""),"^[0-9a-fA-F]{32}$")) return null;
        var channel=(values.GetValueOrDefault("channel"),values.GetValueOrDefault("sub_channel")) switch
        {
            ("1","1")=>EndfieldChannel.Official,("2","2")=>EndfieldChannel.Bilibili,_=>EndfieldChannel.Unknown
        };
        if (channel==EndfieldChannel.Unknown) return null;
        await using var stream=new FileStream(entry,FileMode.Open,FileAccess.Read,FileShare.Read);
        var hash=Convert.ToHexString(await MD5.HashDataAsync(stream,token));
        if (!hash.Equals(values["entry_md5"],StringComparison.OrdinalIgnoreCase)) return null;
        return (channel,values["version"]);
    }
}
