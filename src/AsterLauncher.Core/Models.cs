using System.Text.Json.Serialization;

namespace AsterLauncher.Core;

public enum GameFeature
{
    Launch,
    InstallDiscovery,
    ManualInstall,
    PlayTime,
    Gacha,
    OfficialDownload
}

public enum LauncherThemePreference
{
    System,
    Dark,
    Light,
    TyphonPurple,
    ElysiaPink,
    PaimonWhite
}

public enum CloseButtonBehavior
{
    Exit,
    MinimizeToTray
}

public enum ScanResultKind
{
    Found,
    NotFound,
    InvalidPath,
    AccessDenied,
    Error,
    Cancelled
}

public enum LaunchPhase
{
    BeforeGame,
    Game,
    AfterGame,
    OnGameExit
}

public enum LaunchFailurePolicy
{
    Continue,
    Ask,
    Abort
}

public enum ProcessLaunchStatus
{
    Started,
    SkippedAlreadyRunning,
    UacCancelled,
    TimedOut,
    FileNotFound,
    Failed
}

public enum LaunchSessionStatus
{
    Completed,
    Aborted,
    Cancelled,
    GameNotDetected
}

public sealed record GameDefinition(
    string Id,
    string DisplayName,
    string Publisher,
    string Description,
    string IconGlyph,
    string GradientStart,
    string GradientEnd,
    IReadOnlyList<string> ExecutableNames,
    IReadOnlyList<string> DefaultArguments,
    IReadOnlySet<GameFeature> SupportedFeatures,
    bool IsCustom = false,
    string? OfficialDownloadUri = null,
    string? DownloadDescription = null,
    string? IconAssetPath = null,
    string? HeroAssetPath = null);

public sealed record GameInstallation(string ExecutablePath, string InstallDirectory, string Source);

public sealed record ScanObservation(string Source, string Message, ScanResultKind? ResultKind = null);

public sealed record InstallScanResult(
    ScanResultKind Kind,
    string Source,
    string Message,
    GameInstallation? Installation = null,
    Exception? Exception = null);

public sealed class LaunchProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string GameId { get; set; } = string.Empty;

    public string Name { get; set; } = "默认方案";

    public bool IsDefault { get; set; } = true;

    public string GameArguments { get; set; } = string.Empty;

    public string? GameWorkingDirectory { get; set; }

    public int GameDetectionTimeoutSeconds { get; set; } = 45;

    public List<LaunchStep> Steps { get; set; } = [];
}

public sealed class LaunchStep
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "新步骤";

    public string ExecutablePath { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public string? WorkingDirectory { get; set; }

    public LaunchPhase Phase { get; set; }

    public int DelayMilliseconds { get; set; }

    public bool RunAsAdministrator { get; set; }

    public bool SkipIfAlreadyRunning { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 15;

    public LaunchFailurePolicy FailurePolicy { get; set; } = LaunchFailurePolicy.Abort;

    public bool CloseOnGameExit { get; set; }

    public bool TakesOverGameLaunch { get; set; }

    public bool IsEnabled { get; set; } = true;

    public LaunchStep Clone() => (LaunchStep)MemberwiseClone();
}

public sealed class CompanionTool
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "伴随工具";

    public string Category { get; set; } = "通用";

    public string ExecutablePath { get; set; } = string.Empty;

    public string DefaultArguments { get; set; } = string.Empty;

    public bool IsMaaCliPreset { get; set; }

    public bool IsBuiltInPreset { get; set; }

    public string? GameId { get; set; }

    public string Description { get; set; } = string.Empty;

    public string? HomepageUri { get; set; }

    public List<string> SuggestedExecutableNames { get; set; } = [];

    [JsonIgnore]
    public string SuggestedExecutableText => SuggestedExecutableNames.Count == 0
        ? "选择该工具的主程序 EXE"
        : $"建议文件：{string.Join(" / ", SuggestedExecutableNames)}";
}

public sealed class GameUserState
{
    public string GameId { get; set; } = string.Empty;

