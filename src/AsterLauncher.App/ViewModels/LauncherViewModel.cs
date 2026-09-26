using System.Collections.ObjectModel;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace AsterLauncher.App.ViewModels;

public sealed class LauncherViewModel : ObservableObject
{
    private readonly IConfigurationStore _configurationStore;
    private readonly GameLaunchOrchestrator _orchestrator;
    private readonly IReadOnlyList<IGameAdapter> _builtInAdapters;
    private readonly ILogger<LauncherViewModel> _logger;
    private readonly DispatcherTimer _runtimeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly List<GameCardViewModel> _allGames = [];
    private LauncherConfiguration _configuration = new();
    private GameCardViewModel? _currentGame;
    private LaunchProfile? _selectedProfile;
    private readonly HashSet<string> _activeLaunchGameIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _isRunning;
    private string _statusText = "正在初始化…";
    private string _scanStatus = "尚未扫描";

    public LauncherViewModel(
        IConfigurationStore configurationStore,
        GameLaunchOrchestrator orchestrator,
        IEnumerable<IGameAdapter> builtInAdapters,
        ILogger<LauncherViewModel> logger)
    {
        _configurationStore = configurationStore;
        _orchestrator = orchestrator;
        _builtInAdapters = builtInAdapters.ToList();
        _logger = logger;
        _runtimeTimer.Tick += RuntimeTimerOnTick;
    }

    public ObservableCollection<GameCardViewModel> Games { get; } = [];

    public ObservableCollection<GameCardViewModel> LibraryGames { get; } = [];

    public ObservableCollection<LaunchProfile> CurrentProfiles { get; } = [];

    public ObservableCollection<LaunchSequenceItem> LaunchSequence { get; } = [];

    public ObservableCollection<CompanionTool> CompanionTools { get; } = [];

    public ObservableCollection<InstallScanResult> LastScanResults { get; } = [];

    public event EventHandler? GamesChanged;

    public event EventHandler? ThemeChanged;

    public GameCardViewModel? CurrentGame
    {
        get => _currentGame;
        private set
        {
            if (SetProperty(ref _currentGame, value))
            {
                OnPropertyChanged(nameof(HasCurrentGame));
                OnPropertyChanged(nameof(IsLaunching));
                OnPropertyChanged(nameof(LaunchButtonText));
                IsRunning = value?.IsRunning == true;
                OnPropertyChanged(nameof(CanLaunch));
                OnPropertyChanged(nameof(CompanionSummary));
                OnPropertyChanged(nameof(CurrentCompanionTools));
            }
        }
    }

    public bool HasCurrentGame => CurrentGame is not null;

