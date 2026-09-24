namespace AsterLauncher.Core;

public static class MaaCliPresetFactory
{
    public static CompanionTool CreateTool() => new()
    {
        Name = "MAA / maa-cli",
        Category = "明日方舟自动化",
        DefaultArguments = "run daily",
        IsMaaCliPreset = true,
        IsBuiltInPreset = true,
        GameId = BuiltInGameIds.Arknights,
        Description = "明日方舟日常任务与自动化",
        HomepageUri = "https://github.com/MaaAssistantArknights/MaaAssistantArknights",
        SuggestedExecutableNames = ["MAA.exe", "maa.exe", "maa-cli.exe"]
    };

    public static LaunchStep CreateAfterGameStep(string executablePath = "") => new()
    {
        Name = "游戏启动后运行 MAA",
        ExecutablePath = executablePath,
        Arguments = "run daily",
        Phase = LaunchPhase.AfterGame,
        FailurePolicy = LaunchFailurePolicy.Ask,
        SkipIfAlreadyRunning = true,
        CloseOnGameExit = false
    };

    public static LaunchStep CreateTakeoverStep(string executablePath = "") => new()
    {
        Name = "由 MAA 接管游戏启动",
        ExecutablePath = executablePath,
        Arguments = "run daily",
        Phase = LaunchPhase.Game,
        FailurePolicy = LaunchFailurePolicy.Abort,
        SkipIfAlreadyRunning = false,
        TakesOverGameLaunch = true
    };
}

public static class CompanionToolPresetFactory
{
    public static IReadOnlyList<CompanionTool> CreateDefaults() =>
    [
        MaaCliPresetFactory.CreateTool(),
        Create(
            "MaaEnd",
            "终末地自动化",
            BuiltInGameIds.Endfield,
            "MaaEnd.exe",
            "终末地自动化",
            "https://github.com/MaaEnd/MaaEnd"),
        Create(
            "三月七小助手",
            "星穹铁道自动化",
            BuiltInGameIds.HonkaiStarRail,
            "March7th Launcher.exe",
            "星穹铁道日常任务与自动化",
            "https://github.com/moesnow/March7thAssistant",
            "March7th Assistant.exe"),
        Create(
            "BetterGI",
            "原神自动化",
            BuiltInGameIds.GenshinImpact,
            "BetterGI.exe",
            "原神辅助工具",
            "https://github.com/babalae/better-genshin-impact")
    ];

    private static CompanionTool Create(
        string name,
        string category,
        string gameId,
        string executableName,
        string description,
        string homepageUri,
        params string[] alternateExecutableNames) => new()
        {
            Name = name,
            Category = category,
            GameId = gameId,
            IsBuiltInPreset = true,
            Description = description,
            HomepageUri = homepageUri,
            SuggestedExecutableNames = [executableName, .. alternateExecutableNames]
        };
}
