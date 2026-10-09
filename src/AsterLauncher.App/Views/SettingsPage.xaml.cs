using AsterLauncher.App.ViewModels;
using AsterLauncher.App.Services;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AsterLauncher.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly LauncherViewModel _viewModel;
    private readonly IFilePickerService _filePicker;
    private readonly LauncherUpdateService _updateService;
    private readonly ResourceUpdateService _resources;
    private LauncherUpdate? _availableUpdate;
    private bool _initialized;

    public SettingsPage(LauncherViewModel viewModel, IFilePickerService filePicker, LauncherUpdateService updateService, ResourceUpdateService resources)
    {
        _viewModel = viewModel;
        _filePicker = filePicker;
        _updateService = updateService;
        _resources = resources;
        InitializeComponent();
        DataContext = viewModel;
        VersionText.Text = $"版本 {_updateService.CurrentVersion}";
        Loaded += Page_OnLoaded;
    }

    private void Page_OnLoaded(object sender, RoutedEventArgs e)
    {
        _initialized = false;
        AccentBox.SelectedIndex = (int)_viewModel.AccentPreference;
        ThemeBox.SelectedIndex = _viewModel.ThemePreference switch
        {
            LauncherThemePreference.Dark => 1,
            LauncherThemePreference.Light => 2,
            _ => 0
        };
        CloseBehaviorBox.SelectedIndex = _viewModel.CloseButtonBehavior == CloseButtonBehavior.MinimizeToTray ? 1 : 0;
        DirectoryBox.Text = _viewModel.GameDownloadDirectory;
        DataDirectoryBox.Text = LauncherDataPaths.ResolveDataDirectory();
        ResourceUpdatesBox.IsChecked = _viewModel.ResourceUpdatesEnabled;
        ResourceStatusText.Text = _resources.Current is { } cached ? $"资料版本 {cached.Revision}" : "使用内置资料";
        _initialized = true;
    }

    private async void ResourceUpdates_OnClick(object sender, RoutedEventArgs e)
        => await _viewModel.SetResourceUpdatesEnabledAsync(ResourceUpdatesBox.IsChecked == true);
    private async void ResourceUpdate_OnClick(object sender, RoutedEventArgs e)
    {
        ResourceUpdateButton.IsEnabled = false; ResourceStatusText.Text = "正在检查资料…";
        try { var changed = await _resources.RefreshAsync(); ResourceStatusText.Text = changed ? $"已更新 · 资料版本 {_resources.Current?.Revision}" : $"资料已是最新 · {_resources.Current?.Revision}"; }
        catch { ResourceStatusText.Text = "检查失败，保留缓存和内置资料。"; }
        finally { ResourceUpdateButton.IsEnabled = true; }
    }
    private async void ThemeBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<LauncherThemePreference>(tag, out var preference))
        {
            return;
        }

        await _viewModel.SetThemePreferenceAsync(preference);
    }

    private async void AccentBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized && AccentBox.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<LauncherAccentPreference>(tag, out var value))
            await _viewModel.SetAccentPreferenceAsync(value);
    }
    private async void CloseBehaviorBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || CloseBehaviorBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<CloseButtonBehavior>(tag, out var behavior)) return;
        await _viewModel.SetCloseButtonBehaviorAsync(behavior);
    }

    private async void BrowseDirectory_OnClick(object sender, RoutedEventArgs e)
    {
        var directory = await _filePicker.PickGameDirectoryAsync(App.MainWindow);
        if (directory is not null) DirectoryBox.Text = directory;
    }

    private async void SaveDirectory_OnClick(object sender, RoutedEventArgs e)
    {
        var error = await _viewModel.SetGameDownloadDirectoryAsync(DirectoryBox.Text);
        if (error is not null)
        {
            DirectoryResultText.Text = error;
            return;
        }
        DirectoryResultText.Text = "目录已保存，正在重新查找…";
        var (found, total) = await _viewModel.ScanBuiltInGamesAsync();
        DirectoryResultText.Text = $"目录已保存；当前找到 {found}/{total} 款游戏。";
    }

    private async void BrowseDataDirectory_OnClick(object sender, RoutedEventArgs e)
    {
        var directory = await _filePicker.PickGameDirectoryAsync(App.MainWindow);
        if (directory is not null) DataDirectoryBox.Text = directory;
    }

    private async void RelocateData_OnClick(object sender, RoutedEventArgs e)
    {
        DataDirectoryResultText.Text = "正在复制启动器数据…";
        try
        {
            if (!await LauncherDataPaths.RelocateAsync(DataDirectoryBox.Text))
            {
                DataDirectoryResultText.Text = "当前已在此目录。";
                return;
            }

            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定启动器 EXE 路径。");
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
            Application.Current.Exit();
        }
        catch (Exception exception)
        {
            DataDirectoryResultText.Text = $"迁移失败：{exception.Message}";
        }
    }

    private async void RecoverPlayHistory_OnClick(object sender, RoutedEventArgs e)
    {
        RecoverPlayHistoryButton.IsEnabled = false;
        RecoveryStatusText.Text = "正在核对本地完成会话…";
        try
        {
            var plan = await _viewModel.PreviewPlayHistoryRecoveryAsync();
            if (!plan.HasChanges)
            {
                RecoveryStatusText.Text = plan.ReachedScanLimit || plan.SkippedLogCount > 0
                    ? "已检查的日志中没有可补充的时长；部分日志未能读取或超过检查范围，原记录保留。"
                    : "没有可补充的已完成会话，现有时长保持不变。";
                return;
            }
            var names = BuiltInGameCatalog.CreateAdapters().ToDictionary(a => a.Definition.Id, a => a.Definition.DisplayName);
            var lines = plan.Games.Select(change =>
                $"{names.GetValueOrDefault(change.GameId, change.GameId)}：补充 {change.AddedSessionCount} 次会话，恢复后累计 {FormatRecoveryTime(change.RecoveredTotalSeconds)}");
            var body = string.Join(Environment.NewLine, lines)
                + Environment.NewLine + Environment.NewLine
                + "只合并能核实的已完成会话，不改动游戏路径、隐藏列表、启动方案或抽卡档案。没有结束记录的会话不计入，重复恢复不会重复累计。";
            if (plan.ReachedScanLimit || plan.SkippedLogCount > 0)
                body += Environment.NewLine + "部分日志未能读取或超过检查范围，本次仅恢复已核实的部分。";
            var dialog = new ContentDialog
            {
                Title = "恢复游戏时长",
                Content = new ScrollViewer { MaxHeight = 360, Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap } },
                PrimaryButtonText = "确认恢复", CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot, RequestedTheme = ActualTheme
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                RecoveryStatusText.Text = "已取消，记录未改动。";
                return;
            }
            var changes = await _viewModel.ApplyPlayHistoryRecoveryAsync(plan);
            RecoveryStatusText.Text = $"已恢复 {changes.Sum(change => change.AddedSessionCount)} 次完成会话；其他设置保留。";
        }
        catch (ConfigurationReadException exception) { RecoveryStatusText.Text = exception.Message; }
        catch { RecoveryStatusText.Text = "未能完成恢复，原有记录保留。请检查数据目录后重试。"; }
        finally { RecoverPlayHistoryButton.IsEnabled = true; }
    }

    private async void RecoverConfiguration_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Games.Any(game => game.IsRunning))
        {
            RecoveryStatusText.Text = "游戏运行期间不能替换整份配置。请结束游戏后重试；从日志恢复时长仍可使用。";
            return;
        }
        RecoverConfigurationButton.IsEnabled = false;
        try
        {
            var store = (JsonConfigurationStore)((App)Application.Current).Services.GetRequiredService<IConfigurationStore>();
            var restored = await ConfigurationRecoveryActions.ConfirmAndRestoreAsync(store, XamlRoot, ActualTheme);
            RecoveryStatusText.Text = restored ? "配置已恢复，正在重新打开启动器…" : "未恢复配置，现有设置保持不变。";
            if (restored) await ConfigurationRecoveryActions.RestartAsync(XamlRoot, ActualTheme);
        }
        catch (ConfigurationRecoveryException exception) { RecoveryStatusText.Text = exception.Message; }
        catch { RecoveryStatusText.Text = "未能恢复配置。请检查数据目录；已存在的备份仍保留。"; }
        finally { RecoverConfigurationButton.IsEnabled = true; }
    }

    private static string FormatRecoveryTime(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(long)duration.TotalHours} 小时 {duration.Minutes} 分钟";
    }
    private async void OpenRepository_OnClick(object sender, RoutedEventArgs e)
    {
        await ExternalBrowserService.OpenAsync(new Uri(LauncherUpdateService.RepositoryUrl), XamlRoot);
    }

    private async void CheckUpdate_OnClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查 GitHub Releases…";
        try
        {
            _availableUpdate = await _updateService.CheckAsync();
            if (_availableUpdate is null)
            {
                UpdateStatusText.Text = "当前已是最新版本。";
            }
            else
            {
                UpdateStatusText.Text = $"发现 {_availableUpdate.Version} · {_availableUpdate.Method}";
                InstallUpdateButton.IsEnabled = true;
            }
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = $"检查失败：{exception.Message}";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void InstallUpdate_OnClick(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null) return;
        InstallUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = $"正在下载并校验 {_availableUpdate.Version}…";
        try
        {
            await _updateService.DownloadAndInstallAsync(_availableUpdate);
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = $"更新失败：{exception.Message}";
            CheckUpdateButton.IsEnabled = true;
            InstallUpdateButton.IsEnabled = true;
        }
    }
}
