using AsterLauncher.App.ViewModels;
using AsterLauncher.App.Services;
using AsterLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AsterLauncher.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly LauncherViewModel _viewModel;
    private readonly IFilePickerService _filePicker;
    private readonly LauncherUpdateService _updateService;
    private LauncherUpdate? _availableUpdate;
    private bool _initialized;

    public SettingsPage(LauncherViewModel viewModel, IFilePickerService filePicker, LauncherUpdateService updateService)
    {
        _viewModel = viewModel;
        _filePicker = filePicker;
        _updateService = updateService;
        InitializeComponent();
        DataContext = viewModel;
        VersionText.Text = $"版本 Beta {_updateService.CurrentVersion.Replace("-beta", "", StringComparison.OrdinalIgnoreCase)}";
        Loaded += Page_OnLoaded;
    }

    private void Page_OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeBox.SelectedIndex = _viewModel.ThemePreference switch
        {
            LauncherThemePreference.Dark => 1,
            LauncherThemePreference.Light => 2,
            LauncherThemePreference.TyphonPurple => 3,
            LauncherThemePreference.ElysiaPink => 4,
            LauncherThemePreference.PaimonWhite => 5,
            _ => 0
        };
        CloseBehaviorBox.SelectedIndex = _viewModel.CloseButtonBehavior == CloseButtonBehavior.MinimizeToTray ? 1 : 0;
        DirectoryBox.Text = _viewModel.GameDownloadDirectory;
        _initialized = true;
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
