using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

public static class InstalledServerDeclaration
{
    /// <summary>Accept owner confirmation when metadata is absent; reject known contradictory channel metadata. No game writes.</summary>
    public static void ValidateHoYo(string root,string executable,HoYoChannel channel)
    {
        if(channel is not (HoYoChannel.Official or HoYoChannel.Bilibili))throw new ArgumentException("请选择服务器。");
        if(!File.Exists(SafeGamePath.Resolve(root,executable)))throw new FileNotFoundException("游戏 EXE 不存在。");
        var config=SafeGamePath.Resolve(root,"config.ini");if(!File.Exists(config))return;
        if(new FileInfo(config).Length>65536)throw new InvalidDataException("游戏配置文件过大。");
        var values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var line in File.ReadLines(config))
        {
            var parts=line.Split('=',2);if(parts.Length!=2)continue;
            var key=parts[0].Trim();if(key is not ("channel" or "sub_channel" or "cps" or "game_biz"))continue;
            if(!values.TryAdd(key,parts[1].Trim()))throw new InvalidDataException("游戏配置含重复渠道信息。");
        }
        if(values.GetValueOrDefault("game_biz","").Contains("global",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("此目录属于国际服，不能登记为国服。");
        var known=values.GetValueOrDefault("channel") switch{"1"=>HoYoChannel.Official,"14"=>HoYoChannel.Bilibili,_=>HoYoChannel.Unknown};
        var cps=values.GetValueOrDefault("cps") switch{"mihoyo" or "hyp_mihoyo"=>HoYoChannel.Official,"bilibili"=>HoYoChannel.Bilibili,_=>HoYoChannel.Unknown};
        if(values.GetValueOrDefault("cps") == "hyp_mihoyo" && ReadVerifiedHoYoChannel(root, executable) != HoYoChannel.Official)
            throw new InvalidOperationException("米哈游启动器的渠道信息不完整或冲突，请检查原安装。");
        if(known!=HoYoChannel.Unknown && known!=channel || cps!=HoYoChannel.Unknown && cps!=channel)
            throw new InvalidOperationException("游戏安装配置属于另一服务器，请保留原目录。");
    }

    /// <summary>Automatic discovery requires a complete, consistent game and channel identity.</summary>
    public static HoYoChannel ReadVerifiedHoYoChannel(string root, string executable)
    {
        var gameBiz = Path.GetFileName(executable).ToLowerInvariant() switch
        {
            "yuanshen.exe" => "hk4e_cn",
            "starrail.exe" => "hkrpg_cn",
            "zenlesszonezero.exe" => "nap_cn",
            _ => null
        };
        if (gameBiz is null || !File.Exists(SafeGamePath.Resolve(root, executable))) return HoYoChannel.Unknown;
        var config = SafeGamePath.Resolve(root, "config.ini");
        if (!File.Exists(config)) return HoYoChannel.Unknown;
        if (new FileInfo(config).Length > 65536) throw new InvalidDataException("游戏配置文件过大。");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(config))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2) continue;
            var key = parts[0].Trim();
            if (!new[] { "channel", "sub_channel", "cps", "game_biz" }.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            if (!values.TryAdd(key, parts[1].Trim())) throw new InvalidDataException("游戏配置含重复渠道信息。");
        }
        if (values.GetValueOrDefault("game_biz") != gameBiz) return HoYoChannel.Unknown;
        return (values.GetValueOrDefault("channel"), values.GetValueOrDefault("sub_channel"), values.GetValueOrDefault("cps")) switch
        {
            ("1", "1", "mihoyo" or "hyp_mihoyo") => HoYoChannel.Official,
            ("14", "0", "bilibili") => HoYoChannel.Bilibili,
            _ => HoYoChannel.Unknown
        };
    }
}
