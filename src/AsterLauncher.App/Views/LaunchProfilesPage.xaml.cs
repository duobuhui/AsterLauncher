using AsterLauncher.App.Services;
using AsterLauncher.App.ViewModels;
using AsterLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AsterLauncher.App.Views;

public sealed partial class LaunchProfilesPage : Page
{
    private readonly IFilePickerService _filePicker;

    public LaunchProfilesPage(LauncherViewModel viewModel, IFilePickerService filePicker)
    {
        ViewModel = viewModel;
        _filePicker = filePicker;
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += (_, _) => ProfileNameBox.Text = ViewModel.SelectedProfile?.Name ?? string.Empty;
    }

    public LauncherViewModel ViewModel { get; }

    private async void ProfileBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileNameBox is null) return;
        ProfileNameBox.Text = ViewModel.SelectedProfile?.Name ?? string.Empty;
        await ViewModel.PersistSelectedProfileAsync();
    }

    private async void AddProfile_OnClick(object sender, RoutedEventArgs e)
    {
        var profile = await ViewModel.AddProfileAsync();
        if (profile is not null) ProfileNameBox.Text = profile.Name;
    }

    private async void RemoveProfile_OnClick(object sender, RoutedEventArgs e)
    {
        if (!await ViewModel.RemoveSelectedProfileAsync())
        {
            ShowResult("无法删除方案", "每款游戏至少要保留一个方案。", InfoBarSeverity.Informational);
        }
    }

    private async void RenameProfile_OnClick(object sender, RoutedEventArgs e)
    {
        if (!await ViewModel.RenameSelectedProfileAsync(ProfileNameBox.Text))
        {
            ShowResult("无法保存名称", "请输入不重复的方案名称。", InfoBarSeverity.Warning);
        }
    }

    private void ShowResult(string title, string message, InfoBarSeverity severity)
    {
        PathResultInfo.Title = title;
        PathResultInfo.Message = message;
        PathResultInfo.Severity = severity;
        PathResultInfo.IsOpen = true;
    }

    private async void AddBefore_OnClick(object sender, RoutedEventArgs e) =>
        await AddExecutableStepAsync(LaunchPhase.BeforeGame, "启动前工具");

    private async void AddAfter_OnClick(object sender, RoutedEventArgs e) =>
        await AddExecutableStepAsync(LaunchPhase.AfterGame, "启动后工具");

    private async void AddTakeover_OnClick(object sender, RoutedEventArgs e) =>
        await AddExecutableStepAsync(LaunchPhase.Game, "接管游戏启动", takesOverGameLaunch: true);

    private async void AddOnExit_OnClick(object sender, RoutedEventArgs e) =>
        await AddExecutableStepAsync(LaunchPhase.OnGameExit, "游戏退出后任务");

    private async void ChangeGameExecutable_OnClick(object sender, RoutedEventArgs e)
    {
        var path = await _filePicker.PickExecutableAsync(App.MainWindow);
        if (path is null)
        {
            PathResultInfo.Title = "未更改游戏 EXE";
            PathResultInfo.Message = "已取消文件选择。";
            PathResultInfo.Severity = InfoBarSeverity.Informational;
            PathResultInfo.IsOpen = true;
            return;
        }

        var result = await ViewModel.SetManualExecutableAsync(path);
        PathResultInfo.Title = result.Kind == ScanResultKind.Found ? "已更新游戏 EXE" : "无法更新游戏 EXE";
        PathResultInfo.Message = result.Message;
        PathResultInfo.Severity = result.Kind == ScanResultKind.Found ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        PathResultInfo.IsOpen = true;
    }

    private async void RemoveStep_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LaunchStep step })
        {
            await ViewModel.RemoveStepAsync(step);
        }
    }

    private async Task AddExecutableStepAsync(LaunchPhase phase, string defaultName, bool takesOverGameLaunch = false)
    {
        var path = await _filePicker.PickExecutableAsync(App.MainWindow);
        if (path is null)
        {
            return;
        }

        var nameBox = new TextBox { Header = "步骤名称", Text = Path.GetFileNameWithoutExtension(path) };
        var argumentsBox = new TextBox { Header = "参数", PlaceholderText = "例如：run daily" };
        var closeToggle = new ToggleSwitch { Header = "游戏退出后关闭（仅限本次由启动器创建的进程）" };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(nameBox);
        panel.Children.Add(argumentsBox);
        panel.Children.Add(closeToggle);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = defaultName,
            Content = panel,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消"
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.AddStepAsync(new LaunchStep
            {
                Name = string.IsNullOrWhiteSpace(nameBox.Text) ? defaultName : nameBox.Text.Trim(),
                ExecutablePath = path,
                Arguments = argumentsBox.Text,
                WorkingDirectory = Path.GetDirectoryName(path),
                Phase = phase,
                CloseOnGameExit = closeToggle.IsOn,
                TakesOverGameLaunch = takesOverGameLaunch,
                FailurePolicy = takesOverGameLaunch ? LaunchFailurePolicy.Abort : LaunchFailurePolicy.Ask
            });
        }
    }
}
