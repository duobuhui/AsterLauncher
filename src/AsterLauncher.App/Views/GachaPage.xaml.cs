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

    public GachaPage(LauncherViewModel launcher, IUigfArchiveService archive, IEndfieldGachaArchiveService endfieldArchive, IFilePickerService filePicker)
    {
        _launcher = launcher;
        _archive = archive;
        _endfieldArchive = endfieldArchive;
        _filePicker = filePicker;
        InitializeComponent();
        PoolLeftItems.ItemTemplate = PoolItems.ItemTemplate;
        PoolRightItems.ItemTemplate = PoolItems.ItemTemplate;
    }

    private async void Page_OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        GachaScrollViewer.ChangeView(null, 0, null, true);
    }

    private async Task RefreshAsync()
    {
        var isEndfield = _launcher.CurrentGame?.Id == BuiltInGameIds.Endfield;
        var supportsUigfCapture = _launcher.CurrentGame?.Id is BuiltInGameIds.GenshinImpact
            or BuiltInGameIds.HonkaiStarRail
            or BuiltInGameIds.ZenlessZoneZero;
        CaptureButton.Visibility = isEndfield || supportsUigfCapture ? Visibility.Visible : Visibility.Collapsed;
        CaptureButton.Content = isEndfield ? "从游戏同步" : "同步记录";
        ImportButton.Content = isEndfield ? "导入终末地 JSON" : "导入 UIGF v4 JSON";
        ExportButton.Content = isEndfield ? "导出终末地 JSON" : "导出 UIGF v4.2 JSON";
        ExportCsvButton.Visibility = isEndfield ? Visibility.Visible : Visibility.Collapsed;
        EndfieldAnalysisPanel.Visibility = isEndfield ? Visibility.Visible : Visibility.Collapsed;
        EndfieldPityStrip.Visibility = isEndfield ? Visibility.Visible : Visibility.Collapsed;

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
            UpRemainingLabel.Text = analysis.Pity.UpLabel;
            UpRemainingText.Text = analysis.Pity.UpText;
            PoolItems.ItemsSource = analysis.Pools;
            PoolLeftItems.ItemsSource = analysis.Pools.Where((_, index) => index % 2 == 0).ToArray();
            PoolRightItems.ItemsSource = analysis.Pools.Where((_, index) => index % 2 == 1).ToArray();
            SixStarEmptyText.Visibility = analysis.Pools.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        PoolItems.ItemsSource = null;
        PoolLeftItems.ItemsSource = null;
        PoolRightItems.ItemsSource = null;
        var summary = await _archive.GetSummaryAsync();
        CurrentGameText.Text = _launcher.CurrentGame?.DisplayName ?? "未选择游戏";
        ArchiveSummaryText.Text = $"本地档案 · 原神 {summary.GenshinCount} · 星穹铁道 {summary.StarRailCount} · 绝区零 {summary.ZenlessCount} · 共 {summary.TotalCount} 条";
        ToolTipService.SetToolTip(ArchiveSummaryText, _archive.ArchivePath);
    }

    private void GachaScrollViewer_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Each card needs room for its title, count, and compact six-star rows.
        var useTwoColumns = e.NewSize.Width - 48 >= 740;
        PoolColumns.Visibility = useTwoColumns ? Visibility.Visible : Visibility.Collapsed;
        PoolItems.Visibility = useTwoColumns ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SixStarBar_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ProgressBar { Tag: true } bar)
        {
            bar.Foreground = HighCountBrush;
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
        try
        {
            var result = await operation();
            ShowResult(result);
            await RefreshAsync();
        }
        finally
        {
            BusyRing.IsActive = false;
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