    public string? DisplayNameOverride { get; set; }

    public string? PublisherOverride { get; set; }

    public string? IconGlyphOverride { get; set; }

    public string? ArtworkPath { get; set; }

    public string? ExecutablePath { get; set; }

    public DateTimeOffset? LastPlayedAt { get; set; }

    public double TotalPlaySeconds { get; set; }

    public List<GamePlaySession> PlaySessions { get; set; } = [];

    public bool IsCustom { get; set; }

    [JsonIgnore]
    public bool IsInstalled => !string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath);

    public double GetThisWeekPlaySeconds(DateTimeOffset now)
    {
        var localNow = now.ToOffset(now.Offset);
        var daysSinceMonday = ((int)localNow.DayOfWeek + 6) % 7;
        var weekStart = new DateTimeOffset(localNow.Date.AddDays(-daysSinceMonday), localNow.Offset);
        return PlaySessions
            .Where(session => session.StartedAt.ToOffset(localNow.Offset) >= weekStart
                && session.StartedAt.ToOffset(localNow.Offset) <= localNow)
            .Sum(session => Math.Max(0, session.DurationSeconds));
    }

    public double? GetLastSessionPlaySeconds() => PlaySessions
        .OrderByDescending(session => session.StartedAt)
        .Select(session => (double?)Math.Max(0, session.DurationSeconds))
        .FirstOrDefault();
}

public sealed class GamePlaySession
{
    public DateTimeOffset StartedAt { get; set; }

    public double DurationSeconds { get; set; }
}

public sealed class LauncherConfiguration
{
    public int SchemaVersion { get; set; } = 3;

    public LauncherThemePreference ThemePreference { get; set; } = LauncherThemePreference.System;

    public CloseButtonBehavior CloseButtonBehavior { get; set; } = CloseButtonBehavior.Exit;

    // Null denotes a configuration created before the first-run guide existed.
    public bool? FirstRunCompleted { get; set; }

    public string? GameDownloadDirectory { get; set; }

    public string SelectedGameId { get; set; } = BuiltInGameIds.Endfield;

    public Guid? SelectedProfileId { get; set; }

    public List<GameUserState> Games { get; set; } = [];

    // Hidden built-in games keep their paths, play history and launch profiles.
    public List<string> HiddenGameIds { get; set; } = [];

    // Game IDs in user order, including hidden built-ins. New catalog entries are appended on load.
    public List<string> GameOrder { get; set; } = [];

    public List<LaunchProfile> LaunchProfiles { get; set; } = [];

    public List<CompanionTool> CompanionTools { get; set; } = [];
}

public static class BuiltInGameIds
{
    public const string Endfield = "endfield";
    public const string GenshinImpact = "genshin-impact";
    public const string HonkaiImpact3rd = "honkai-impact-3rd";
    public const string HonkaiStarRail = "honkai-star-rail";
    public const string ZenlessZoneZero = "zenless-zone-zero";
    public const string PetitPlanet = "petit-planet";
    public const string Arknights = "arknights";
}

public sealed record OwnedProcessToken(Guid Value, int ProcessId, Guid SessionId, string StepName);

public sealed record ProcessLaunchResult(
    ProcessLaunchStatus Status,
    string Message,
    OwnedProcessToken? Process = null)
{
    public bool IsSuccess => Status is ProcessLaunchStatus.Started or ProcessLaunchStatus.SkippedAlreadyRunning;
}

public sealed record LaunchStepResult(LaunchStep Step, ProcessLaunchResult Result);

public sealed record LaunchProgress(LaunchPhase Phase, string Message, LaunchStep? Step = null);

public sealed record GameLaunchRequest(
    GameDefinition Game,
    string ExecutablePath,
    LaunchProfile Profile,
    IGameProcessDetector ProcessDetector);

public sealed record LaunchSessionResult(
    Guid SessionId,
    LaunchSessionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyList<LaunchStepResult> Steps,
    string Message)
{
    public TimeSpan Duration => EndedAt - StartedAt;
}
