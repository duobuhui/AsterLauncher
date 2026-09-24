using System.Collections.ObjectModel;
using System.ComponentModel;
using AsterLauncher.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace AsterLauncher.App.ViewModels;

public sealed class GameLibraryViewModel : ObservableObject
{
    private static readonly Brush Accent = CreateBrush(137, 117, 255);
    private static readonly Brush Success = CreateBrush(85, 214, 160);
    private static readonly Brush Warning = CreateBrush(244, 199, 104);
    private static readonly Brush Neutral = CreateBrush(160, 169, 191);

    private readonly LauncherViewModel _launcher;
    private readonly IUigfArchiveService _uigfArchive;
    private readonly HashSet<GameCardViewModel> _observedGames = [];
    private string _searchText = string.Empty;
    private string _selectedPublisher = "全部发行商";
    private bool _installedOnly;
    private bool _runningOnly;
    private UigfArchiveSummary _gachaSummary = new("v4.2", 0, 0, 0, null);

    public GameLibraryViewModel(LauncherViewModel launcher, IUigfArchiveService uigfArchive)
    {
        _launcher = launcher;
        _uigfArchive = uigfArchive;
        _launcher.GamesChanged += (_, _) => Refresh();
        _launcher.PropertyChanged += LauncherOnPropertyChanged;
    }

    public ObservableCollection<GameCardViewModel> FilteredGames { get; } = [];

    public ObservableCollection<string> Publishers { get; } = [];

    public ObservableCollection<InfoChipViewModel> InfoChips { get; } = [];

    public ObservableCollection<InfoChipViewModel> QuickInfoChips { get; } = [];

    public string FilteredCountText => $"{FilteredGames.Count} 款";

    public LauncherViewModel Launcher => _launcher;

    public GameCardViewModel? CurrentGame => _launcher.CurrentGame;

    public ObservableCollection<LaunchProfile> CurrentProfiles => _launcher.CurrentProfiles;

    public LaunchProfile? SelectedProfile
    {
        get => _launcher.SelectedProfile;
        set
        {
            if (!ReferenceEquals(_launcher.SelectedProfile, value))
            {
                _launcher.SelectedProfile = value;
                OnPropertyChanged();
                RefreshSummary();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                Refresh();
            }
        }
    }

    public string SelectedPublisher
    {
        get => _selectedPublisher;
        set
        {
            if (SetProperty(ref _selectedPublisher, value))
            {
                Refresh();
            }
        }
    }

    public bool InstalledOnly
    {
        get => _installedOnly;
        set
        {
            if (SetProperty(ref _installedOnly, value))
            {
                Refresh();
            }
        }
    }

    public bool RunningOnly
    {
        get => _runningOnly;
        set
        {
            if (SetProperty(ref _runningOnly, value))
            {
                Refresh();
            }
        }
    }

    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(SearchText)
        || SelectedPublisher != "全部发行商"
        || InstalledOnly
        || RunningOnly;

    public string ProfileSummary => _launcher.LaunchOrderText;

    public string PathText => CurrentGame?.ExecutablePath ?? "尚未指定";

    public string VersionText => CurrentGame?.VersionText ?? "暂无数据";

    public string PublisherText => CurrentGame?.Publisher ?? "暂无数据";

    public string LastPlayedText => CurrentGame?.LastPlayedText ?? "暂无数据";

    public string LastSessionPlayTimeText => CurrentGame?.LastSessionPlayTimeText ?? "暂无数据";

    public string ThisWeekPlayTimeText => CurrentGame?.ThisWeekPlayTimeText ?? "暂无数据";

    public string TotalPlayTimeText => CurrentGame?.TotalPlayTimeText ?? "暂无数据";

    public string RecentLaunchText => $"最近启动 · {LastPlayedText}";

    public bool CanLaunch => _launcher.CanLaunch;

    public string LaunchButtonText => _launcher.LaunchButtonText;

    public string LaunchStatusText => _launcher.StatusText;

