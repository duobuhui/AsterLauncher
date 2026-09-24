using AsterLauncher.App.Services;
using AsterLauncher.App.ViewModels;
using AsterLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace AsterLauncher.App.Views;

public sealed partial class ToolsPage : Page
{
    private readonly IFilePickerService _filePicker;

    public ToolsPage(LauncherViewModel viewModel, IFilePickerService filePicker)
    {
        ViewModel = viewModel;
        _filePicker = filePicker;
        InitializeComponent();
        DataContext = ViewModel;
    }

    public LauncherViewModel ViewModel { get; }

    private async void SelectTool_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CompanionTool tool })
        {
            return;
        }

        var path = await _filePicker.PickExecutableAsync(App.MainWindow);
        if (path is not null)
        {
            await ViewModel.SetCompanionToolPathAsync(tool, path);
        }
    }

    private async void OpenHomepage_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CompanionTool tool }
            && Uri.TryCreate(tool.HomepageUri, UriKind.Absolute, out var uri))
        {
            await Launcher.LaunchUriAsync(uri);
        }
    }
}
