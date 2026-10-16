using AsterLauncher.App.Services;
using AsterLauncher.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AsterLauncher.App.Views;


public sealed record EndfieldLauncherPathStatus
{
    public string Title { get; init; } = "尚未选择官方启动器";
    public string Message { get; init; } = "选择当前渠道的官方启动程序。启动前会再次检查文件。";
    public bool IsLauncherUsable { get; init; }
    public bool SupportsExperimentalBridge { get; init; }
    public bool RequiresChannelConfirmation { get; init; }
}

public sealed record EndfieldLauncherChoice
{
    public string Path { get; init; } = string.Empty;
    public string Label { get; init; } = "官方启动器";
    public EndfieldLauncherPathStatus Status { get; init; } = new();
}

public sealed record EndfieldLaunchOptionsInput
{
    public string ChannelLabel { get; init; } = "国服官服";
    public EndfieldLaunchMode Mode { get; init; } = EndfieldLaunchMode.OfficialLauncher;
    public string OfficialLauncherPath { get; init; } = string.Empty;
    public int DetectionTimeoutSeconds { get; init; } = 180;
    public bool IsChannelAssociationConfirmed { get; init; }
    public IReadOnlyList<EndfieldLauncherChoice> Candidates { get; init; } = [];
    public Func<string, CancellationToken, Task<EndfieldLauncherPathStatus>>? InspectPathAsync { get; init; }
}

public sealed record EndfieldLaunchOptionsResult(
    EndfieldLaunchMode Mode, string OfficialLauncherPath, int DetectionTimeoutSeconds, bool IsChannelAssociationConfirmed);