    public string ScanStatusText => string.IsNullOrWhiteSpace(_launcher.ScanStatus) ? "暂无数据" : _launcher.ScanStatus;

    public ObservableCollection<InstallScanResult> ScanResults => _launcher.LastScanResults;

    public async Task SelectGameAsync(GameCardViewModel game) => await _launcher.SelectGameAsync(game);

    public Task<LaunchSessionResult?> LaunchAsync() => _launcher.LaunchSelectedAsync();

    public async Task ScanAsync() => await _launcher.ScanSelectedAsync();

    public void ClearSearch() => SearchText = string.Empty;

    public void ClearFilters()
    {
        SelectedPublisher = "全部发行商";
        InstalledOnly = false;
        RunningOnly = false;
    }

    public void Refresh()
    {
        ObserveGames();

        var previousPublisher = SelectedPublisher;
        Publishers.Clear();
        Publishers.Add("全部发行商");
        foreach (var publisher in _launcher.Games.Select(game => game.Publisher).Distinct().OrderBy(value => value))
        {
            Publishers.Add(publisher);
        }

        if (!Publishers.Contains(previousPublisher))
        {
            _selectedPublisher = "全部发行商";
            OnPropertyChanged(nameof(SelectedPublisher));
        }

        var query = _launcher.Games.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(game =>
                game.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || game.Publisher.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        if (SelectedPublisher != "全部发行商")
        {
            query = query.Where(game => game.Publisher == SelectedPublisher);
        }

        if (InstalledOnly)
        {
            query = query.Where(game => game.IsInstalled);
        }

        if (RunningOnly)
        {
            query = query.Where(game => game.IsRunning);
        }

        FilteredGames.Clear();
        foreach (var game in query)
        {
            FilteredGames.Add(game);
        }

        OnPropertyChanged(nameof(FilteredCountText));
        OnPropertyChanged(nameof(HasActiveFilters));

        RefreshSummary();
        _ = RefreshGachaSummaryAsync();
    }

    private void LauncherOnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(LauncherViewModel.CurrentGame))
        {
            OnPropertyChanged(nameof(CurrentGame));
            OnPropertyChanged(nameof(SelectedProfile));
            RefreshSummary();
            return;
        }