    public LaunchProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                _configuration.SelectedProfileId = value?.Id;
                RefreshLaunchSequence();
                OnPropertyChanged(nameof(CompanionSummary));
                OnPropertyChanged(nameof(CanLaunch));
            }
        }
    }

    public bool IsLaunching => CurrentGame is not null && _activeLaunchGameIds.Contains(CurrentGame.Id);

    private void SetLaunching(string gameId, bool launching)
    {
        if (launching ? _activeLaunchGameIds.Add(gameId) : _activeLaunchGameIds.Remove(gameId))
        {
            if (string.Equals(CurrentGame?.Id, gameId, StringComparison.OrdinalIgnoreCase))
            {
                OnPropertyChanged(nameof(IsLaunching));
                OnPropertyChanged(nameof(CanLaunch));
                OnPropertyChanged(nameof(LaunchButtonText));
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(RunningStatusText));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ScanStatus
    {
        get => _scanStatus;
        private set => SetProperty(ref _scanStatus, value);
    }

    public bool CanLaunch => CurrentGame?.IsInstalled == true && SelectedProfile is not null && !IsLaunching;

    public string LaunchButtonText => IsLaunching ? "运行中…" : "启动游戏";

    public string RunningStatusText => IsRunning ? "运行中" : "未运行";

    public string ConfigurationPath => _configurationStore.ConfigurationPath;

    public LauncherThemePreference ThemePreference => _configuration.ThemePreference;

    public CloseButtonBehavior CloseButtonBehavior => _configuration.CloseButtonBehavior;

    public bool NeedsFirstRunGuide => _configuration.FirstRunCompleted == false;

    public string GameDownloadDirectory => _configuration.GameDownloadDirectory ?? string.Empty;

    public string LaunchOrderText => LaunchSequence.Count == 0
        ? "尚未选择方案"
        : string.Join(" → ", LaunchSequence
            .Where(item => item.Step?.IsEnabled != false)
            .Select(item => item.Stage == "游戏退出后" ? $"退出后：{item.Title}" : item.Title));

    public IReadOnlyList<CompanionTool> CurrentCompanionTools => CurrentGame is null
        ? []
        : CompanionTools.Where(tool => tool.GameId is null || tool.GameId == CurrentGame.Id).ToArray();

    public string CompanionSummary
    {
        get
        {
            var names = SelectedProfile?.Steps
                .Where(step => step.IsEnabled && step.Phase is LaunchPhase.BeforeGame or LaunchPhase.AfterGame)
                .Select(step => step.Name)
                .ToArray() ?? [];
            return names.Length == 0 ? "未启用伴随工具" : string.Join(" · ", names);
        }
    }

    public async Task InitializeAsync()
    {
        _configuration = await _configurationStore.LoadAsync();
        _configuration.SchemaVersion = 3;
        _configuration.HiddenGameIds ??= [];
        _configuration.GameOrder ??= [];
        EnsureBuiltInStates();
        RebuildGames();

        CompanionTools.Clear();
        foreach (var tool in _configuration.CompanionTools)
        {
            CompanionTools.Add(tool);
        }

        CurrentGame = Games.FirstOrDefault(game => game.Id == _configuration.SelectedGameId) ?? Games.FirstOrDefault();
        RefreshProfiles();
        StatusText = "就绪";
        await RefreshRunningStateAsync();
        _runtimeTimer.Start();
        await _configurationStore.SaveAsync(_configuration);
    }

    public async Task SelectGameAsync(GameCardViewModel game)
    {
        ArgumentNullException.ThrowIfNull(game);
        CurrentGame = game;
        _configuration.SelectedGameId = game.Id;
        RefreshProfiles();
        StatusText = game.IsRunning ? "游戏运行中" : "就绪";
        await RefreshRunningStateAsync();
        await _configurationStore.SaveAsync(_configuration);
    }

    public async Task ScanSelectedAsync()
    {
        if (CurrentGame is null)
        {
            return;
        }

        ScanStatus = "准备扫描…";
        LastScanResults.Clear();
        var progress = new Progress<ScanObservation>(observation =>
        {
            var label = observation.ResultKind switch
            {
                ScanResultKind.NotFound => "没有找到",
                ScanResultKind.InvalidPath => "路径无效",
                ScanResultKind.AccessDenied => "权限不足",
                ScanResultKind.Error => "扫描异常",
                ScanResultKind.Cancelled => "用户取消",
                ScanResultKind.Found => "已找到",
                _ => "正在检查"
            };
            ScanStatus = $"{label} · {observation.Source} · {observation.Message}";
        });

        var game = CurrentGame;
        var results = await ScanGameAsync(game, progress);
        foreach (var result in results)
        {
            LastScanResults.Add(result);
        }

        var found = results.FirstOrDefault(result => result.Kind == ScanResultKind.Found && result.Installation is not null);
        if (found?.Installation is not null)
        {
            ScanStatus = $"已找到 · {found.Source} · {found.Installation.ExecutablePath}";
            return;
        }

        var lastResult = results.LastOrDefault();
        ScanStatus = lastResult is null
            ? "没有找到可信规则 · 未生成扫描结果"
            : $"{GetScanLabel(lastResult.Kind)} · {lastResult.Source} · {lastResult.Message}";
    }

    public async Task<(int Found, int Total)> ScanBuiltInGamesAsync()
    {
        var games = _allGames.Where(game => !game.Adapter.Definition.IsCustom).ToArray();
        var found = 0;
        foreach (var game in games)
        {
            ScanStatus = $"正在查找 {game.DisplayName}…";
            var results = await ScanGameAsync(game);
            if (results.Any(result => result.Kind == ScanResultKind.Found)) found++;
        }
        ScanStatus = $"自动查找完成：{found}/{games.Length} 款游戏已找到。";
        return (found, games.Length);
    }

    private async Task<IReadOnlyList<InstallScanResult>> ScanGameAsync(
        GameCardViewModel game, IProgress<ScanObservation>? progress = null)
    {
        var results = (await game.Adapter.InstallLocator.ScanAsync(progress)).ToList();
        if (!results.Any(result => result.Kind == ScanResultKind.Found)
            && !string.IsNullOrWhiteSpace(_configuration.GameDownloadDirectory))
        {
            progress?.Report(new ScanObservation("设定的游戏目录", "正在检查设定的游戏目录"));
            var directoryResult = await GameDirectoryInstallScanner.ScanAsync(game.Adapter, _configuration.GameDownloadDirectory);
            results.Add(directoryResult);
            progress?.Report(new ScanObservation(directoryResult.Source, directoryResult.Message, directoryResult.Kind));
        }

        var found = results.FirstOrDefault(result => result.Kind == ScanResultKind.Found && result.Installation is not null);
        if (found?.Installation is not null)
        {
            game.State.ExecutablePath = found.Installation.ExecutablePath;
            UpdateSavedPath(game);
            game.Refresh();
            OnPropertyChanged(nameof(CanLaunch));
            await _configurationStore.SaveAsync(_configuration);
        }
        return results;
    }

    public async Task<string?> SetGameDownloadDirectoryAsync(string directory)
    {
        try
        {
            var input = directory.Trim();
            if (string.IsNullOrWhiteSpace(input) || !Path.IsPathFullyQualified(input))
                return "请选择已经存在的完整文件夹路径。";
            var fullPath = Path.GetFullPath(input);
            if (!Directory.Exists(fullPath))
                return "请选择已经存在的完整文件夹路径。";
            _configuration.GameDownloadDirectory = fullPath;
            OnPropertyChanged(nameof(GameDownloadDirectory));
            await _configurationStore.SaveAsync(_configuration);
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"游戏目录无效：{exception.Message}";
        }
    }

    public async Task CompleteFirstRunGuideAsync()
    {
        if (string.IsNullOrWhiteSpace(GameDownloadDirectory))
            throw new InvalidOperationException("请先设置游戏目录。");
        _configuration.FirstRunCompleted = true;
        OnPropertyChanged(nameof(NeedsFirstRunGuide));
        await _configurationStore.SaveAsync(_configuration);
    }

    public async Task SetCloseButtonBehaviorAsync(CloseButtonBehavior behavior)
    {
        if (_configuration.CloseButtonBehavior == behavior) return;
        _configuration.CloseButtonBehavior = behavior;
        OnPropertyChanged(nameof(CloseButtonBehavior));
        await _configurationStore.SaveAsync(_configuration);
    }

    public async Task<InstallScanResult> SetManualExecutableAsync(string executablePath)
    {
        if (CurrentGame is null)
        {
            return new InstallScanResult(ScanResultKind.Error, "手动选择", "没有选中的游戏。");
        }

        var result = CurrentGame.Adapter.ValidateManualExecutable(executablePath);
        LastScanResults.Clear();
        LastScanResults.Add(result);
        ScanStatus = result.Message;
        if (result.Kind == ScanResultKind.Found && result.Installation is not null)
        {
            CurrentGame.State.ExecutablePath = result.Installation.ExecutablePath;
            UpdateSavedPath(CurrentGame);
            CurrentGame.Refresh();
            RefreshLaunchSequence();
            OnPropertyChanged(nameof(CanLaunch));
            await _configurationStore.SaveAsync(_configuration);
            await RefreshRunningStateAsync();
        }

        return result;
    }

    public void ReportManualSelectionCancelled()
    {
        var result = new InstallScanResult(ScanResultKind.Cancelled, "手动选择", "用户取消了文件选择。");
        LastScanResults.Clear();
        LastScanResults.Add(result);
        ScanStatus = "用户取消 · 手动选择 · 未更改游戏路径";
    }

    public async Task<InstallScanResult> AddCustomGameAsync(
        string displayName,
        string publisher,
        string executablePath,
        string? iconGlyph = null,
        string? artworkPath = null)
    {
        var matchingAdapter = GameLibraryOrder.MatchBuiltInExecutable(_builtInAdapters, executablePath);
        var builtIn = matchingAdapter is null ? null : _allGames.FirstOrDefault(game => game.Id == matchingAdapter.Definition.Id);
        if (builtIn is not null)
        {
            var result = builtIn.Adapter.ValidateManualExecutable(executablePath);
            if (result.Kind != ScanResultKind.Found || result.Installation is null) return result;
            builtIn.State.ExecutablePath = result.Installation.ExecutablePath;
            if (!string.IsNullOrWhiteSpace(artworkPath)) builtIn.State.ArtworkPath = artworkPath;
            UpdateSavedPath(builtIn);
            builtIn.Refresh();
            await RestoreBuiltInGameAsync(builtIn);
            await SelectGameAsync(builtIn);
            return result;
        }

        var state = new GameUserState
        {
            GameId = $"custom-{Guid.NewGuid():N}",
            DisplayNameOverride = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(executablePath) : displayName.Trim(),
            PublisherOverride = string.IsNullOrWhiteSpace(publisher) ? "自定义" : publisher.Trim(),
            IconGlyphOverride = string.IsNullOrWhiteSpace(iconGlyph) ? null : iconGlyph.Trim(),
            ArtworkPath = string.IsNullOrWhiteSpace(artworkPath) ? null : artworkPath,
            ExecutablePath = executablePath,
            IsCustom = true
        };
        var adapter = new AsterLauncher.Infrastructure.CustomGameAdapter(state);
        var validation = adapter.ValidateManualExecutable(executablePath);
        if (validation.Kind != ScanResultKind.Found || validation.Installation is null)
        {
            return validation;
        }

        state.ExecutablePath = validation.Installation.ExecutablePath;
        _configuration.Games.Add(state);
        var profile = new LaunchProfile { GameId = state.GameId, Name = "默认启动", IsDefault = true };
        _configuration.LaunchProfiles.Add(profile);
        var card = new GameCardViewModel(adapter, state);
        _allGames.Add(card);
        _configuration.GameOrder.Add(card.Id);
        RefreshVisibleGames();
        await SelectGameAsync(card);
        return validation;
    }

    public async Task RestoreBuiltInGameAsync(GameCardViewModel game)
    {
        if (game.Adapter.Definition.IsCustom || !_allGames.Contains(game)) return;
        _configuration.HiddenGameIds.RemoveAll(id => string.Equals(id, game.Id, StringComparison.OrdinalIgnoreCase));
        _configuration.GameOrder.RemoveAll(id => string.Equals(id, game.Id, StringComparison.OrdinalIgnoreCase));
        _configuration.GameOrder.Add(game.Id);
        RefreshVisibleGames();
        if (CurrentGame is null)
        {
            CurrentGame = game;
            _configuration.SelectedGameId = game.Id;
            RefreshProfiles();
            await RefreshRunningStateAsync();
        }
        await _configurationStore.SaveAsync(_configuration);
    }

    public async Task RemoveGameAsync(GameCardViewModel game)
    {
        if (!Games.Contains(game)) return;
        if (game.Adapter.Definition.IsCustom)
        {
            _configuration.Games.Remove(game.State);
            _configuration.LaunchProfiles.RemoveAll(profile => profile.GameId == game.Id);
            _allGames.Remove(game);
            _configuration.GameOrder.RemoveAll(id => string.Equals(id, game.Id, StringComparison.OrdinalIgnoreCase));
        }
        else if (!_configuration.HiddenGameIds.Contains(game.Id, StringComparer.OrdinalIgnoreCase))
        {
            _configuration.HiddenGameIds.Add(game.Id);
        }
        RefreshVisibleGames();
        if (ReferenceEquals(CurrentGame, game))
        {
            CurrentGame = Games.FirstOrDefault();
            _configuration.SelectedGameId = CurrentGame?.Id ?? string.Empty;
            RefreshProfiles();
            await RefreshRunningStateAsync();
        }
        await _configurationStore.SaveAsync(_configuration);
    }

    public async Task<bool> MoveGameAsync(GameCardViewModel game, int offset)
    {
        if (!Games.Contains(game)
            || !GameLibraryOrder.MoveWithin(_configuration.GameOrder, game.Id, offset, Games.Select(item => item.Id))) return false;
        RefreshVisibleGames();
        await _configurationStore.SaveAsync(_configuration);
        return true;
    }

    public async Task<bool> MoveLibraryGameAsync(GameCardViewModel game, int offset)
    {
        if (!LibraryGames.Contains(game)
            || !GameLibraryOrder.MoveWithin(_configuration.GameOrder, game.Id, offset, LibraryGames.Select(item => item.Id))) return false;
        RefreshVisibleGames();
        await _configurationStore.SaveAsync(_configuration);
        return true;
    }

    public async Task PersistDraggedOrderAsync(bool library, IEnumerable<string> displayedOrder)
    {
        var group = library ? LibraryGames : Games;
        var ids = displayedOrder.ToArray();
        if (ids.Length != group.Count || !GameLibraryOrder.ApplyGroupOrder(_configuration.GameOrder, ids)) return;
        RefreshVisibleGames();
        await _configurationStore.SaveAsync(_configuration);
    }

    public IReadOnlyList<GameCardViewModel> GetUninstalledGames() => Games.Where(game => !game.IsInstalled).ToArray();

    public async Task<int> ClearUninstalledGamesAsync()
    {
        var candidates = GetUninstalledGames();
        if (candidates.Count == 0) return 0;
        foreach (var game in candidates)
        {
            if (game.Adapter.Definition.IsCustom)
            {
                _configuration.Games.Remove(game.State);
                _configuration.LaunchProfiles.RemoveAll(profile => profile.GameId == game.Id);
                _configuration.GameOrder.RemoveAll(id => string.Equals(id, game.Id, StringComparison.OrdinalIgnoreCase));
                _allGames.Remove(game);
            }
            else if (!_configuration.HiddenGameIds.Contains(game.Id, StringComparer.OrdinalIgnoreCase))
            {
                _configuration.HiddenGameIds.Add(game.Id);
            }
        }
        RefreshVisibleGames();
        if (CurrentGame is not null && candidates.Contains(CurrentGame))
        {
            CurrentGame = Games.FirstOrDefault();
            _configuration.SelectedGameId = CurrentGame?.Id ?? string.Empty;
            RefreshProfiles();
            await RefreshRunningStateAsync();
        }
        await _configurationStore.SaveAsync(_configuration);
        return candidates.Count;
    }

    public async Task UpdateGamePresentationAsync(
        GameCardViewModel game,
        string? displayName,
        string? publisher,
        string? iconGlyph,
        string? artworkPath)
    {
        game.State.DisplayNameOverride = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        game.State.PublisherOverride = string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim();
        game.State.IconGlyphOverride = string.IsNullOrWhiteSpace(iconGlyph) ? null : iconGlyph.Trim();
        game.State.ArtworkPath = string.IsNullOrWhiteSpace(artworkPath) ? null : artworkPath;
        game.Refresh();
        await _configurationStore.SaveAsync(_configuration);
        GamesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<LaunchSessionResult?> LaunchSelectedAsync()
    {
        if (IsLaunching)
        {
            StatusText = "游戏正在启动或运行中。";
            return null;
        }
        if (CurrentGame is null)
        {
            StatusText = "请先选择游戏。";
            return null;
        }
        if (!CurrentGame.IsInstalled)
        {
            StatusText = "游戏路径无效，请先手动指定游戏 EXE。";
            _logger.LogWarning("Launch rejected: executable is missing for {GameId}: {Path}", CurrentGame.Id, CurrentGame.State.ExecutablePath);
            return null;
        }
        if (SelectedProfile is null)
        {
            StatusText = "没有可用的启动方案，请先创建启动方案。";
            _logger.LogWarning("Launch rejected: no profile for {GameId}", CurrentGame.Id);
            return null;
        }

        var selectedGame = CurrentGame;
        var selectedProfile = SelectedProfile;
        SetLaunching(selectedGame.Id, true);
        StatusText = "正在执行启动方案…";
        _logger.LogInformation("Launch button accepted for {GameId} with profile {ProfileId}", selectedGame.Id, selectedProfile.Id);
        var request = new GameLaunchRequest(
            selectedGame.Adapter.Definition,
            selectedGame.State.ExecutablePath!,
            selectedProfile,
            selectedGame.Adapter.ProcessDetector);
        var progress = new Progress<LaunchProgress>(item =>
        {
            if (string.Equals(CurrentGame?.Id, selectedGame.Id, StringComparison.OrdinalIgnoreCase))
                StatusText = item.Message;
        });

        try
        {
            var result = await _orchestrator.LaunchAsync(request, progress);
            if (string.Equals(CurrentGame?.Id, selectedGame.Id, StringComparison.OrdinalIgnoreCase))
                StatusText = result.Message;
            if (result.Status == LaunchSessionStatus.Completed)
            {
                selectedGame.State.LastPlayedAt = result.StartedAt;
                selectedGame.State.TotalPlaySeconds += Math.Max(0, result.Duration.TotalSeconds);
                selectedGame.State.PlaySessions.Add(new GamePlaySession
                {
                    StartedAt = result.StartedAt,
                    DurationSeconds = Math.Max(0, result.Duration.TotalSeconds)
                });
                if (selectedGame.State.PlaySessions.Count > 512)
                {
                    selectedGame.State.PlaySessions = selectedGame.State.PlaySessions
                        .OrderByDescending(session => session.StartedAt)
                        .Take(512)
                        .OrderBy(session => session.StartedAt)
                        .ToList();
                }
                selectedGame.Refresh();
                await _configurationStore.SaveAsync(_configuration);
            }
            return result;
        }
        finally
        {
            SetLaunching(selectedGame.Id, false);
            await RefreshRunningStateAsync();
        }
    }

    public async Task AddStepAsync(LaunchStep step)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        SelectedProfile.Steps.Add(step);
        RefreshLaunchSequence();
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(CompanionSummary));
        await _configurationStore.SaveAsync(_configuration);
    }

    public async Task RemoveStepAsync(LaunchStep step)
    {
        if (SelectedProfile?.Steps.Remove(step) == true)
        {
            RefreshLaunchSequence();
            OnPropertyChanged(nameof(SelectedProfile));
            OnPropertyChanged(nameof(CompanionSummary));
            await _configurationStore.SaveAsync(_configuration);
        }
    }

    public async Task<LaunchProfile?> AddProfileAsync()
    {
        if (CurrentGame is null) return null;
        var index = 1;
        while (_configuration.LaunchProfiles.Any(profile => profile.GameId == CurrentGame.Id
            && profile.Name == $"新方案 {index}")) index++;
        var profile = new LaunchProfile
        {
            GameId = CurrentGame.Id,
            Name = $"新方案 {index}",
            IsDefault = false
        };
        _configuration.LaunchProfiles.Add(profile);
        CurrentProfiles.Add(profile);
        SelectedProfile = profile;
        await _configurationStore.SaveAsync(_configuration);
        return profile;
    }

    public Task PersistSelectedProfileAsync() => _configurationStore.SaveAsync(_configuration);

    public async Task<bool> RemoveSelectedProfileAsync()
    {
        if (SelectedProfile is null || CurrentProfiles.Count <= 1) return false;
        var removed = SelectedProfile;
        CurrentProfiles.Remove(removed);
        _configuration.LaunchProfiles.Remove(removed);
        SelectedProfile = CurrentProfiles.FirstOrDefault(profile => profile.IsDefault) ?? CurrentProfiles[0];
        await _configurationStore.SaveAsync(_configuration);
        return true;
    }

    public async Task<bool> RenameSelectedProfileAsync(string name)
    {
        if (SelectedProfile is null || string.IsNullOrWhiteSpace(name)) return false;
        var newName = name.Trim();
        if (CurrentProfiles.Any(profile => !ReferenceEquals(profile, SelectedProfile)
            && string.Equals(profile.Name, newName, StringComparison.OrdinalIgnoreCase))) return false;
        var profile = SelectedProfile;
        profile.Name = newName;
        var index = CurrentProfiles.IndexOf(profile);
        CurrentProfiles.RemoveAt(index);
        CurrentProfiles.Insert(index, profile);
        SelectedProfile = profile;
        await _configurationStore.SaveAsync(_configuration);
        return true;
    }

    private void RefreshLaunchSequence()
    {
        LaunchSequence.Clear();
        if (SelectedProfile is null)
        {
            OnPropertyChanged(nameof(LaunchOrderText));
            return;
        }

        var steps = SelectedProfile.Steps;
        foreach (var step in steps.Where(step => step.Phase == LaunchPhase.BeforeGame)) Add(step, "启动前");
        foreach (var step in steps.Where(step => step.Phase == LaunchPhase.Game)) Add(step, "游戏阶段");
        if (!steps.Any(step => step.IsEnabled && step.TakesOverGameLaunch))
        {
            LaunchSequence.Add(new LaunchSequenceItem(LaunchSequence.Count + 1, "游戏本体", "游戏阶段",
                CurrentGame?.ExecutablePath ?? "尚未指定游戏 EXE", null));
        }
        foreach (var step in steps.Where(step => step.Phase == LaunchPhase.AfterGame)) Add(step, "游戏启动后");
        foreach (var step in steps.Where(step => step.Phase == LaunchPhase.OnGameExit)) Add(step, "游戏退出后");
        OnPropertyChanged(nameof(LaunchOrderText));
        return;

        void Add(LaunchStep step, string stage) => LaunchSequence.Add(new LaunchSequenceItem(
            LaunchSequence.Count + 1,
            step.IsEnabled ? step.Name : $"{step.Name}（已停用）",
            stage,
            string.IsNullOrWhiteSpace(step.Arguments) ? step.ExecutablePath : $"{step.ExecutablePath}  {step.Arguments}",
            step));
    }

    public async Task SetMaaToolPathAsync(string executablePath)
    {
        var tool = CompanionTools.FirstOrDefault(item => item.IsMaaCliPreset);
        if (tool is null)
        {
            tool = MaaCliPresetFactory.CreateTool();
            CompanionTools.Add(tool);
            _configuration.CompanionTools.Add(tool);
        }

        tool.ExecutablePath = executablePath;
        await _configurationStore.SaveAsync(_configuration);
        OnPropertyChanged(nameof(CompanionTools));
    }

    public async Task SetCompanionToolPathAsync(CompanionTool tool, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(tool);
        tool.ExecutablePath = executablePath;
        await _configurationStore.SaveAsync(_configuration);
        OnPropertyChanged(nameof(CompanionTools));
        OnPropertyChanged(nameof(CurrentCompanionTools));
    }

    public async Task SetThemePreferenceAsync(LauncherThemePreference preference)
    {
        if (_configuration.ThemePreference == preference)
        {
            return;
        }

        _configuration.ThemePreference = preference;
        OnPropertyChanged(nameof(ThemePreference));
        await _configurationStore.SaveAsync(_configuration);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureBuiltInStates()
    {
        foreach (var state in _configuration.Games)
        {
            state.PlaySessions ??= [];
        }

        foreach (var adapter in _builtInAdapters)
        {
            var state = _configuration.Games.FirstOrDefault(game => game.GameId == adapter.Definition.Id);
            if (state is null)
            {
                _configuration.Games.Add(new GameUserState { GameId = adapter.Definition.Id });
            }

            if (_configuration.LaunchProfiles.All(profile => profile.GameId != adapter.Definition.Id))
            {
                _configuration.LaunchProfiles.Add(new LaunchProfile
                {
                    GameId = adapter.Definition.Id,
                    Name = "默认启动",
                    IsDefault = true
                });
            }
        }

        foreach (var preset in CompanionToolPresetFactory.CreateDefaults())
        {
            var existing = _configuration.CompanionTools.FirstOrDefault(tool =>
                tool.IsBuiltInPreset && string.Equals(tool.Name, preset.Name, StringComparison.OrdinalIgnoreCase))
                ?? _configuration.CompanionTools.FirstOrDefault(tool =>
                    preset.IsMaaCliPreset && tool.IsMaaCliPreset);
            if (existing is null)
            {
                _configuration.CompanionTools.Add(preset);
                continue;
            }

            existing.IsBuiltInPreset = true;
            existing.GameId ??= preset.GameId;
            existing.Description = preset.Description;
            existing.HomepageUri ??= preset.HomepageUri;
            if (existing.SuggestedExecutableNames.Count == 0)
            {
                existing.SuggestedExecutableNames = preset.SuggestedExecutableNames;
            }
        }
    }

    private void RebuildGames()
    {
        _allGames.Clear();
        foreach (var adapter in _builtInAdapters)
        {
            var state = _configuration.Games.First(game => game.GameId == adapter.Definition.Id);
            var configuredAdapter = adapter is IStateAwareGameAdapter stateAware
                ? stateAware.WithSavedExecutablePath(state.ExecutablePath)
                : adapter;
            _allGames.Add(new GameCardViewModel(configuredAdapter, state));
        }

        foreach (var state in _configuration.Games.Where(game => game.IsCustom))
        {
            _allGames.Add(new GameCardViewModel(new AsterLauncher.Infrastructure.CustomGameAdapter(state), state));
        }

        RefreshVisibleGames();
    }

    private void RefreshVisibleGames()
    {
        _configuration.GameOrder = GameLibraryOrder.OrderedIds(
            _allGames.Select(game => game.Id), _configuration.GameOrder).ToList();
        var visibleIds = GameLibraryOrder.VisibleIds(
            _allGames.Select(game => game.Id), _configuration.HiddenGameIds, _configuration.GameOrder);
        Games.Clear();
        foreach (var id in visibleIds)
        {
            Games.Add(_allGames.First(game => string.Equals(game.Id, id, StringComparison.OrdinalIgnoreCase)));
        }
        LibraryGames.Clear();
        foreach (var id in _configuration.GameOrder.Where(id => _configuration.HiddenGameIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
        {
            if (_allGames.FirstOrDefault(game => game.Id == id && !game.Adapter.Definition.IsCustom) is { } game)
                LibraryGames.Add(game);
        }
        GamesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshProfiles()
    {
        CurrentProfiles.Clear();
        if (CurrentGame is null)
        {
            SelectedProfile = null;
            return;
        }

        foreach (var profile in _configuration.LaunchProfiles.Where(profile => profile.GameId == CurrentGame.Id))
        {
            CurrentProfiles.Add(profile);
        }

        SelectedProfile = CurrentProfiles.FirstOrDefault(profile => profile.Id == _configuration.SelectedProfileId)
            ?? CurrentProfiles.FirstOrDefault(profile => profile.IsDefault)
            ?? CurrentProfiles.FirstOrDefault();
    }

    private async void RuntimeTimerOnTick(object? sender, object e)
    {
        await RefreshRunningStateAsync();
    }

    private async Task RefreshRunningStateAsync()
    {
        foreach (var game in Games)
        {
            try
            {
                var running = game.IsInstalled && await game.Adapter.ProcessDetector.IsRunningAsync(
                    game.Adapter.Definition,
                    game.State.ExecutablePath);
                game.SetRunning(running);
            }
            catch (Exception exception)
            {
                game.SetRunning(false);
                _logger.LogWarning(exception, "Process status refresh failed for {GameId}", game.Id);
            }
        }

        IsRunning = CurrentGame?.IsRunning == true;
    }

    private static string GetScanLabel(ScanResultKind kind) => kind switch
    {
        ScanResultKind.Found => "已找到",
        ScanResultKind.NotFound => "没有找到可信规则",
        ScanResultKind.InvalidPath => "路径存在但 EXE 不存在",
        ScanResultKind.AccessDenied => "权限不足",
        ScanResultKind.Error => "扫描异常",
        ScanResultKind.Cancelled => "用户取消",
        _ => "扫描结果"
    };

    private static void UpdateSavedPath(GameCardViewModel game)
    {
        if (game.Adapter.InstallLocator is IUpdatableInstallLocator locator)
        {
            locator.UpdateSavedPath(game.State.ExecutablePath);
        }
    }
}
