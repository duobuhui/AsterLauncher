using AsterLauncher.App.Services;
using AsterLauncher.App.ViewModels;
using AsterLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AsterLauncher.App.Views;

public sealed partial class HomePage : Page
{
    private readonly IFilePickerService _filePicker;

    public HomePage(LauncherViewModel viewModel, IFilePickerService filePicker)
    {
        ViewModel = viewModel;
        _filePicker = filePicker;
        InitializeComponent();
        DataContext = ViewModel;
    }

    public LauncherViewModel ViewModel { get; }

    private async void LaunchButton_OnClick(object sender, RoutedEventArgs e) =>
        await ViewModel.LaunchSelectedAsync();

    private async void ScanButton_OnClick(object sender, RoutedEventArgs e) =>
        await ViewModel.ScanSelectedAsync();

    private async void ManualPathButton_OnClick(object sender, RoutedEventArgs e)
    {
        var path = await _filePicker.PickExecutableAsync(App.MainWindow);
        if (path is null)
        {
            return;
        }

        var result = await ViewModel.SetManualExecutableAsync(path);
        if (result.Kind != ScanResultKind.Found)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "无法使用所选路径",
                Content = result.Message,
                CloseButtonText = "知道了"
            };
            await dialog.ShowAsync();
        }
    }
}
