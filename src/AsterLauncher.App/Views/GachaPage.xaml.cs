using AsterLauncher.App.Services;
using AsterLauncher.App.ViewModels;
using AsterLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AsterLauncher.App.Views;

public sealed partial class GachaPage : Page
{
    private static readonly SolidColorBrush HighCountBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(255, 143, 29, 43));

    private readonly LauncherViewModel _launcher;
    private readonly IUigfArchiveService _archive;
    private readonly IEndfieldGachaArchiveService _endfieldArchive;
    private readonly IFilePickerService _filePicker;
    private int _poolCardCount;
    private UigfGachaAnalysis _analysis = UigfGachaAnalysis.Empty;
    private bool _settingAccounts;
    private int _refreshRevision;

    public GachaPage(LauncherViewModel launcher, IUigfArchiveService archive, IEndfieldGachaArchiveService endfieldArchive, IFilePickerService filePicker)
    {
        _launcher = launcher;
        _archive = archive;
        _endfieldArchive = endfieldArchive;
        _filePicker = filePicker;
        InitializeComponent();
        HistoryPoolItems.ItemTemplate = PoolItems.ItemTemplate;
        PoolLeftItems.ItemTemplate = PoolItems.ItemTemplate;
        PoolRightItems.ItemTemplate = PoolItems.ItemTemplate;
        UigfRoleSections.ItemTemplate = UigfSections.ItemTemplate;
        UigfOtherSections.ItemTemplate = UigfSections.ItemTemplate;
    }

    private async void Page_OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        GachaScrollViewer.ChangeView(null, 0, null, true);
    }

    private async Task RefreshAsync()
    {
        var revision = ++_refreshRevision;
        var gameId = _launcher.CurrentGame?.Id ?? "";
        var gameName = _launcher.CurrentGame?.DisplayName ?? "未选择游戏";
        var previousUid = UigfAccountSelector.SelectedIndex is var selected && selected >= 0 && selected < _analysis.Accounts.Count
            ? _analysis.Accounts[selected].Uid : null;
        bool IsCurrent() => revision == _refreshRevision && _launcher.CurrentGame?.Id == gameId;
        var isEndfield = gameId == BuiltInGameIds.Endfield;

        var supportsUigfCapture = gameId is BuiltInGameIds.GenshinImpact
            or BuiltInGameIds.HonkaiStarRail
            or BuiltInGameIds.ZenlessZoneZero;
        CaptureButton.Visibility = isEndfield || supportsUigfCapture ? Visibility.Visible : Visibility.Collapsed;
        CaptureButton.Content = "增量同步";
        FullCaptureButton.Visibility = isEndfield || supportsUigfCapture ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.Content = isEndfield ? "导入终末地 JSON" : "导入 UIGF v4 JSON";
        ExportButton.Content = isEndfield ? "导出终末地 JSON" : "导出 UIGF v4.2 JSON";
        ExportCsvButton.Visibility = isEndfield ? Visibility.Visible : Visibility.Collapsed;
        EndfieldAnalysisPanel.Visibility = isEndfield ? Visibility.Visible : Visibility.Collapsed;
        EndfieldPityStrip.Visibility = isEndfield ? Visibility.Visible : Visibility.Collapsed;
        UigfAnalysisPanel.Visibility = supportsUigfCapture ? Visibility.Visible : Visibility.Collapsed;
        UigfPityStrip.Visibility = supportsUigfCapture ? Visibility.Visible : Visibility.Collapsed;

        if (isEndfield)
        {
            var endfieldSummary = await _endfieldArchive.GetSummaryAsync();
            var analysis = await _endfieldArchive.GetAnalysisAsync();
            if (!IsCurrent()) return;
            CurrentGameText.Text = gameName;
            ArchiveSummaryText.Text = $"{endfieldSummary.TotalCount} 条 · {analysis.CharacterDrawCount} 抽 · 六星 {analysis.SixStarCount}（免费 {analysis.FreeSixStarCount}）";
            ToolTipService.SetToolTip(ArchiveSummaryText, _endfieldArchive.ArchivePath);
            StandardPityText.Text = analysis.Pity.StandardText;
            LimitedPityLabel.Text = analysis.Pity.LimitedLabel;
            LimitedPityText.Text = analysis.Pity.LimitedText;
            LimitedPityBar.Value = analysis.Pity.LimitedPercent;
            LimitedPityBar.Foreground = analysis.Pity.LimitedOver65 ? HighCountBrush :
                (Brush)Application.Current.Resources["PageTitleBrush"];
            UpRemainingLabel.Text = analysis.Pity.UpLabel;
            UpRemainingText.Text = analysis.Pity.UpText;
            UpPityBar.Visibility = analysis.Pity.UpIsRefactor && !analysis.Pity.UpRecorded && analysis.Pity.UpOperatorKnown
                ? Visibility.Visible : Visibility.Collapsed;
            UpPityBar.Value = analysis.Pity.UpPercent;
            var historyPools = analysis.Pools.Where(pool => pool.PreviousPhaseCount > 0).ToArray();
            var otherPools = analysis.Pools.Where(pool => pool.PreviousPhaseCount == 0).ToArray();
            HistoryPoolItems.ItemsSource = historyPools;
            HistoryPoolItems.Visibility = historyPools.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _poolCardCount = otherPools.Length;
            PoolItems.ItemsSource = otherPools;
            PoolLeftItems.ItemsSource = otherPools.Where((_, index) => index % 2 == 0).ToArray();
            PoolRightItems.ItemsSource = otherPools.Where((_, index) => index % 2 == 1).ToArray();
            SixStarEmptyText.Visibility = analysis.Pools.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdatePoolLayout(GachaScrollViewer.ActualWidth);
            return;
        }

        _poolCardCount = 0;
        HistoryPoolItems.ItemsSource = null;
        HistoryPoolItems.Visibility = Visibility.Collapsed;
        PoolItems.ItemsSource = null;
        PoolLeftItems.ItemsSource = null;
        PoolRightItems.ItemsSource = null;
        if (supportsUigfCapture)
        {
            var analysis = await _archive.GetAnalysisAsync(gameId);
            if (!IsCurrent()) return;
            _analysis = analysis;
            CurrentGameText.Text = gameName;
            UigfRulesText.Text = gameId switch
            {
                BuiltInGameIds.GenshinImpact => "角色活动祈愿 301 / 400 共用计数，武器、常驻和集录分别统计；不推断定轨或 UP。",
                BuiltInGameIds.ZenlessZoneZero => "各调频类别分别统计 S / A / B 级；特殊类别保底规则待核对，不推断 UP。",
                _ => "同类跃迁共用五星计数；特殊类别保留原始记录，不推断 UP。"
            };
            UigfEmptyText.Text = $"暂无{gameName}记录；同步或导入 UIGF 后显示。";
            _settingAccounts = true;
            UigfAccountSelector.ItemsSource = analysis.Accounts.Select(account => account.AccountLabel).ToArray();
            UigfAccountSelector.Visibility = analysis.Accounts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            var index = previousUid is null ? -1 : analysis.Accounts.ToList().FindIndex(account => account.Uid == previousUid);
            UigfAccountSelector.SelectedIndex = analysis.Accounts.Count == 0 ? -1 : Math.Max(0, index);
            _settingAccounts = false;
            ShowUigfAccount(UigfAccountSelector.SelectedIndex);
            return;
        }

        UigfSections.ItemsSource = null;
        UigfRoleSections.ItemsSource = null;
        UigfOtherSections.ItemsSource = null;
        var summary = await _archive.GetSummaryAsync();
        if (!IsCurrent()) return;
        CurrentGameText.Text = gameName;
        ArchiveSummaryText.Text = $"本地档案 · 原神 {summary.GenshinCount} · 星穹铁道 {summary.StarRailCount} · 绝区零 {summary.ZenlessCount} · 共 {summary.TotalCount} 条";
    }

    private void UigfAccountSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingAccounts) ShowUigfAccount(UigfAccountSelector.SelectedIndex);
    }

    private void ShowUigfAccount(int index)
    {
        var account = index >= 0 && index < _analysis.Accounts.Count ? _analysis.Accounts[index] : null;
        UigfSections.ItemsSource = account?.Sections;
        UigfRoleSections.ItemsSource = account?.Sections.Where(section => section.IsFeaturedCharacter).ToArray();
        UigfOtherSections.ItemsSource = account?.Sections.Where(section => !section.IsFeaturedCharacter).ToArray();
        UpdateUigfLayout(GachaScrollViewer.ActualWidth, account);
        UigfEmptyText.Visibility = account?.RecordCount > 0 ? Visibility.Collapsed : Visibility.Visible;
        ArchiveSummaryText.Text = account is null ? "本地档案 · 0 条"
            : $"{account.AccountLabel} · {account.RecordCount} 条 · {_analysis.HighRarityLabel} {account.Sections.Sum(section => section.HighRarityCount)}";
        var displayed = _analysis.Categories.Where(category => category.PityMaximum is not null &&
            (!IsOptionalCategory(category.Type) || account?.Sections.Any(section => section.Type == category.Type) == true)).ToList();
        displayed.AddRange(account?.Sections.Where(section => displayed.All(c => c.Type != section.Type))
            .Select(section => new UigfPoolDefinition(section.Type, section.Title, section.PityMaximum)) ?? []);
        UigfPityStrip.ItemsSource = displayed.Select(category =>
        {
            var section = account?.Sections.FirstOrDefault(section => section.Type == category.Type);
            return new UigfPityIndicator(category.Title, section?.PityCount, category.PityMaximum, section?.HighRarityCount > 0);
        }).ToArray();
    }

    private bool IsOptionalCategory(string type) => _analysis.GameId switch
    {
        BuiltInGameIds.GenshinImpact => type == "500",
        BuiltInGameIds.ZenlessZoneZero => type is "102" or "103",
        _ => false
    };

    private void UigfAnalysisPanel_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var index = UigfAccountSelector.SelectedIndex;
        var account = index >= 0 && index < _analysis.Accounts.Count ? _analysis.Accounts[index] : null;
        UpdateUigfLayout(e.NewSize.Width, account);
    }

    private void GachaScrollViewer_OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdatePoolLayout(e.NewSize.Width);

    private void UpdatePoolLayout(double width)
    {
        var useTwoColumns = _poolCardCount > 1 && width - 48 >= 740;
        PoolColumns.Visibility = useTwoColumns ? Visibility.Visible : Visibility.Collapsed;
        PoolItems.Visibility = useTwoColumns ? Visibility.Collapsed : Visibility.Visible;
        var index = UigfAccountSelector.SelectedIndex;
        var account = index >= 0 && index < _analysis.Accounts.Count ? _analysis.Accounts[index] : null;
        UpdateUigfLayout(width, account);
    }

    private void UpdateUigfLayout(double width, UigfAccountAnalysis? account)
    {
        var hasRole = account?.Sections.Any(section => section.IsFeaturedCharacter) == true;
        var hasOther = account?.Sections.Any(section => !section.IsFeaturedCharacter) == true;
        var useColumns = width >= 650 && hasRole && hasOther;
        UigfColumns.Visibility = useColumns ? Visibility.Visible : Visibility.Collapsed;
        UigfSections.Visibility = useColumns ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UigfFill_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border fill)
        {
            var high = fill.Tag is true;
            fill.Background = high ? HighCountBrush : (Brush)Application.Current.Resources["PageTitleBrush"];
            fill.Opacity = high ? 0.84 : 0.62;
        }
    }
    private void SixStarBar_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ProgressBar bar)
        {
            bar.Foreground = bar.Tag is true ? HighCountBrush :
                (Brush)Application.Current.Resources["PageTitleBrush"];
        }
    }

    private async void Capture_OnClick(object sender, RoutedEventArgs e)
    {
        var game = _launcher.CurrentGame;
        if (game is null)
        {
            ShowResult(new GachaImportResult(false, 0, "请先选择游戏。"));
            return;
        }

        if (game.Id == BuiltInGameIds.Endfield)
        {
            await RunAsync(() => _endfieldArchive.CaptureFromGameAsync(
                new Progress<string>(message =>
                {
                    ResultInfo.Title = "正在同步";
                    ResultInfo.Message = message;
                    ResultInfo.Severity = InfoBarSeverity.Informational;
                    ResultInfo.IsOpen = true;
                })));
            return;
        }

        await RunAsync(() => _archive.CaptureFromGameAsync(
            game.Id,
            game.State.ExecutablePath ?? string.Empty,
            new Progress<string>(message =>
            {
                ResultInfo.Title = "正在同步";
                ResultInfo.Message = message;
                ResultInfo.Severity = InfoBarSeverity.Informational;
                ResultInfo.IsOpen = true;
            })));
    }

    private async void FullCapture_OnClick(object sender, RoutedEventArgs e)
    {
        var game = _launcher.CurrentGame;
        if (game is null) return;
        var progress = new Progress<string>(message =>
        {
            ResultInfo.Title = "正在全量同步";
            ResultInfo.Message = message;
            ResultInfo.Severity = InfoBarSeverity.Informational;
            ResultInfo.IsOpen = true;
        });
        await RunAsync(() => game.Id == BuiltInGameIds.Endfield
            ? _endfieldArchive.CaptureFromGameAsync(progress, fullRefresh: true)
            : _archive.CaptureFromGameAsync(
                game.Id, game.State.ExecutablePath ?? string.Empty, progress, fullRefresh: true));
    }
    private async void Import_OnClick(object sender, RoutedEventArgs e)
    {
        var path = await _filePicker.PickJsonAsync(App.MainWindow);
        if (path is not null)
        {
            await RunAsync(() => _launcher.CurrentGame?.Id == BuiltInGameIds.Endfield
                ? _endfieldArchive.ImportAsync(path)
                : _archive.ImportAsync(path));
        }
    }

    private async void Export_OnClick(object sender, RoutedEventArgs e)
    {
        var path = await _filePicker.PickJsonSaveAsync(App.MainWindow, $"AsterLauncher-UIGF-{DateTime.Now:yyyyMMdd}");
        if (path is not null)
        {
            await RunAsync(() => _launcher.CurrentGame?.Id == BuiltInGameIds.Endfield
                ? _endfieldArchive.ExportJsonAsync(path)
                : _archive.ExportAsync(path));
        }
    }

    private async void ExportCsv_OnClick(object sender, RoutedEventArgs e)
    {
        var path = await _filePicker.PickCsvSaveAsync(App.MainWindow, $"AsterLauncher-Endfield-{DateTime.Now:yyyyMMdd}");
        if (path is not null)
        {
            await RunAsync(() => _endfieldArchive.ExportCsvAsync(path));
        }
    }

    private async Task RunAsync(Func<Task<GachaImportResult>> operation)
    {
        BusyRing.IsActive = true;
        CaptureButton.IsEnabled = false;
        FullCaptureButton.IsEnabled = false;
        try
        {
            var result = await operation();
            ShowResult(result);
            await RefreshAsync();
        }
        finally
        {
            BusyRing.IsActive = false;
            CaptureButton.IsEnabled = true;
            FullCaptureButton.IsEnabled = true;
        }
    }

    private void ShowResult(GachaImportResult result)
    {
        ResultInfo.Title = result.Success ? "操作完成" : "操作未完成";
        ResultInfo.Message = result.Message;
        ResultInfo.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ResultInfo.IsOpen = true;
    }
}
