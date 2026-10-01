using System.Collections.Concurrent;
using System.Text.Json;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

public sealed class GachaClassificationStore(string dataRoot)
{
    private static readonly ConcurrentDictionary<string,SemaphoreSlim> Gates=new(StringComparer.OrdinalIgnoreCase);
    private string FilePath=>SafeGamePath.Resolve(dataRoot,"gacha/up-classifications.json");
    private SemaphoreSlim Gate=>Gates.GetOrAdd(Path.GetFullPath(dataRoot),_=>new(1,1));
    public async Task<IReadOnlyDictionary<string,GachaUpStatus>> ReadAsync()
    {
        await Gate.WaitAsync();try{return await ReadCoreAsync();}finally{Gate.Release();}
    }
    public async Task SetAsync(string key,GachaUpStatus? value)
    {
        if(!ValidKey(key)||value is not null&&value is not (GachaUpStatus.Featured or GachaUpStatus.NonFeatured or GachaUpStatus.Unknown))
            throw new ArgumentException("Invalid classification identity or value.");
        await Gate.WaitAsync();
        try
        {
            var data=await ReadCoreAsync();if(value is null)data.Remove(key);else data[key]=value.Value;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary=SafeGamePath.Resolve(dataRoot,"gacha/up-classifications.json.tmp");
            await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(data));File.Move(temporary,FilePath,true);
        }
        finally{Gate.Release();}
    }
    private async Task<Dictionary<string,GachaUpStatus>> ReadCoreAsync()
    {
        if(!File.Exists(FilePath))return [];
        try
        {
            var data=JsonSerializer.Deserialize<Dictionary<string,GachaUpStatus>>(await File.ReadAllTextAsync(FilePath));
            if(data is null||data.Any(p=>!ValidKey(p.Key)||p.Value is not(GachaUpStatus.Featured or GachaUpStatus.NonFeatured or GachaUpStatus.Unknown)))
                throw new InvalidDataException("UP 校对文件无效。");
            return data;
        }
        catch(JsonException ex){throw new InvalidDataException("UP 校对文件无法解析。",ex);}
    }
    private static bool ValidKey(string key)=>key.Length<=1024&&key.Split('\u001F') is {Length:4} parts&&parts.All(p=>p.Length>0);
}