using AsterLauncher.App.Services;
using AsterLauncher.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AsterLauncher.App.Views;

public sealed record EndfieldLauncherPathStatus
{
    public string Title { get; init; } = "尚未选择官方启动器";
    public string Message { get; init; } = "请选择当前服务器使用的官方启动器。";
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

/// <summary>A single-screen choice for the selected channel. Launch arguments never enter this dialog.</summary>
public sealed class EndfieldLaunchOptionsDialog : ContentDialog
{
    private readonly Window _owner;
    private readonly IFilePickerService _filePicker;
    private readonly EndfieldLaunchOptionsInput _input;
    private readonly RadioButton _official = new() { GroupName = "EndfieldLaunchMode", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly RadioButton _experimental = new() { GroupName = "EndfieldLaunchMode", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Border _officialCard;
    private readonly Border _experimentalCard;
    private readonly ComboBox _paths = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "选择官方启动器 EXE", MaxDropDownHeight = 190 };
    private readonly ComboBox _timeout = new() { Width = 106, MinWidth = 90, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _channelConfirmed = new() { MinHeight = 28 };
    private readonly CheckBox _experimentalConfirmed = new() { MinHeight = 28 };
    private readonly Button _browse = new() { Content = "选择启动器", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = Caption(string.Empty);
    private readonly TextBlock _permission = Caption("需要管理员权限时会先询问你；只提升本次启动辅助程序和游戏，Aster 界面保持普通权限。");
    private CancellationTokenSource? _inspection;
    private EndfieldLauncherPathStatus _pathStatus = new();
    private string _selectedPath = string.Empty;
    private bool _updatingPath;
    private bool _inspecting;
    private bool _closed;

    public EndfieldLaunchOptionsDialog(Window owner, IFilePickerService filePicker, EndfieldLaunchOptionsInput input)
    {
        _owner = owner;
        _filePicker = filePicker;
        _input = input;
        Title = "终末地 · 选择启动方式";
        PrimaryButtonText = "保存选择";
        PrimaryButtonStyle = Application.Current.Resources["ThemePrimaryButtonStyle"] as Style;
        CloseButtonText = "取消";
        CloseButtonStyle = Application.Current.Resources["SecondaryButtonStyle"] as Style;
        DefaultButton = ContentDialogButton.Primary;
        Resources["ContentDialogMaxWidth"] = 680d;
        AutomationProperties.SetAutomationId(this, "EndfieldLaunchOptionsDialog");
        AutomationProperties.SetName(_official, "打开官方启动器（推荐）");
        AutomationProperties.SetAutomationId(_official, "EndfieldLaunchOfficialMode");
        AutomationProperties.SetName(_experimental, "直接启动游戏（实验性）");
        AutomationProperties.SetAutomationId(_experimental, "EndfieldLaunchExperimentalMode");
        AutomationProperties.SetName(_paths, "当前服务器官方启动器路径");
        AutomationProperties.SetAutomationId(_paths, "EndfieldOfficialLauncherPath");
        AutomationProperties.SetName(_browse, "选择当前服务器官方启动器 EXE");
        AutomationProperties.SetAutomationId(_browse, "EndfieldChooseOfficialLauncher");
        AutomationProperties.SetAutomationId(_status, "EndfieldLauncherValidationStatus");
        AutomationProperties.SetName(_timeout, "等待游戏启动时限");
        AutomationProperties.SetAutomationId(_timeout, "EndfieldLaunchDetectionTimeout");
        AutomationProperties.SetName(_channelConfirmed, "确认官方启动器与当前服务器关联");
        AutomationProperties.SetAutomationId(_channelConfirmed, "EndfieldConfirmLauncherChannel");
        AutomationProperties.SetAutomationId(_experimentalConfirmed, "EndfieldConfirmExperimentalMode");

        _officialCard = ModeCard(_official, "打开官方启动器（推荐）", "在官方窗口点击“开始游戏”。");
        _experimentalCard = ModeCard(_experimental, "直接启动游戏（实验性）", "不显示官方窗口，可能启动失败。");
        _official.IsChecked = input.Mode != EndfieldLaunchMode.ExperimentalBridge;
        _experimental.IsChecked = input.Mode == EndfieldLaunchMode.ExperimentalBridge;
        _timeout.Items.Add(new ComboBoxItem { Content = "1 分钟", Tag = 60 });
        _timeout.Items.Add(new ComboBoxItem { Content = "3 分钟", Tag = 180 });
        _timeout.Items.Add(new ComboBoxItem { Content = "5 分钟", Tag = 300 });
        _timeout.SelectedIndex = input.DetectionTimeoutSeconds == 60 ? 0 : input.DetectionTimeoutSeconds == 300 ? 2 : 1;
        _channelConfirmed.Content = Caption($"我已核对，这是“{input.ChannelLabel}”使用的启动器。");
        _channelConfirmed.IsChecked = input.IsChannelAssociationConfirmed;
        _experimentalConfirmed.Content = Caption("我了解实验方式可能失败，仍要尝试。");
        foreach (var candidate in input.Candidates) AddPath(candidate.Path, candidate.Label, candidate.Status.Message);
        SelectPath(input.OfficialLauncherPath, resetConfirmation: false);

        var body = new StackPanel { Spacing = 8, Width = 610, MaxWidth = 610 };
        body.Children.Add(new TextBlock
        {
            Text = "终末地新版会检查是否由官方启动器启动，直接打开游戏可能被拒绝。",
            TextWrapping = TextWrapping.Wrap, FontSize = 13
        });
        var choices = new Grid { ColumnSpacing = 10 };
        choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        choices.Children.Add(_officialCard);
        Grid.SetColumn(_experimentalCard, 1);
        choices.Children.Add(_experimentalCard);
        AutomationProperties.SetName(choices, "终末地启动方式");
        AutomationProperties.SetAutomationId(choices, "EndfieldLaunchMode");
        body.Children.Add(choices);

        var pathRow = new Grid { ColumnSpacing = 8 };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathRow.Children.Add(new TextBlock { Text = "官方启动器", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_paths, 1); pathRow.Children.Add(_paths);
        Grid.SetColumn(_browse, 2); pathRow.Children.Add(_browse);
        body.Children.Add(pathRow);
        body.Children.Add(_status);
        body.Children.Add(_channelConfirmed);
        body.Children.Add(_experimentalConfirmed);
        body.Children.Add(_permission);

        var timing = new Grid { ColumnSpacing = 8 };
        timing.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        timing.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        timing.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timing.Children.Add(new TextBlock { Text = "最多等待", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_timeout, 1); timing.Children.Add(_timeout);
        var changeHint = Caption("以后可点启动按钮右侧箭头，更改启动方式。");
        changeHint.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(changeHint, 2); timing.Children.Add(changeHint);
        body.Children.Add(timing);
        Content = body;

        _official.Checked += (_, _) => UpdateState();
        _experimental.Checked += (_, _) => UpdateState();
        _channelConfirmed.Checked += (_, _) => UpdateState();
        _channelConfirmed.Unchecked += (_, _) => UpdateState();
        _experimentalConfirmed.Checked += (_, _) => UpdateState();
        _experimentalConfirmed.Unchecked += (_, _) => UpdateState();
        _paths.SelectionChanged += async (_, _) =>
        {
            if (_updatingPath || _paths.SelectedItem is not ComboBoxItem { Tag: string path }) return;
            SelectPath(path, resetConfirmation: true);
            await InspectAsync(path);
        };
        _browse.Click += BrowseAsync;
        Opened += async (_, _) => await InspectAsync(_selectedPath);
        Closed += (_, _) => { _closed = true; _inspection?.Cancel(); };
        PrimaryButtonClick += (_, args) =>
        {
            if (!CanSave) { args.Cancel = true; return; }
            Result = new(SelectedMode, _selectedPath,
                _timeout.SelectedItem is ComboBoxItem { Tag: int seconds } ? seconds : 180,
                _channelConfirmed.IsChecked == true);
        };
        UpdateState();
    }

    public EndfieldLaunchOptionsResult? Result { get; private set; }
    private EndfieldLaunchMode SelectedMode => _experimental.IsChecked == true
        ? EndfieldLaunchMode.ExperimentalBridge : EndfieldLaunchMode.OfficialLauncher;

    private void AddPath(string path, string label, string details)
    {
        if (string.IsNullOrWhiteSpace(path) || _paths.Items.OfType<ComboBoxItem>().Any(item => string.Equals(item.Tag as string, path, StringComparison.OrdinalIgnoreCase))) return;
        var text = new TextBlock { Text = path, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 470 };
        var item = new ComboBoxItem { Content = text, Tag = path, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(item, label + "，" + path);
        ToolTipService.SetToolTip(item, label + "\n" + path + "\n" + details);
        _paths.Items.Add(item);
    }

    private void SelectPath(string path, bool resetConfirmation)
    {
        if (resetConfirmation && !string.Equals(path, _selectedPath, StringComparison.OrdinalIgnoreCase))
        {
            _channelConfirmed.IsChecked = false;
            _experimentalConfirmed.IsChecked = false;
        }
        _selectedPath = path.Trim();
        _updatingPath = true;
        try
        {
            AddPath(_selectedPath, "所选启动器", string.Empty);
            _paths.SelectedItem = _paths.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag as string, _selectedPath, StringComparison.OrdinalIgnoreCase));
            ToolTipService.SetToolTip(_paths, _selectedPath);
        }
        finally { _updatingPath = false; }
    }

    private async void BrowseAsync(object sender, RoutedEventArgs args)
    {
        _browse.IsEnabled = false;
        try
        {
            var path = await _filePicker.PickExecutableAsync(_owner);
            if (_closed || string.IsNullOrWhiteSpace(path)) return;
            SelectPath(path, resetConfirmation: true);
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
            var status = string.IsNullOrWhiteSpace(path) ? new EndfieldLauncherPathStatus()
                : _input.InspectPathAsync is not null ? await _input.InspectPathAsync(path, inspection.Token)
                : _input.Candidates.FirstOrDefault(candidate => string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase))?.Status
                    ?? new EndfieldLauncherPathStatus { Title = "尚未验证启动器", Message = "请重新选择对应服务器的官方启动器。" };
            if (!_closed && !inspection.IsCancellationRequested) _pathStatus = status;
        }
        catch (OperationCanceledException) when (inspection.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)
        {
            if (!_closed && !inspection.IsCancellationRequested)
                _pathStatus = new() { Title = "无法检查启动器", Message = "请重新选择对应服务器的官方启动器。" };
        }
        finally
        {
            if (ReferenceEquals(_inspection, inspection) && !_closed) { _inspecting = false; UpdateState(); }
        }
    }

    private void UpdateState()
    {
        var experimental = SelectedMode == EndfieldLaunchMode.ExperimentalBridge;
        _experimentalConfirmed.Visibility = experimental ? Visibility.Visible : Visibility.Collapsed;
        _permission.Visibility = experimental ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = _inspecting ? "正在检查启动器…" : !_pathStatus.IsLauncherUsable ? "请选择对应服务器的官方启动器。"
            : experimental && !_pathStatus.SupportsExperimentalBridge ? "未找到可用于实验方式的官方组件，请选择推荐方式。"
            : experimental ? "已找到所需官方组件；实验方式仍可能因游戏更新失效。"
            : "已找到启动器。请核对下方服务器，再保存选择。";
        ToolTipService.SetToolTip(_status, _pathStatus.Title + "\n" + _pathStatus.Message);
        Highlight(_officialCard, !experimental);
        Highlight(_experimentalCard, experimental);
        IsPrimaryButtonEnabled = CanSave;
    }

    private bool CanSave => !_inspecting && _channelConfirmed.IsChecked == true
        && (SelectedMode == EndfieldLaunchMode.ExperimentalBridge
            ? _pathStatus.SupportsExperimentalBridge && _experimentalConfirmed.IsChecked == true
            : _pathStatus.IsLauncherUsable);

    private static Border ModeCard(RadioButton button, string title, string description)
    {
        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(Caption(description));
        button.Content = content;
        return new Border
        {
            Child = button, Padding = new Thickness(8), CornerRadius = new CornerRadius(10),
            Background = Application.Current.Resources["ControlSurfaceBrush"] as Brush,
            BorderThickness = new Thickness(1)
        };
    }

    private static void Highlight(Border card, bool selected) => card.BorderBrush =
        Application.Current.Resources[selected ? "ThemeAccentBrush" : "SubtleStrokeBrush"] as Brush;

    private static TextBlock Caption(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12,
        Style = Application.Current.Resources["CaptionTextStyle"] as Style
    };
}