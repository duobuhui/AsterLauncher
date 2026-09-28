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
    private StarRailGachaAnalysis _starRailAnalysis = StarRailGachaAnalysis.Empty;
    private bool _settingStarRailAccounts;

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
        StarRailRoleSections.ItemTemplate = StarRailSections.ItemTemplate;
        StarRailOtherSections.ItemTemplate = StarRailSections.ItemTemplate;
    }

    private async void Page_OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        GachaScrollViewer.ChangeView(null, 0, null, true);
    }

    private async Task RefreshAsync()
    {
        var isEndfield = _launcher.CurrentGame?.Id == BuiltInGameIds.Endfield;
        var isStarRail = _launcher.CurrentGame?.Id == BuiltInGameIds.HonkaiStarRail;
        var supportsUigfCapture = _launcher.CurrentGame?.Id is BuiltInGameIds.GenshinImpact
            or BuiltInGameIds.HonkaiStarRail
            or BuiltInGameIds.ZenlessZoneZero;
        CaptureButton.Visibility = isEndfield || supportsUigfCapture ? Visibility.Visible : Visibility.Collapsed;
        CaptureButton.Content = isEndfield ? "增量同步" : "同步记录";
        FullCaptureButton.Visibility = isEndfield ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.Content = isEndfield ? "导入终末地 JSON" : "导入 UIGF v4 JSON";
        ExportButton.Content = isEndfield ? "导出终末地 JSON" : "导出 UIGF v4.2 JSON";
        ExportCsvButton.Visibility = isEndfield ? Visibility.Visible : Visibility.Collapsed;
        EndfieldAnalysisPanel.Visibility = isEndfield ? Visibility.Visible : Visibility.Collapsed;
        EndfieldPityStrip.Visibility = isEndfield ? Visibility.Visible : Visibility.Collapsed;
        StarRailAnalysisPanel.Visibility = isStarRail ? Visibility.Visible : Visibility.Collapsed;
        StarRailPityStrip.Visibility = isStarRail ? Visibility.Visible : Visibility.Collapsed;

        if (isEndfield)
        {
            var endfieldSummary = await _endfieldArchive.GetSummaryAsync();
            var analysis = await _endfieldArchive.GetAnalysisAsync();
            CurrentGameText.Text = _launcher.CurrentGame?.DisplayName ?? "明日方舟：终末地";
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
        if (isStarRail)
        {
            _starRailAnalysis = await _archive.GetStarRailAnalysisAsync();
            CurrentGameText.Text = _launcher.CurrentGame?.DisplayName ?? "崩坏：星穹铁道";
            ToolTipService.SetToolTip(ArchiveSummaryText, _archive.ArchivePath);
            _settingStarRailAccounts = true;
            StarRailAccountSelector.ItemsSource = _starRailAnalysis.Accounts.Select(account => account.AccountLabel).ToArray();
            StarRailAccountSelector.Visibility = _starRailAnalysis.Accounts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            StarRailAccountSelector.SelectedIndex = _starRailAnalysis.Accounts.Count > 0 ? 0 : -1;
            _settingStarRailAccounts = false;
            ShowStarRailAccount(0);
            return;
        }

        StarRailSections.ItemsSource = null;
        StarRailRoleSections.ItemsSource = null;
        StarRailOtherSections.ItemsSource = null;
        var summary = await _archive.GetSummaryAsync();
        CurrentGameText.Text = _launcher.CurrentGame?.DisplayName ?? "未选择游戏";
        ArchiveSummaryText.Text = $"本地档案 · 原神 {summary.GenshinCount} · 星穹铁道 {summary.StarRailCount} · 绝区零 {summary.ZenlessCount} · 共 {summary.TotalCount} 条";
        ToolTipService.SetToolTip(ArchiveSummaryText, _archive.ArchivePath);
    }

    private void StarRailAccountSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingStarRailAccounts)
        {
            ShowStarRailAccount(StarRailAccountSelector.SelectedIndex);
        }
    }

    private void ShowStarRailAccount(int index)
    {
        if (index < 0 || index >= _starRailAnalysis.Accounts.Count)
        {
            StarRailSections.ItemsSource = null;
        StarRailRoleSections.ItemsSource = null;
        StarRailOtherSections.ItemsSource = null;
            StarRailEmptyText.Visibility = Visibility.Visible;
            ArchiveSummaryText.Text = "本地档案 · 0 条";
            UpdateStarRailPity(null, StarRailRolePityText, StarRailRolePityBar, 90);
            UpdateStarRailPity(null, StarRailConePityText, StarRailConePityBar, 80);
            UpdateStarRailPity(null, StarRailStandardPityText, StarRailStandardPityBar, 90);
            return;
        }

        var account = _starRailAnalysis.Accounts[index];
        StarRailSections.ItemsSource = account.Sections;
        StarRailRoleSections.ItemsSource = account.Sections.Where(section => section.Type == "11").ToArray();
        StarRailOtherSections.ItemsSource = account.Sections.Where(section => section.Type != "11").ToArray();
        UpdateStarRailLayout(GachaScrollViewer.ActualWidth, account);
        StarRailEmptyText.Visibility = account.RecordCount > 0 ? Visibility.Collapsed : Visibility.Visible;
        ArchiveSummaryText.Text = $"{account.AccountLabel} · {account.RecordCount} 条 · 五星 {account.Sections.Sum(section => section.FiveStarCount)}";
        UpdateStarRailPity(account.Sections.FirstOrDefault(section => section.Type == "11"),
            StarRailRolePityText, StarRailRolePityBar, 90);
        UpdateStarRailPity(account.Sections.FirstOrDefault(section => section.Type == "12"),
            StarRailConePityText, StarRailConePityBar, 80);
        UpdateStarRailPity(account.Sections.FirstOrDefault(section => section.Type == "1"),
            StarRailStandardPityText, StarRailStandardPityBar, 90);
    }

    private static void UpdateStarRailPity(StarRailWarpSection? section, TextBlock text, ProgressBar bar, int maximum)
    {
        text.Text = section is null ? "—" : $"{section.PityCount} / {maximum}";
        bar.Value = section?.PityPercent ?? 0;
        bar.Foreground = section?.PityCount > maximum - 15
            ? HighCountBrush : (Brush)Application.Current.Resources["PageTitleBrush"];
    }
    private void StarRailAnalysisPanel_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var index = StarRailAccountSelector.SelectedIndex;
        var account = index >= 0 && index < _starRailAnalysis.Accounts.Count
            ? _starRailAnalysis.Accounts[index] : null;
        UpdateStarRailLayout(e.NewSize.Width, account);
    }

    private void GachaScrollViewer_OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdatePoolLayout(e.NewSize.Width);

    private void UpdatePoolLayout(double width)
    {
        // A lone series uses the full width; two columns need room for compact pull rows.
        var useTwoColumns = _poolCardCount > 1 && width - 48 >= 740;
        PoolColumns.Visibility = useTwoColumns ? Visibility.Visible : Visibility.Collapsed;
        PoolItems.Visibility = useTwoColumns ? Visibility.Collapsed : Visibility.Visible;
        if (_launcher.CurrentGame?.Id == BuiltInGameIds.HonkaiStarRail)
        {
            var index = StarRailAccountSelector.SelectedIndex;
            var account = index >= 0 && index < _starRailAnalysis.Accounts.Count
                ? _starRailAnalysis.Accounts[index] : null;
            UpdateStarRailLayout(width, account);
        }
    }

    private void UpdateStarRailLayout(double width, StarRailAccountAnalysis? account)
    {
        var hasRole = account?.Sections.Any(section => section.Type == "11") == true;
        var hasOther = account?.Sections.Any(section => section.Type != "11") == true;
        var useColumns = width >= 650 && hasRole && hasOther;
        StarRailColumns.Visibility = useColumns ? Visibility.Visible : Visibility.Collapsed;
        StarRailSections.Visibility = useColumns ? Visibility.Collapsed : Visibility.Visible;
    }

    private void StarRailFill_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border fill)
        {
            var high = fill.Tag is true;
            fill.Background = high ? HighCountBrush :
                (Brush)Application.Current.Resources["PageTitleBrush"];
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
        await RunAsync(() => _endfieldArchive.CaptureFromGameAsync(
            new Progress<string>(message =>
            {
                ResultInfo.Title = "正在全量同步";
                ResultInfo.Message = message;
                ResultInfo.Severity = InfoBarSeverity.Informational;
                ResultInfo.IsOpen = true;
            }),
            fullRefresh: true));
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