/// <summary>Edits only the selected channel's launch method; the game EXE remains separate.</summary>
public sealed class EndfieldLaunchOptionsDialog : ContentDialog
{
    private readonly Window _owner;
    private readonly IFilePickerService _filePicker;
    private readonly EndfieldLaunchOptionsInput _input;
    private readonly ComboBox _mode = new() { Header = "启动方式", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _candidates = new() { Header = "发现的启动器", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _timeout = new() { Header = "等待游戏启动", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _channelConfirmed = new();
    private readonly TextBox _path = new()
    {
        IsReadOnly = true, PlaceholderText = "尚未选择官方启动器 EXE",
        HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0
    };
    private readonly Button _browse = new() { Content = "选择 EXE", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _description = Caption(string.Empty);
    private readonly InfoBar _status = new() { IsOpen = true, IsClosable = false };
    private readonly ScrollViewer _bodyScroll = new();
    private XamlRoot? _dialogRoot;
    private readonly TextBlock _experimental = Caption("实验功能需要本机受信任、签名有效的官方 Games.exe。兼容情况尚需实际游戏验证；失败后可切回官方启动器接管。");
    private CancellationTokenSource? _inspection;
    private EndfieldLauncherPathStatus _pathStatus = new();
    private bool _inspecting;
    private bool _closed;

    public EndfieldLaunchOptionsDialog(Window owner, IFilePickerService filePicker, EndfieldLaunchOptionsInput input)
    {
        _owner = owner;
        _filePicker = filePicker;
        _input = input;
        Title = "终末地 · 启动方式";
        PrimaryButtonText = "保存";
        CloseButtonText = "取消";
        CloseButtonStyle = Application.Current.Resources["SecondaryButtonStyle"] as Style;
        DefaultButton = ContentDialogButton.Primary;
        Resources["ContentDialogMaxWidth"] = 600d;
        AutomationProperties.SetAutomationId(this, "EndfieldLaunchOptionsDialog");
        AutomationProperties.SetName(_mode, "终末地启动方式");
        AutomationProperties.SetAutomationId(_mode, "EndfieldLaunchMode");
        AutomationProperties.SetName(_path, "当前渠道官方启动器路径");
        AutomationProperties.SetAutomationId(_path, "EndfieldOfficialLauncherPath");
        AutomationProperties.SetName(_browse, "选择当前渠道官方启动器 EXE");
        AutomationProperties.SetAutomationId(_browse, "EndfieldChooseOfficialLauncher");
        AutomationProperties.SetName(_candidates, "发现的官方启动器候选");
        AutomationProperties.SetAutomationId(_status, "EndfieldLauncherValidationStatus");

        _mode.Items.Add(new ComboBoxItem { Content = "官方启动器接管（默认）", Tag = EndfieldLaunchMode.OfficialLauncher });
        _mode.Items.Add(new ComboBoxItem { Content = "无界面启动桥（实验性）", Tag = EndfieldLaunchMode.ExperimentalBridge });
        _mode.SelectedIndex = input.Mode == EndfieldLaunchMode.ExperimentalBridge ? 1 : 0;
        _timeout.Items.Add(new ComboBoxItem { Content = "1 分钟", Tag = 60 });
        _timeout.Items.Add(new ComboBoxItem { Content = "3 分钟（默认）", Tag = 180 });
        _timeout.Items.Add(new ComboBoxItem { Content = "5 分钟", Tag = 300 });
        _timeout.SelectedIndex = input.DetectionTimeoutSeconds == 60 ? 0 : input.DetectionTimeoutSeconds == 300 ? 2 : 1;
        AutomationProperties.SetName(_timeout, "等待游戏启动时限");
        AutomationProperties.SetAutomationId(_timeout, "EndfieldLaunchDetectionTimeout");
        _channelConfirmed.Content = new TextBlock { Text = $"我确认此启动器用于当前服务器：{input.ChannelLabel}", TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        _channelConfirmed.IsChecked = input.IsChannelAssociationConfirmed;
        AutomationProperties.SetName(_channelConfirmed, "确认官方启动器与当前服务器关联");
        _path.Text = input.OfficialLauncherPath;
        ToolTipService.SetToolTip(_path, input.OfficialLauncherPath);

        var body = new StackPanel { Spacing = 12, MinWidth = 280 };
        var channel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        channel.Children.Add(new TextBlock { Text = input.ChannelLabel, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        channel.Children.Add(Caption("仅应用于此服务器"));
        body.Children.Add(channel);
        body.Children.Add(_mode);
        body.Children.Add(_description);

        foreach (var candidate in input.Candidates)
        {
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock { Text = candidate.Label, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(Caption(candidate.Path));
            var item = new ComboBoxItem { Content = row, Tag = candidate, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(item, candidate.Label + "，" + candidate.Path);
            _candidates.Items.Add(item);
            if (string.Equals(candidate.Path, input.OfficialLauncherPath, StringComparison.OrdinalIgnoreCase))
                _candidates.SelectedItem = item;
        }
        if (_candidates.Items.Count > 0) body.Children.Add(_candidates);

        var pathLabel = new TextBlock { Text = "当前渠道的官方启动器", FontSize = 13 };
        var pathGrid = new Grid { ColumnSpacing = 8 };
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathGrid.Children.Add(_path);
        Grid.SetColumn(_browse, 1);
        pathGrid.Children.Add(_browse);
        var pathSection = new StackPanel { Spacing = 6 };
        pathSection.Children.Add(pathLabel);
        pathSection.Children.Add(pathGrid);
        body.Children.Add(pathSection);
        body.Children.Add(_status);
        body.Children.Add(_channelConfirmed);
        body.Children.Add(_timeout);
        body.Children.Add(_experimental);
        body.Children.Add(Caption("游戏路径仍使用 Endfield.exe。此设置不修改游戏文件，也不会自动申请管理员权限。"));
        _bodyScroll.Content = body;
        _bodyScroll.MaxHeight = 440;
        _bodyScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _bodyScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        AutomationProperties.SetAutomationId(_bodyScroll, "EndfieldLaunchOptionsScroll");
        Content = _bodyScroll;

        _mode.SelectionChanged += (_, _) => UpdateState();
        _channelConfirmed.Checked += (_, _) => UpdateState();
        _channelConfirmed.Unchecked += (_, _) => UpdateState();
        _candidates.SelectionChanged += async (_, _) =>
        {
            if (_candidates.SelectedItem is ComboBoxItem { Tag: EndfieldLauncherChoice choice })
            {
                if (!string.Equals(_path.Text, choice.Path, StringComparison.OrdinalIgnoreCase)) _channelConfirmed.IsChecked = false;
                _path.Text = choice.Path;
                ToolTipService.SetToolTip(_path, choice.Path);
                await InspectAsync(choice.Path);
            }
        };
        _browse.Click += BrowseAsync;
        Opened += async (_, _) =>
        {
            _dialogRoot = XamlRoot;
            if (_dialogRoot is not null) _dialogRoot.Changed += DialogRootChanged;
            UpdateContentHeight();
            await InspectAsync(_path.Text);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _inspection?.Cancel();
            if (_dialogRoot is not null) _dialogRoot.Changed -= DialogRootChanged;
        };
        PrimaryButtonClick += (_, args) =>
        {
            if (!CanSave)
            {
                args.Cancel = true;
                return;
            }
            Result = new EndfieldLaunchOptionsResult(SelectedMode, _path.Text.Trim(),
                _timeout.SelectedItem is ComboBoxItem { Tag: int seconds } ? seconds : 180,
                _channelConfirmed.IsChecked == true);
        };
        UpdateState();
    }

    /// <summary>Available only after the primary action; cancellation leaves it null.</summary>
    public EndfieldLaunchOptionsResult? Result { get; private set; }

    private EndfieldLaunchMode SelectedMode =>
        _mode.SelectedItem is ComboBoxItem { Tag: EndfieldLaunchMode value }
            ? value : EndfieldLaunchMode.OfficialLauncher;

    private void DialogRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateContentHeight();

    private void UpdateContentHeight()
    {
        if (XamlRoot is { Size.Height: > 0 } root)
            _bodyScroll.MaxHeight = Math.Clamp(root.Size.Height - 220, 150, 440);
    }
    private async void BrowseAsync(object sender, RoutedEventArgs args)
    {
        _browse.IsEnabled = false;
        try
        {
            var path = await _filePicker.PickExecutableAsync(_owner);
            if (_closed || string.IsNullOrWhiteSpace(path)) return;
            _candidates.SelectedIndex = -1;
            if (!string.Equals(_path.Text, path, StringComparison.OrdinalIgnoreCase)) _channelConfirmed.IsChecked = false;
            _path.Text = path;
            ToolTipService.SetToolTip(_path, path);
            await InspectAsync(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _pathStatus = new() { Title = "未能选择启动器", Message = "文件选择未完成，请重试。" };
            UpdateState();
        }
        finally { if (!_closed) _browse.IsEnabled = true; }
    }

    private async Task InspectAsync(string path)
    {
        _inspection?.Cancel();
        _inspection?.Dispose();
        var inspection = _inspection = new CancellationTokenSource();
        _inspecting = !string.IsNullOrWhiteSpace(path);
        UpdateState();
        try
        {
            var status = string.IsNullOrWhiteSpace(path)
                ? new EndfieldLauncherPathStatus()
                : _input.InspectPathAsync is not null
                    ? await _input.InspectPathAsync(path, inspection.Token)
                    : _input.Candidates.FirstOrDefault(candidate => string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase))?.Status
                        ?? new EndfieldLauncherPathStatus { Title = "已选择路径", Message = "启动前将检查文件是否可用；实验模式需要先验证官方签名。" };
            if (_closed || inspection.IsCancellationRequested) return;
            _pathStatus = status;
        }
        catch (OperationCanceledException) when (inspection.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)
        {
            if (!_closed && !inspection.IsCancellationRequested)
                _pathStatus = new() { Title = "无法验证此文件", Message = "文件可能不存在、被占用或签名无法验证。可重新选择对应渠道的官方启动器。" };
        }
        finally
        {
            if (ReferenceEquals(_inspection, inspection) && !_closed)
            {
                _inspecting = false;
                UpdateState();
            }
        }
    }

    private void UpdateState()
    {
        var experimental = SelectedMode == EndfieldLaunchMode.ExperimentalBridge;
        _description.Text = experimental
            ? "尝试通过本机官方启动文件承接启动来源，不显示官方启动器界面。此功能默认关闭。"
            : "先打开对应渠道的官方启动器，再由你点击开始游戏。Aster 等待实际游戏进程，并按游戏进程记录时长。";
        _experimental.Visibility = experimental ? Visibility.Visible : Visibility.Collapsed;
        _status.Title = _inspecting ? "正在验证官方启动器…" : _pathStatus.Title;
        _status.Message = _inspecting ? "检查文件与签名，请稍候。" : _pathStatus.Message;
        _status.Severity = _inspecting ? InfoBarSeverity.Informational
            : _pathStatus.RequiresChannelConfirmation && _channelConfirmed.IsChecked != true ? InfoBarSeverity.Warning
            : (experimental ? _pathStatus.SupportsExperimentalBridge : _pathStatus.IsLauncherUsable)
                ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        _channelConfirmed.Visibility = _pathStatus.RequiresChannelConfirmation ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = CanSave;
    }

    private bool CanSave => !_inspecting
        && (!_pathStatus.RequiresChannelConfirmation || _channelConfirmed.IsChecked == true)
        && (SelectedMode == EndfieldLaunchMode.ExperimentalBridge ? _pathStatus.SupportsExperimentalBridge : _pathStatus.IsLauncherUsable);

    private static TextBlock Caption(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12,
        Style = Application.Current.Resources["CaptionTextStyle"] as Style
    };
}