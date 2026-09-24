using AsterLauncher.App.Services;
using AsterLauncher.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AsterLauncher.App.Views;

public sealed partial class FirstRunGuidePage : Page
{
    private readonly LauncherViewModel _launcher;
    private readonly IFilePickerService _filePicker;
    private readonly Action _completed;

    public FirstRunGuidePage(LauncherViewModel launcher, IFilePickerService filePicker, Action completed)
    {
        _launcher = launcher;
        _filePicker = filePicker;
        _completed = completed;
        InitializeComponent();
        DirectoryBox.Text = launcher.GameDownloadDirectory;
    }

    private async void ScanButton_OnClick(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        ScanResultText.Text = "正在查找…";
        try
        {
            var (found, total) = await _launcher.ScanBuiltInGamesAsync();
            ScanResultText.Text = $"找到 {found}/{total} 款游戏。设定目录后会在目录内再查找一次。";
        }
        catch (Exception exception)
        {
            ScanResultText.Text = $"查找未完成：{exception.Message}";
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private async void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var directory = await _filePicker.PickGameDirectoryAsync(App.MainWindow);
        if (directory is not null) DirectoryBox.Text = directory;
    }

    private async void FinishButton_OnClick(object sender, RoutedEventArgs e)
    {
        FinishButton.IsEnabled = false;
        try
        {
            var error = await _launcher.SetGameDownloadDirectoryAsync(DirectoryBox.Text);
            if (error is not null)
            {
                DirectoryResultText.Text = error;
                return;
            }
            DirectoryResultText.Text = "正在游戏目录中查找…";
            var (found, total) = await _launcher.ScanBuiltInGamesAsync();
            DirectoryResultText.Text = $"目录已保存；当前共找到 {found}/{total} 款游戏。";
            await _launcher.CompleteFirstRunGuideAsync();
            _completed();
        }
        catch (Exception exception)
        {
            DirectoryResultText.Text = $"设置未完成：{exception.Message}";
        }
        finally
        {
            FinishButton.IsEnabled = true;
        }
    }
}
