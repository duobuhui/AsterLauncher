using AsterLauncher.App.Services;
using AsterLauncher.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using AsterLauncher.Infrastructure;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AsterLauncher.App.Views;

public sealed partial class GameSettingsPage : Page
{
    private readonly LauncherViewModel _viewModel;
    private readonly IFilePickerService _filePicker;

    public GameSettingsPage(LauncherViewModel viewModel, IFilePickerService filePicker, EndfieldDisplayService display)
    {
        _viewModel = viewModel;
        _filePicker = filePicker;
        EndfieldSettings = new(viewModel, display);
        InitializeComponent();
    }

    public EndfieldSettingsViewModel EndfieldSettings { get; }
    private async void Page_OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshForm();
        await EndfieldSettings.RefreshAsync();
    }
    private async void ReadDisplay_OnClick(object sender, RoutedEventArgs e) => await EndfieldSettings.RefreshAsync();
    private async void DisplayAction_OnClick(object sender, RoutedEventArgs e)
    {
        try { await EndfieldSettings.ExecuteAsync((sender as FrameworkElement)?.Tag?.ToString() ?? ""); }
        catch (Exception exception) { ResultInfo.Title = "设置未保存"; ResultInfo.Message = exception.Message; ResultInfo.IsOpen = true; }
    }
    private async void SelectPhotoDirectory_OnClick(object sender, RoutedEventArgs e)
    {
        var path = await _filePicker.PickGameDirectoryAsync(App.MainWindow);
        if (path is not null) await EndfieldSettings.SelectPhotoDirectoryAsync(path);
    }
    private async void OpenPhotoDirectory_OnClick(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(EndfieldSettings.PhotoDirectory))
            await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(EndfieldSettings.PhotoDirectory));
    }
    private async void Photo_OnClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EndfieldPhoto photo && File.Exists(photo.Path))
            await Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(photo.Path));
    }
    private async void PhotoImage_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not EndfieldPhoto photo) return;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(photo.Path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.PicturesView, 320, ThumbnailOptions.UseCurrentScale);
            if (thumbnail is null || !Equals(image.DataContext, photo)) return;
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            if (Equals(image.DataContext, photo)) image.Source = bitmap;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { image.Source = null; }
    }
    private void PhotoImage_OnUnloaded(object sender, RoutedEventArgs e) { if (sender is Image image) image.Source = null; }

    private async void SelectServerExecutable_OnClick(object sender,RoutedEventArgs e)
    {
        var path = await _filePicker.PickExecutableAsync(App.MainWindow);
        if(path is not null) ServerExecutableBox.Text = path;
    }
    private async void ConfirmServer_OnClick(object sender,RoutedEventArgs e)
    {
        var game = _viewModel.CurrentGame;
        if(game is null || ServerBox.SelectedIndex is not (1 or 2)) { ResultInfo.Message = "请选择服务器。"; ResultInfo.IsOpen = true; return; }
        try
        {
            await _viewModel.ConfirmInstalledServerAsync(game,ServerBox.SelectedIndex == 2,ServerExecutableBox.Text);
            ResultInfo.Title = "服务器已保存"; ResultInfo.Message = ""; ResultInfo.IsOpen = true; RefreshForm();
        }
        catch(Exception ex) { ResultInfo.Title = "服务器未保存"; ResultInfo.Message = ex.Message; ResultInfo.IsOpen = true; }
    }
    private void RefreshForm()
    {
        var game = _viewModel.CurrentGame;
        ServerPanel.Visibility = game is not null && (game.Id == Core.BuiltInGameIds.Endfield || Core.HoYoInstallationIdentity.HasChannels(game.Id)) ? Visibility.Visible : Visibility.Collapsed;
        ServerExecutableBox.Text = game?.EffectiveExecutablePath ?? game?.State.ExecutablePath ?? "";
        ServerBox.SelectedIndex = game?.Id == Core.BuiltInGameIds.Endfield ? (int)_viewModel.SelectedEndfieldChannel : game?.State.SelectedHoYoChannel switch { Core.HoYoChannel.Official => 1, Core.HoYoChannel.Bilibili => 2, _ => 0 };
        PresentationExpander.IsExpanded = game?.Id != Core.BuiltInGameIds.Endfield;
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
