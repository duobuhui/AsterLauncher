using AsterLauncher.App.Services;
using AsterLauncher.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace AsterLauncher.App.Views;

public sealed partial class GameSettingsPage : Page
{
    private readonly LauncherViewModel _viewModel;
    private readonly IFilePickerService _filePicker;

    public GameSettingsPage(LauncherViewModel viewModel, IFilePickerService filePicker)
    {
        _viewModel = viewModel;
        _filePicker = filePicker;
        InitializeComponent();
    }

    private void Page_OnLoaded(object sender, RoutedEventArgs e) => RefreshForm();

    private void RefreshForm()
    {
        var game = _viewModel.CurrentGame;
        GameNameText.Text = game?.DisplayName ?? "未选择游戏";
        ExecutablePathBox.Text = game?.State.ExecutablePath ?? string.Empty;
        DisplayNameBox.Text = game?.DisplayName ?? string.Empty;
        PublisherBox.Text = game?.Publisher ?? string.Empty;
        ArtworkPathBox.Text = game?.State.ArtworkPath ?? string.Empty;
        DownloadDescriptionText.Text = game?.DownloadDescription ?? "暂无官方下载信息。";
        OfficialDownloadButton.IsEnabled = Uri.TryCreate(game?.OfficialDownloadUri, UriKind.Absolute, out _);
    }

    private async void SelectExecutable_OnClick(object sender, RoutedEventArgs e)
    {
        var path = await _filePicker.PickExecutableAsync(App.MainWindow);
        if (path is not null)
        {
            ExecutablePathBox.Text = path;
        }
    }

    private async void SelectArtwork_OnClick(object sender, RoutedEventArgs e)
    {
        var path = await _filePicker.PickImageAsync(App.MainWindow);
        if (path is not null)
        {
            ArtworkPathBox.Text = path;
        }
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var game = _viewModel.CurrentGame;
        if (game is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(ExecutablePathBox.Text)
            && !string.Equals(ExecutablePathBox.Text, game.State.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            var validation = await _viewModel.SetManualExecutableAsync(ExecutablePathBox.Text);
            if (validation.Kind != Core.ScanResultKind.Found)
            {
                ResultInfo.Title = "路径未保存";
                ResultInfo.Message = validation.Message;
                ResultInfo.Severity = InfoBarSeverity.Error;
                ResultInfo.IsOpen = true;
                return;
            }
        }

        await _viewModel.UpdateGamePresentationAsync(
            game,
            DisplayNameBox.Text,
            PublisherBox.Text,
            game.State.IconGlyphOverride,
            ArtworkPathBox.Text);
        ResultInfo.Title = "已保存";
        ResultInfo.Message = "当前游戏的路径与展示信息已更新。";
        ResultInfo.Severity = InfoBarSeverity.Success;
        ResultInfo.IsOpen = true;
        RefreshForm();
    }

    private async void OpenOfficialDownload_OnClick(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(_viewModel.CurrentGame?.OfficialDownloadUri, UriKind.Absolute, out var uri))
        {
            await Launcher.LaunchUriAsync(uri);
        }
    }
}
