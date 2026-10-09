using System.Diagnostics;
using AsterLauncher.App.Services;
using AsterLauncher.Infrastructure;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace AsterLauncher.App.Views;

public sealed partial class ConfigurationRecoveryWindow : Window
{
    private readonly string _directory;
    private readonly JsonConfigurationStore _store;

    public ConfigurationRecoveryWindow(ConfigurationReadException exception, JsonConfigurationStore store)
    {
        InitializeComponent();
        _store = store;
        _directory = Path.GetDirectoryName(exception.ConfigurationPath)!;
        ReasonText.Text = exception.Message;
        PathText.Text = exception.ConfigurationPath;
        AppWindow.Resize(new SizeInt32(680, 400));
        Closed += (_, _) => Application.Current.Exit();
    }

    private async void RestoreBackup_OnClick(object sender, RoutedEventArgs e)
    {
        RestoreButton.IsEnabled = false;
        try
        {
            if (await ConfigurationRecoveryActions.ConfirmAndRestoreAsync(_store, ((FrameworkElement)Content).XamlRoot, ((FrameworkElement)Content).ActualTheme))
                await ConfigurationRecoveryActions.RestartAsync(((FrameworkElement)Content).XamlRoot, ((FrameworkElement)Content).ActualTheme);
        }
        catch (ConfigurationRecoveryException exception) { ReasonText.Text = exception.Message; }
        catch { ReasonText.Text = "恢复未能完成。原文件与已有备份保留，请重新检查后再试。"; }
        finally { RestoreButton.IsEnabled = true; }
    }
    private void OpenDataDirectory_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(_directory);
            Process.Start(start)?.Dispose();
        }
        catch
        {
            ReasonText.Text = "无法打开数据目录。请复制下方路径，在文件资源管理器中检查配置文件。";
        }
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}