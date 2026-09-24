using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

public sealed class EndfieldGameAdapter : IGameAdapter, IStateAwareGameAdapter
{
    private static readonly GameDefinition EndfieldDefinition = new(
        BuiltInGameIds.Endfield,
        "明日方舟：终末地",
        "鹰角网络 / HYPERGRYPH",
        "3D 实时策略 RPG",
        "✦",
        "#6549D5",
        "#16A5A0",
        [EndfieldInstallLocator.VerifiedExecutableName],
        [],
        new HashSet<GameFeature>
        {
            GameFeature.Launch,
            GameFeature.InstallDiscovery,
            GameFeature.ManualInstall,
            GameFeature.PlayTime,
            GameFeature.OfficialDownload
        },
        OfficialDownloadUri: "https://endfield.hypergryph.com/",
        DownloadDescription: "通过鹰角启动器 / GRYPHLINK 安装与更新。",
        IconAssetPath: "Assets/Games/endfield.ico",
        HeroAssetPath: "Assets/Games/endfield-hero.jpg");

    public EndfieldGameAdapter(string? savedExecutablePath = null, IEndfieldInstallEvidenceSource? evidenceSource = null)
    {
        InstallLocator = new EndfieldInstallLocator(savedExecutablePath, evidenceSource);
        ProcessDetector = new PathAwareGameProcessDetector();
    }

    public GameDefinition Definition => EndfieldDefinition;

    public IGameInstallLocator InstallLocator { get; }

    public IGameProcessDetector ProcessDetector { get; }

    public IGameAdapter WithSavedExecutablePath(string? executablePath) => new EndfieldGameAdapter(executablePath);

    public InstallScanResult ValidateManualExecutable(string executablePath)
    {
        var result = ManualExecutableValidator.Validate(executablePath, "手动选择");
        if (result.Kind == ScanResultKind.Found
            && !string.Equals(Path.GetFileName(executablePath), EndfieldInstallLocator.VerifiedExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return new InstallScanResult(
                ScanResultKind.InvalidPath,
                "手动选择",
                $"当前真实安装样本验证的游戏入口为 {EndfieldInstallLocator.VerifiedExecutableName}，所选文件为 {Path.GetFileName(executablePath)}。");
        }

        return result;
    }
}

public sealed class KnownGameAdapter : IGameAdapter, IStateAwareGameAdapter
{
    public KnownGameAdapter(GameDefinition definition, string? savedExecutablePath = null)
    {
        Definition = definition;
        InstallLocator = new ExplicitPathInstallLocator(savedExecutablePath);
        ProcessDetector = new PathAwareGameProcessDetector();
    }

    public GameDefinition Definition { get; }

    public IGameInstallLocator InstallLocator { get; }

    public IGameProcessDetector ProcessDetector { get; }

    public IGameAdapter WithSavedExecutablePath(string? executablePath) => new KnownGameAdapter(Definition, executablePath);

    public InstallScanResult ValidateManualExecutable(string executablePath)
    {
        var result = ManualExecutableValidator.Validate(executablePath, "手动选择");
        if (result.Kind != ScanResultKind.Found || Definition.ExecutableNames.Count == 0)
        {
            return result;
        }

        var fileName = Path.GetFileName(executablePath);
        if (Definition.ExecutableNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            return result;
        }

        return new InstallScanResult(
            ScanResultKind.InvalidPath,
            "手动选择",
            $"所选文件为 {fileName}；当前适配器只接受：{string.Join("、", Definition.ExecutableNames)}。");
    }
}

public static class BuiltInGameCatalog
{
    public static IReadOnlyList<IGameAdapter> CreateAdapters() =>
    [
        new EndfieldGameAdapter(),
        Create(
            BuiltInGameIds.GenshinImpact,
            "原神",
            "米哈游 / miHoYo",
            "开放世界冒险 RPG",
            "原",
            "#315E78",
            "#8DC8C1",
            ["YuanShen.exe", "GenshinImpact.exe"],
            true,
            "https://ys.mihoyo.com/",
            "通过米哈游启动器 / HoYoPlay 安装与更新。",
            "Assets/Games/genshin-impact-icon.png"),
        Create(
            BuiltInGameIds.HonkaiImpact3rd,
            "崩坏3",
            "米哈游 / miHoYo",
            "科幻动作游戏",
            "崩",
            "#263C79",
            "#E04C8A",
            ["BH3.exe"],
            false,
            "https://bh3.mihoyo.com/",
            "通过崩坏3官网或米哈游启动器获取桌面版。",
            "Assets/Games/honkai-impact-3rd-icon.png"),
        Create(
            BuiltInGameIds.HonkaiStarRail,
            "崩坏：星穹铁道",
            "米哈游 / miHoYo",
            "银河冒险策略 RPG",
            "星",
            "#243B78",
            "#B889D8",
            ["StarRail.exe"],
            true,
            "https://sr.mihoyo.com/",
            "通过米哈游启动器 / HoYoPlay 安装与更新。",
            "Assets/Games/honkai-star-rail-icon.png"),
        Create(
            BuiltInGameIds.ZenlessZoneZero,
            "绝区零",
            "米哈游 / miHoYo",
            "都市幻想动作 RPG",
            "绝",
            "#34323A",
            "#E4D74E",
            ["ZenlessZoneZero.exe"],
            true,
            "https://zzz.mihoyo.com/",
            "通过米哈游启动器 / HoYoPlay 安装与更新。",
            "Assets/Games/zenless-zone-zero-icon.png"),
        Create(
            BuiltInGameIds.PetitPlanet,
            "星布谷地",
            "HoYoverse",
            "生活模拟游戏",
            "布",
            "#775D9D",
            "#80C6A6",
            [],
            false,
            "https://planet.hoyoverse.com/zh-cn/home",
            "打开官方页面查看当前测试或下载状态。",
            "Assets/Games/petit-planet-icon.png"),
        new KnownGameAdapter(new GameDefinition(
            BuiltInGameIds.Arknights,
            "明日方舟",
            "鹰角网络 / HYPERGRYPH",
            "策略塔防游戏",
            "舟",
            "#1D2A35",
            "#35A7C6",
            [],
            [],
            new HashSet<GameFeature>
            {
                GameFeature.Launch,
                GameFeature.ManualInstall,
                GameFeature.PlayTime,
                GameFeature.OfficialDownload
            },
            OfficialDownloadUri: "https://ak.hypergryph.com/news/0717",
            DownloadDescription: "官方 PC 版通过鹰角启动器下载；B 服需使用对应渠道启动器。",
            IconAssetPath: "Assets/Games/arknights.ico",
            HeroAssetPath: "Assets/Games/arknights-hero.jpg"))
    ];

