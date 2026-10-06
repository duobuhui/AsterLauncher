using AsterLauncher.App.ViewModels;
using AsterLauncher.App.Services;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
using System.Diagnostics;
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
        VersionText.Text = $"版本 Beta {_updateService.CurrentVersion.Replace("-beta", "", StringComparison.OrdinalIgnoreCase)}";
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

    private async void OpenRepository_OnClick(object sender, RoutedEventArgs e)
    {
        await Windows.System.Launcher.LaunchUriAsync(new Uri(LauncherUpdateService.RepositoryUrl));
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