        if (args.PropertyName is nameof(LauncherViewModel.SelectedProfile)
            or nameof(LauncherViewModel.LaunchOrderText)
            or nameof(LauncherViewModel.CompanionSummary)
            or nameof(LauncherViewModel.IsRunning)
            or nameof(LauncherViewModel.IsLaunching)
            or nameof(LauncherViewModel.CanLaunch)
            or nameof(LauncherViewModel.LaunchButtonText)
            or nameof(LauncherViewModel.StatusText)
            or nameof(LauncherViewModel.ScanStatus))
        {
            RefreshSummary();
        }
    }

    private void ObservedGameOnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (ReferenceEquals(sender, CurrentGame)
            || args.PropertyName is nameof(GameCardViewModel.IsInstalled) or nameof(GameCardViewModel.IsRunning))
        {
            RefreshSummary();
        }
    }

    private void ObserveGames()
    {
        foreach (var game in _launcher.Games.Where(game => _observedGames.Add(game)))
        {
            game.PropertyChanged += ObservedGameOnPropertyChanged;
        }
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(CurrentGame));
        OnPropertyChanged(nameof(CurrentProfiles));
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(ProfileSummary));
        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(PublisherText));
        OnPropertyChanged(nameof(LastPlayedText));
        OnPropertyChanged(nameof(LastSessionPlayTimeText));
        OnPropertyChanged(nameof(ThisWeekPlayTimeText));
        OnPropertyChanged(nameof(TotalPlayTimeText));
        OnPropertyChanged(nameof(RecentLaunchText));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(LaunchButtonText));
        OnPropertyChanged(nameof(LaunchStatusText));
        OnPropertyChanged(nameof(ScanStatusText));

        var game = CurrentGame;
        var enabledSteps = SelectedProfile?.Steps.Count(step => step.IsEnabled && step.Phase is LaunchPhase.BeforeGame or LaunchPhase.AfterGame) ?? 0;
        var maaEnabled = SelectedProfile?.Steps.Any(step => step.IsEnabled && IsMaaStep(step)) == true;
        var installedText = game is null ? "暂无数据" : game.IsInstalled ? "已安装" : "未安装";

        InfoChips.Clear();
        InfoChips.Add(new InfoChipViewModel(
            "当前状态",
            game?.RunningStatusText ?? "暂无数据",
            game?.IsRunning == true ? "●" : "○",
            "查看游戏进程状态",
            game?.IsRunning == true ? "已检测到该游戏进程正在运行。" : "当前未检测到该游戏进程。",
            game?.IsRunning == true ? Success : Neutral));
        InfoChips.Add(new InfoChipViewModel("游戏版本", VersionText, "⌁", "查看可执行文件版本", $"从当前游戏可执行文件读取的版本：{VersionText}", Accent));
        InfoChips.Add(new InfoChipViewModel("安装状态", installedText, "✓", "查看安装状态", game?.ExecutablePath ?? "暂无数据", game?.IsInstalled == true ? Success : Warning));
        InfoChips.Add(new InfoChipViewModel("累计游玩", game?.TotalPlayTimeText ?? "暂无数据", "◷", "查看累计游玩时间", $"最近启动：{LastPlayedText}", Accent));
        InfoChips.Add(new InfoChipViewModel("伴随工具", enabledSteps == 0 ? "未启用" : $"{enabledSteps} 个", "⊕", "查看当前方案中的伴随工具", ProfileSummary, enabledSteps > 0 ? Success : Neutral));
        InfoChips.Add(new InfoChipViewModel("MAA", maaEnabled ? "已启用" : "未启用", "M", "查看 MAA CLI 状态", maaEnabled ? "当前启动方案包含 MAA CLI 步骤。" : "当前启动方案未包含 MAA CLI 步骤。", maaEnabled ? Success : Neutral));
        var gachaCount = game?.Id switch
        {
            BuiltInGameIds.GenshinImpact => _gachaSummary.GenshinCount,
            BuiltInGameIds.HonkaiStarRail => _gachaSummary.StarRailCount,
            BuiltInGameIds.ZenlessZoneZero => _gachaSummary.ZenlessCount,
            _ => -1
        };
        var gachaText = gachaCount < 0 ? "不适用" : gachaCount == 0 ? "暂无数据" : $"{gachaCount} 条";
        InfoChips.Add(new InfoChipViewModel("抽卡记录", gachaText, "✦", "查看本地 UIGF 档案", gachaCount < 0 ? "当前游戏不使用米哈游 UIGF 格式。" : $"本地 UIGF v4.2 档案包含 {gachaCount} 条当前游戏记录。", gachaCount > 0 ? Success : Neutral));
        InfoChips.Add(new InfoChipViewModel("自动查找", ScanStatusText, "⌕", "查看最近一次自动查找结果", ScanStatusText, Neutral));

        QuickInfoChips.Clear();
        foreach (var index in new[] { 0, 1, 4, 7 })
        {
            QuickInfoChips.Add(InfoChips[index]);
        }
    }

    private bool IsMaaStep(LaunchStep step)
    {
        if (step.Name.Contains("MAA", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _launcher.CompanionTools.Any(tool =>
            tool.IsMaaCliPreset
            && !string.IsNullOrWhiteSpace(tool.ExecutablePath)
            && string.Equals(tool.ExecutablePath, step.ExecutablePath, StringComparison.OrdinalIgnoreCase));
    }

    private static Brush CreateBrush(byte red, byte green, byte blue) =>
        new SolidColorBrush(ColorHelper.FromArgb(255, red, green, blue));

    private async Task RefreshGachaSummaryAsync()
    {
        try
        {
            _gachaSummary = await _uigfArchive.GetSummaryAsync();
            RefreshSummary();
        }
        catch
        {
            // Detailed storage errors are shown on the gacha page.
        }
    }
}