    private static IGameAdapter Create(
        string id,
        string name,
        string publisher,
        string description,
        string glyph,
        string gradientStart,
        string gradientEnd,
        IReadOnlyList<string> executableNames,
        bool supportsGacha,
        string downloadUri,
        string downloadDescription,
        string iconAssetPath)
    {
        var features = new HashSet<GameFeature>
        {
            GameFeature.Launch,
            GameFeature.ManualInstall,
            GameFeature.PlayTime,
            GameFeature.OfficialDownload
        };
        if (supportsGacha)
        {
            features.Add(GameFeature.Gacha);
        }
        if (executableNames.Count > 0)
        {
            features.Add(GameFeature.InstallDiscovery);
        }

        return new KnownGameAdapter(new GameDefinition(
            id,
            name,
            publisher,
            description,
            glyph,
            gradientStart,
            gradientEnd,
            executableNames,
            [],
            features,
            OfficialDownloadUri: downloadUri,
            DownloadDescription: downloadDescription,
            IconAssetPath: iconAssetPath,
            HeroAssetPath: $"Assets/Games/{id}.png"));
    }
}

public sealed class CustomGameAdapter : IGameAdapter
{
    public CustomGameAdapter(GameUserState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var displayName = string.IsNullOrWhiteSpace(state.DisplayNameOverride) ? "自定义游戏" : state.DisplayNameOverride;
        var publisher = string.IsNullOrWhiteSpace(state.PublisherOverride) ? "自定义" : state.PublisherOverride;
        var executableNames = string.IsNullOrWhiteSpace(state.ExecutablePath)
            ? []
            : new[] { Path.GetFileName(state.ExecutablePath) };

        Definition = new GameDefinition(
            state.GameId,
            displayName,
            publisher,
            "自定义游戏",
            "◆",
            "#C450A7",
            "#E28A42",
            executableNames,
            [],
            new HashSet<GameFeature>
            {
                GameFeature.Launch,
                GameFeature.ManualInstall,
                GameFeature.PlayTime
            },
            true);

        InstallLocator = new ExplicitPathInstallLocator(state.ExecutablePath);
        ProcessDetector = new PathAwareGameProcessDetector();
    }

    public GameDefinition Definition { get; }

    public IGameInstallLocator InstallLocator { get; }

    public IGameProcessDetector ProcessDetector { get; }

    public InstallScanResult ValidateManualExecutable(string executablePath) =>
        ManualExecutableValidator.Validate(executablePath, "手动选择");
}

internal static class ManualExecutableValidator
{
    public static InstallScanResult Validate(string executablePath, string source)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return new InstallScanResult(ScanResultKind.InvalidPath, source, "没有选择可执行文件。");
            }

            var fullPath = Path.GetFullPath(executablePath);
            if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return new InstallScanResult(ScanResultKind.InvalidPath, source, "请选择 .exe 文件。");
            }

            if (!File.Exists(fullPath))
            {
                return new InstallScanResult(ScanResultKind.InvalidPath, source, $"路径不存在：{fullPath}");
            }

            return new InstallScanResult(
                ScanResultKind.Found,
                source,
                $"已验证：{fullPath}",
                new GameInstallation(fullPath, Path.GetDirectoryName(fullPath)!, source));
        }
        catch (UnauthorizedAccessException exception)
        {
            return new InstallScanResult(ScanResultKind.AccessDenied, source, "没有权限读取所选路径。", Exception: exception);
        }
        catch (Exception exception)
        {
            return new InstallScanResult(ScanResultKind.Error, source, $"验证路径时发生异常：{exception.Message}", Exception: exception);
        }
    }
}
