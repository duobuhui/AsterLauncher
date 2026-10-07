using AsterLauncher.App.Services;
using AsterLauncher.App.ViewModels;
using AsterLauncher.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI.ViewManagement;

namespace AsterLauncher.App.Views;

public sealed partial class GameLibraryPage : Page
{
    private readonly LauncherViewModel _launcher;
    private readonly EndfieldMaintenanceViewModel _endfield;
    private readonly HoYoMaintenanceViewModel _hoyo;
    private readonly IFilePickerService _filePicker;
    private readonly IServiceProvider _services;
    private readonly ILogger<GameLibraryPage> _logger;
    private readonly Dictionary<string, Page> _featurePages = [];
    private readonly UISettings _uiSettings = new();
    private Storyboard? _activeTransition;
    private Storyboard? _railTransition;
    private Storyboard? _railFadeTransition;
    private Storyboard? _launchTransition;
    private Storyboard? _surfaceShadeTransition;
    private long _railRevision;
    private long _railFadeRevision;
    private long _launchRevision;
    private long _shadeRevision;
    private long _contentRevision;
    private bool _isRailCompact;
    private bool _isLaunchExpanded;
    private UIElement? _activeSurface;
    private string _activeFeatureTag = string.Empty;

    public GameLibraryPage(
        GameLibraryViewModel viewModel,
        LauncherViewModel launcher,
        IFilePickerService filePicker,
        IServiceProvider services,
        ILogger<GameLibraryPage> logger)
    {
        ViewModel = viewModel;
        _launcher = launcher;
        _filePicker = filePicker;
        _services = services;
        _endfield = services.GetRequiredService<EndfieldMaintenanceViewModel>();
        _hoyo = services.GetRequiredService<HoYoMaintenanceViewModel>();
        _logger = logger;
        InitializeComponent();
        DataContext = ViewModel;
        EndfieldPanel.DataContext = _endfield;
        HoYoPanel.DataContext = _hoyo;
        _hoyo.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(HoYoMaintenanceViewModel.StageText))
                DispatcherQueue.TryEnqueue(RefreshLaunchSurfaceLayout);
        };
        DownloadStatusPanel.DataContext = _endfield;
        DownloadStatusPanel.SetBinding(VisibilityProperty, new Microsoft.UI.Xaml.Data.Binding { Source=_endfield, Path=new PropertyPath(nameof(EndfieldMaintenanceViewModel.DownloadVisibility)) });
        _endfield.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EndfieldMaintenanceViewModel.CanPreload))
                EndfieldPreloadItem.Visibility = _endfield.CanPreload ? Visibility.Visible : Visibility.Collapsed;
            if (args.PropertyName is nameof(EndfieldMaintenanceViewModel.PlanText)
                or nameof(EndfieldMaintenanceViewModel.StageText)
                or nameof(EndfieldMaintenanceViewModel.SharingText)
                or nameof(EndfieldMaintenanceViewModel.PreloadText))
                DispatcherQueue.TryEnqueue(RefreshLaunchSurfaceLayout);
        };        _launcher.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LauncherViewModel.SelectedEndfieldChannel))
                UpdateEndfieldControls();
        };
    }

    public GameLibraryViewModel ViewModel { get; }

    private void Page_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.Refresh();
            GameList.SelectedItem = ViewModel.CurrentGame;
            if (ViewModel.CurrentGame is not null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    GameList.UpdateLayout();
                    GameList.ScrollIntoView(ViewModel.CurrentGame, ScrollIntoViewAlignment.Leading);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (GameList.ContainerFromItem(ViewModel.CurrentGame) is UIElement container)
                        {
                            container.StartBringIntoView(new BringIntoViewOptions
                            {
                                AnimationDesired = false,
                                VerticalAlignmentRatio = 0.5
                            });
                        }
                    });
                });
            }
            SetRailCompact(false, false);
            UpdateLibraryLayout();
            ShowFeature("overview");
            DispatcherQueue.TryEnqueue(async () =>
            {
                await _endfield.ScanSharingAsync();
                if (_launcher.CurrentGame?.Id == BuiltInGameIds.Endfield)
                    await _endfield.CheckVersionAsync();
                else if (_hoyo.IsSupported) await _hoyo.CheckVersionAsync();
#if DEBUG
                if (HoYoMaintenanceUiFixture.Enabled && Environment.GetEnvironmentVariable("ASTERLAUNCHER_UI_TEST_HOYO_PROGRESS") is { } mode)
                    _hoyo.ApplyProgressFixture(mode);
#endif
            });
#if DEBUG
            if (HoYoMaintenanceUiFixture.Enabled && Content is Grid fixtureHost)
            {
                var probeMenu = new Button { Width = 1, Height = 1, Opacity = 0.01, IsTabStop = false,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(probeMenu, "验证游戏右键菜单");
                probeMenu.Click += (_, _) =>
                {
                    if (_launcher.CurrentGame is { } game && GameList.ContainerFromItem(game) is FrameworkElement item)
                        CreateGameContextMenu(game).ShowAt(item);
                };
                fixtureHost.Children.Add(probeMenu);
            }
            if (Environment.GetEnvironmentVariable("ASTERLAUNCHER_UI_TEST_EXPANDED") == "1")
                DispatcherQueue.TryEnqueue(() => SetLaunchExpanded(true));
            if (Environment.GetEnvironmentVariable("ASTERLAUNCHER_UI_TEST_CONTEXT") == "1")
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (GameList.ContainerFromItem(ViewModel.CurrentGame) is FrameworkElement item)
                        item.ContextFlyout?.ShowAt(item);
                });
#endif
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Game library page initialization failed");
            throw;
        }
    }

    private void HideSidebarFlyouts() { SearchButton.Flyout?.Hide(); FilterButton.Flyout?.Hide(); CompactSearchButton.Flyout?.Hide(); CompactFilterButton.Flyout?.Hide(); }
    public void ShowFeature(string tag)
    {
        if (tag == "calendar" && _launcher.CurrentGame?.Id != BuiltInGameIds.Endfield) tag = "overview";
        HideSidebarFlyouts();
        if (_isLaunchExpanded) SetLaunchExpanded(false);
        UpdateNavigationState(tag);
        var isOverview = tag is "library" or "home" or "overview";
        var nextTag = isOverview ? "overview" : tag;
        if (isOverview && _activeFeatureTag != "overview"
            && _launcher.CurrentGame?.Id == BuiltInGameIds.Endfield)
            _ = _endfield.CheckVersionAsync();
        else if (isOverview && _activeFeatureTag != "overview" && _hoyo.IsSupported)
            _ = _hoyo.CheckVersionAsync();
        if (_activeFeatureTag == nextTag && _activeSurface is not null && AddGamePanel.Visibility == Visibility.Collapsed)
        {
            return;
        }

        var outgoing = _activeSurface;
        var outgoingOpacity = outgoing?.Opacity ?? 1;
        var outgoingOffset = (outgoing?.RenderTransform as TranslateTransform)?.Y ?? 0;
        var revision = ++_contentRevision;
        _activeTransition?.Stop();
        _activeTransition = null;
        foreach (var surface in new UIElement[] { OverviewPanel, FeatureFrame, FeatureTransitionFrame, AddGamePanel })
        {
            surface.IsHitTestVisible = false;
            if (!ReferenceEquals(surface, outgoing))
            {
                surface.Visibility = Visibility.Collapsed;
                if (surface is Frame frame) frame.Content = null;
            }
            surface.Opacity = ReferenceEquals(surface, outgoing) ? outgoingOpacity : 1;
            surface.RenderTransform = ReferenceEquals(surface, outgoing)
                ? new TranslateTransform { Y = outgoingOffset }
                : null;
        }

        UIElement incoming;
        if (isOverview)
        {
            incoming = OverviewPanel;
        }
        else
        {
            if (!_featurePages.TryGetValue(tag, out var page))
            {
                page = tag switch
                {
                    "profiles" => _services.GetRequiredService<LaunchProfilesPage>(),
                    "tools" => _services.GetRequiredService<ToolsPage>(),
                    "gacha" => _services.GetRequiredService<GachaPage>(),
                    "calendar" => _services.GetRequiredService<EndfieldCommunityPage>(),
                    "play-time" => _services.GetRequiredService<PlayActivityPage>(),
                    "game-settings" => _services.GetRequiredService<GameSettingsPage>(),
                    "launcher-settings" => _services.GetRequiredService<SettingsPage>(),
                    "logs" => _services.GetRequiredService<LogsPage>(),
                    _ => _services.GetRequiredService<SettingsPage>()
                };
                _featurePages[tag] = page;
            }

            var destination = ReferenceEquals(outgoing, FeatureFrame) ? FeatureTransitionFrame : FeatureFrame;
            destination.Content = page;
            incoming = destination;
        }

        _activeFeatureTag = nextTag;
        _activeSurface = incoming;
        (App.MainWindow as MainWindow)?.SetWorkspaceOverlay(!isOverview);
        SetSurfaceShades(!isOverview);
        incoming.Visibility = Visibility.Visible;
        incoming.IsHitTestVisible = true;
        if (outgoing is null || ReferenceEquals(outgoing, incoming) || !_uiSettings.AnimationsEnabled)
        {
            if (outgoing is not null && !ReferenceEquals(outgoing, incoming))
            {
                outgoing.Visibility = Visibility.Collapsed;
                if (outgoing is Frame oldFrame) oldFrame.Content = null;
            }
            incoming.Opacity = 1;
            incoming.RenderTransform = null;
            return;
        }

        var incomingTransform = new TranslateTransform { Y = -12 };
        var outgoingTransform = new TranslateTransform { Y = outgoingOffset };
        incoming.RenderTransform = incomingTransform;
        outgoing.RenderTransform = outgoingTransform;
        incoming.Opacity = 0;
        outgoing.Opacity = outgoingOpacity;
        var transition = new Storyboard();
        AddLaunchAnimation(transition, incoming, "Opacity", 0, 1, 240);
        AddLaunchAnimation(transition, incomingTransform, "Y", -12, 0, 240);
        AddLaunchAnimation(transition, outgoing, "Opacity", outgoingOpacity, 0, 190);
        AddLaunchAnimation(transition, outgoingTransform, "Y", outgoingOffset, 8, 190);
        _activeTransition = transition;
        transition.Completed += (_, _) =>
        {
            if (revision != _contentRevision) return;
            transition.Stop();
            outgoing.Visibility = Visibility.Collapsed;
            if (outgoing is Frame oldFrame) oldFrame.Content = null;
            outgoing.Opacity = 1;
            outgoing.RenderTransform = null;
            incoming.Opacity = 1;
            incoming.RenderTransform = null;
            _activeTransition = null;
        };
        transition.Begin();
    }

    private async void GameRow_OnClick(object sender,RoutedEventArgs e)
    {
        if(sender is not FrameworkElement { DataContext: GameCardViewModel game })return;
        GameList.SelectedItem=game;
        if(!ReferenceEquals(game,ViewModel.CurrentGame))await ViewModel.SelectGameAsync(game);
        if(!ReferenceEquals(game,ViewModel.CurrentGame))return;
        UpdateEndfieldControls();ShowFeature("overview");
    }
    private async void GameList_OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not GameCardViewModel game) return;
        if (!ReferenceEquals(game, ViewModel.CurrentGame)) await ViewModel.SelectGameAsync(game);
        if (!ReferenceEquals(game, ViewModel.CurrentGame)) return;
        UpdateEndfieldControls();
        ShowFeature("overview");
    }

    private async void GameList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyAllRailItemStates();
        if (GameList.SelectedItem is not GameCardViewModel game)
        {
            return;
        }

        var changed = !ReferenceEquals(game, ViewModel.CurrentGame);
        if (changed)
        {
            await ViewModel.SelectGameAsync(game);
        }

        if (!ReferenceEquals(game, ViewModel.CurrentGame)) return;
        UpdateEndfieldControls();

        ShowFeature("overview");
        if (changed && game.Id == BuiltInGameIds.Endfield) await _endfield.CheckVersionAsync();
        else if (changed && _hoyo.IsSupported) await _hoyo.CheckVersionAsync();
    }

    private async Task SelectChannelAsync(GameCardViewModel game, EndfieldChannel channel)
    {
        await SelectContextGameAsync(game);
        await _launcher.SelectEndfieldChannelAsync(channel);
        UpdateEndfieldControls();
        await _endfield.CheckVersionAsync();
        await _endfield.ScanSharingAsync();
    }

    private void UpdateEndfieldControls()
    {
        var selected = _launcher.CurrentGame?.Id == BuiltInGameIds.Endfield;
        EndfieldCalendarNavigation.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        EndfieldPanel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        HoYoPanel.Visibility = _hoyo.IsSupported ? Visibility.Visible : Visibility.Collapsed;
        var maintenance = selected || _hoyo.IsSupported;
        DownloadStatusPanel.DataContext = _hoyo.IsSupported ? _hoyo : _endfield;
        DownloadStatusPanel.SetBinding(VisibilityProperty, new Microsoft.UI.Xaml.Data.Binding { Source = DownloadStatusPanel.DataContext, Path = new PropertyPath("DownloadVisibility") });
        MaintenanceDivider.Visibility = maintenance ? Visibility.Visible : Visibility.Collapsed;
        MaintenanceColumn.Width = maintenance ? new GridLength(1.4, GridUnitType.Star) : new GridLength(0);
        LaunchExpansionContent.ColumnSpacing = maintenance ? 24 : 0;
        RefreshLaunchSurfaceLayout();
    }

    private async Task SelectContextGameAsync(GameCardViewModel game)
    {
        if (!ReferenceEquals(game, ViewModel.CurrentGame))
            await ViewModel.SelectGameAsync(game);
        GameList.SelectedItem = game;
        UpdateEndfieldControls();
    }

    private MenuFlyout CreateGameContextMenu(GameCardViewModel game)
    {
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = (Style)Resources["GameContextMenuStyle"] };
        MenuFlyoutItem Add(string text, Func<Task> action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            item.Click += async (_, _) =>
            {
                try { await action(); }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Game context action failed");
                    await ShowMessageAsync("操作未完成", "请查看日志，或重新选择游戏后重试。");
                }
            };
            menu.Items.Add(item);
            return item;
        }
        Add("打开游戏页面", async () => { await SelectContextGameAsync(game); ShowFeature("overview"); });
        if (game.Id == BuiltInGameIds.Endfield)
        {
            var channels = new MenuFlyoutSubItem { Text = "服务器" };
            foreach (var channel in new[] { EndfieldChannel.Official, EndfieldChannel.Bilibili })
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = channel == EndfieldChannel.Official ? "官服" : "哔哩哔哩服",
                    Tag = channel,
                    IsChecked = _launcher.SelectedEndfieldChannel == channel
                };
                item.Click += async (_, _) =>
                {
                    try { await SelectChannelAsync(game, channel); }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception, "Channel selection failed");
                        await ShowMessageAsync("切换未完成", "请重新选择服务器，详情见日志。");
                    }
                };
                channels.Items.Add(item);
            }
            menu.Opening += (_, _) =>
            {
                foreach (var item in channels.Items.OfType<ToggleMenuFlyoutItem>())
                    item.IsChecked = item.Tag is EndfieldChannel value && _launcher.SelectedEndfieldChannel == value;
            };
            menu.Items.Add(channels);
            Add("检查游戏更新", async () => { await SelectContextGameAsync(game); await _endfield.CheckVersionAsync(); });
        }
        if (HoYoInstallationIdentity.HasChannels(game.Id))
        {
            var channels = new MenuFlyoutSubItem { Text = "服务器" };
            foreach (var channel in new[] { HoYoChannel.Official, HoYoChannel.Bilibili })
            {
                var item = new ToggleMenuFlyoutItem { Text = channel == HoYoChannel.Official ? "官服" : "哔哩哔哩服", Tag = channel };
                item.Click += async (_, _) =>
                {
                    await SelectContextGameAsync(game);
                    await _launcher.SelectHoYoChannelAsync(game, channel);
                    _hoyo.Refresh(); UpdateEndfieldControls(); await _hoyo.CheckVersionAsync();
                };
                channels.Items.Add(item);
            }
            menu.Opening += (_, _) =>
            {
                foreach (var item in channels.Items.OfType<ToggleMenuFlyoutItem>())
                    item.IsChecked = item.Tag is HoYoChannel channel && game.State.SelectedHoYoChannel == channel;
            };
            menu.Items.Add(channels);
            Add("检查游戏更新", async () => { await SelectContextGameAsync(game); await _hoyo.CheckVersionAsync(); });
        }
        if (game.Id is BuiltInGameIds.Endfield or BuiltInGameIds.GenshinImpact
            or BuiltInGameIds.HonkaiStarRail or BuiltInGameIds.ZenlessZoneZero)
            Add("抽卡记录", async () => { await SelectContextGameAsync(game); ShowFeature("gacha"); });
        if (game.Id == BuiltInGameIds.Endfield)
            Add("版本日历与兑换码", async () => { await SelectContextGameAsync(game); ShowFeature("calendar"); });
        Add("启动方案", async () => { await SelectContextGameAsync(game); ShowFeature("profiles"); });
        menu.Items.Add(new MenuFlyoutSeparator());
        var folder = Add("打开安装目录", async () =>
        {
            if (game.EffectiveExecutablePath is { } executable && Path.GetDirectoryName(executable) is { } directory)
                await Launcher.LaunchFolderPathAsync(directory);
        });
        menu.Opening += (_, _) => folder.IsEnabled = game.EffectiveExecutablePath is { } executable
            && Directory.Exists(Path.GetDirectoryName(executable));
        Add("游戏设置", async () => { await SelectContextGameAsync(game); ShowFeature("game-settings"); });
        var remove = new MenuFlyoutItem { Text = game.Adapter.Definition.IsCustom ? "移除游戏" : "隐藏游戏", Tag = game.Id };
        remove.Click += RemoveGame_OnClick;
        menu.Items.Add(remove);
        return menu;

    }

    private void ShowChannelPicker(FrameworkElement anchor)
    {
        if (ViewModel.CurrentGame is not { } game) return;
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = (Style)Resources["GameContextMenuStyle"] };
        foreach (var channel in new[] { EndfieldChannel.Official, EndfieldChannel.Bilibili })
        {
            var item = new MenuFlyoutItem { Text = channel == EndfieldChannel.Official ? "官服" : "哔哩哔哩服" };
            item.Click += async (_, _) =>
            {
                try { await SelectChannelAsync(game, channel); }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Channel selection failed");
                    await ShowMessageAsync("切换未完成", "请重新选择服务器，详情见日志。");
                }
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }

    private double GetExpandedLaunchWidth() =>
        Math.Min((_launcher.CurrentGame?.Id == BuiltInGameIds.Endfield || _hoyo.IsSupported) ? 880 : 560,
            Math.Max(234, HeroContent.ActualWidth - HeroContent.Padding.Left - HeroContent.Padding.Right));

    private void RefreshLaunchSurfaceLayout()
    {
        if (!_isLaunchExpanded || _launchTransition is not null) return;
        LaunchSurface.Width = GetExpandedLaunchWidth();
        LaunchSurface.Height = GetExpandedLaunchHeight();
    }
    private async void EndfieldChooseRoot_OnClick(object sender, RoutedEventArgs e)
    {
        var root = await _filePicker.PickGameDirectoryAsync(App.MainWindow);
        if (root is null) return;
        try { await _endfield.SetRootAsync(root); await _endfield.ScanSharingAsync(); }
        catch (Exception exception)
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "安装目录不可用",
                Content = exception.Message,
                CloseButtonText = "知道了"
            }.ShowAsync();
        }
    }

    private async void EndfieldCheck_OnClick(object sender, RoutedEventArgs e) => await _endfield.CheckAsync();

    private async void EndfieldSync_OnClick(object sender, RoutedEventArgs e) => await StartEndfieldSyncAsync();

    private async Task StartEndfieldSyncAsync()
    {
        if (_downloadConfirmationOpen || _endfield.IsBusy || !_endfield.HasSelectedChannel) return;
        var game = _launcher.CurrentGame; var channel = _endfield.Channel;
        _downloadConfirmationOpen = true;
        try
        {
            var suggested = _launcher.SelectedEndfieldInstallation?.InstallRoot;
            if (suggested is null && !string.IsNullOrWhiteSpace(_launcher.GameDownloadDirectory))
                suggested = Path.Combine(_launcher.GameDownloadDirectory, "AsterLauncher", "Endfield", channel == EndfieldChannel.Official ? "Official" : "Bilibili");
            var path = new TextBox { Header = "安装目录", Text = suggested ?? "", PlaceholderText = "选择或输入当前服的独立目录" };
            var choose = new Button { Content = "选择目录", Style = (Style)Application.Current.Resources["FloatingButtonStyle"], VerticalAlignment = VerticalAlignment.Bottom };
            choose.Click += async (_, _) => { var selected = await _filePicker.PickGameDirectoryAsync(App.MainWindow); if (selected is not null) path.Text = selected; };
            var location = new Grid { ColumnSpacing = 8 };
            location.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            location.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(choose, 1); location.Children.Add(path); location.Children.Add(choose);
            var content = new StackPanel { MinWidth = 420, Spacing = 12 };
            content.Children.Add(new TextBlock { Text = _endfield.ChannelText });
            content.Children.Add(location);
            content.Children.Add(new TextBlock { Text = "确认后检查本地文件和所需空间。两服使用独立目录，同卷 NTFS 可自动复用已校验的相同资源；暂停保留进度，取消会清理本次任务的下载缓存。", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
                Title = game?.IsInstalled == true ? "更新游戏" : "下载游戏", Content = content,
                PrimaryButtonText = "开始", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            dialog.PrimaryButtonClick += (_, args) => { if (string.IsNullOrWhiteSpace(path.Text)) { args.Cancel = true; path.Focus(FocusState.Programmatic); } };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !ReferenceEquals(game, _launcher.CurrentGame) || _endfield.Channel != channel) return;
            await _endfield.SetRootAsync(path.Text.Trim(), true);
            if (!ReferenceEquals(game, _launcher.CurrentGame) || _endfield.Channel != channel) return;
            _downloadConfirmationOpen = false;
            await _endfield.SyncAsync(false);
        }
        catch (Exception error) { await ShowMessageAsync("无法开始下载", error is InvalidOperationException or IOException or InvalidDataException ? error.Message : "请检查目录和网络后重试。"); }
        finally { _downloadConfirmationOpen = false; }
    }
    private async void HoYoChooseRoot_OnClick(object sender, RoutedEventArgs e)
    {
        var root = await _filePicker.PickGameDirectoryAsync(App.MainWindow);
        if (root is null) return;
        try { await _hoyo.SetRootAsync(root); }
        catch (Exception ex) { await ShowMessageAsync("安装目录不可用", ex.Message); }
    }
    private async void HoYoScanSharing_OnClick(object sender, RoutedEventArgs e) => await _hoyo.ScanSharingAsync();
    private async void HoYoOptimize_OnClick(object sender, RoutedEventArgs e) => await _hoyo.OptimizeAsync();
    private async void HoYoUnshare_OnClick(object sender, RoutedEventArgs e) => await _hoyo.OptimizeAsync(true);
    private async void HoYoCheck_OnClick(object sender, RoutedEventArgs e) => await _hoyo.CheckAsync();
    private async void HoYoRepair_OnClick(object sender, RoutedEventArgs e)
    {
        if (await ConfirmHoYoChannelAsync()) await _hoyo.SyncAsync(true);
    }
    private async Task<bool> ConfirmHoYoChannelAsync()
    {
        if (_hoyo.NeedsChannelConfirmation)
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
                Title = "确认安装渠道", Content = _hoyo.InstallRoot + "\n\n请确认这是" + _hoyo.ChannelText + "安装。维护将使用所选渠道的官方资源清单。",
                PrimaryButtonText = "确认渠道", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            var game = _launcher.CurrentGame;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !ReferenceEquals(game, _launcher.CurrentGame)) return false;
            await _hoyo.ConfirmOfficialChannelAsync();
        }
        return true;
    }
    private async void HoYoVersion_OnClick(object sender, RoutedEventArgs e) => await _hoyo.CheckVersionAsync();
    private async void HoYoPreload_OnClick(object sender, RoutedEventArgs e)
    {
        if (await ConfirmHoYoChannelAsync()) await _hoyo.PreloadAsync();
    }
    private async void HoYoCancelPreload_OnClick(object sender, RoutedEventArgs e) => await _hoyo.CancelAsync(true);
    private bool _downloadConfirmationOpen;
    private async Task StartHoYoSyncAsync()
    {
        if (_downloadConfirmationOpen || !_hoyo.CanSetRoot) return;
        var game = _launcher.CurrentGame;
        var install = game?.State.HoYoInstallation;
        if (game is null || install is null) return;
        _downloadConfirmationOpen = true;
        var confirmationWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var rootBox = new TextBox { Text = _hoyo.SuggestedRoot, Header = "安装目录", PlaceholderText = "选择或输入独立的游戏目录" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(rootBox, "下载安装目录");
            var browse = new Button { Content = "选择目录", Style = (Style)Application.Current.Resources["FloatingButtonStyle"] };
            browse.Click += async (_, _) =>
            {
                var selected = await _filePicker.PickGameDirectoryAsync(App.MainWindow);
                if (selected is not null) rootBox.Text = selected;
            };
            var languages = new[] { ("中文", "zh-cn"), ("英语", "en-us"), ("日语", "ja-jp"), ("韩语", "ko-kr") };
            var audio = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var choices = languages.Select(language => new CheckBox { Content = language.Item1, Tag = language.Item2,
                IsChecked = install.AudioLanguages.Contains(language.Item2), MinWidth = 0 }).ToArray();
            foreach (var choice in choices) audio.Children.Add(choice);
            var share = new CheckBox { Content = "复用另一服的相同资源", IsChecked = install.ShareResources,
                Visibility = HoYoInstallationIdentity.HasChannels(game.Id) ? Visibility.Visible : Visibility.Collapsed };
            var content = new StackPanel { Spacing = 10, MinWidth = 420 };
            content.Children.Add(new TextBlock { Text = _hoyo.ChannelText, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var location = new Grid { ColumnSpacing = 8 };
            location.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            location.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            browse.VerticalAlignment = VerticalAlignment.Bottom; Grid.SetColumn(browse, 1);
            location.Children.Add(rootBox); location.Children.Add(browse); content.Children.Add(location);
            content.Children.Add(new TextBlock { Text = "下载语音" }); content.Children.Add(audio); content.Children.Add(share);
            content.Children.Add(new TextBlock { Text = "确认后检查所需文件和磁盘空间，只下载缺失或损坏的内容。暂停保留进度，取消任务会删除不再被其他任务使用的缓存。",
                TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            if (HoYoInstallationIdentity.HasChannels(game.Id))
                content.Children.Add(new TextBlock { Text = "复用前需要读取并校验另一服的资源，大型游戏可能耗时较久，任务卡片会显示校验进度。两服使用独立目录；同卷 NTFS 可共享已校验的资源包。使用官方启动器维护前，请先在“更多”中解除共享。",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark,
                Title = game.IsInstalled ? "更新游戏" : "下载游戏",
                Content = content, PrimaryButtonText = "开始", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
            };
#if DEBUG
            if (Environment.GetEnvironmentVariable("ASTERLAUNCHER_UI_TEST_HOYO_SLOW_CHECK") == "1")
                dialog.Opened += (_, _) => _logger.LogInformation("Download confirmation opened after {Milliseconds} ms", confirmationWatch.ElapsedMilliseconds);
#endif
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(rootBox.Text)) { args.Cancel = true; rootBox.Focus(FocusState.Programmatic); }
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !ReferenceEquals(game, _launcher.CurrentGame)
                || !ReferenceEquals(install, game.State.HoYoInstallation)) return;
            await _hoyo.SetRootAsync(rootBox.Text.Trim());
            if (!ReferenceEquals(install, game.State.HoYoInstallation) || !ReferenceEquals(game, _launcher.CurrentGame)) return;
            install.AudioLanguages = choices.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
            install.ShareResources = share.IsChecked == true;
            await _hoyo.EnsureDefaultRootAsync();
            await _hoyo.ConfirmOfficialChannelAsync();
            _downloadConfirmationOpen = false;
            await _hoyo.SyncAsync(false);
        }
        catch (Exception ex) { await ShowMessageAsync("无法开始下载", ex is InvalidOperationException or IOException or InvalidDataException ? ex.Message : "请检查目录和网络后重试。"); }
        finally { _downloadConfirmationOpen = false; }
    }
    private async void EndfieldRepair_OnClick(object sender, RoutedEventArgs e) => await _endfield.SyncAsync(true);
    private async void EndfieldCancel_OnClick(object sender, RoutedEventArgs e) { if (_hoyo.IsSupported) await _hoyo.CancelAsync(); else await _endfield.CancelAsync(); }
    private async void EndfieldCancelPreload_OnClick(object sender, RoutedEventArgs e) => await _endfield.CancelAsync(true);
    private async void EndfieldPause_OnClick(object sender, RoutedEventArgs e)
    {
        if (_hoyo.IsSupported)
        {
            if (_hoyo.CanResume) await _hoyo.ResumeAsync();
            else _hoyo.Pause();
        }
        else _endfield.Pause();
    }
    private async void EndfieldCheckPreload_OnClick(object sender, RoutedEventArgs e) => await _endfield.CheckPreloadAsync();
    private async void EndfieldPreload_OnClick(object sender, RoutedEventArgs e) => await _endfield.PreloadAsync();
    private async void EndfieldOptimize_OnClick(object sender, RoutedEventArgs e) => await _endfield.OptimizeAsync();
    private async void EndfieldUnshare_OnClick(object sender, RoutedEventArgs e) => await _endfield.UnshareAsync();
    private void GameList_OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            args.ItemContainer.ContextFlyout = null;
            return;
        }

        if (args.Item is GameCardViewModel game)
            args.ItemContainer.ContextFlyout = CreateGameContextMenu(game);
        ApplyRailItemState(args.ItemContainer);
    }

    private void GameList_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (GameList.SelectedItem is not GameCardViewModel selected)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            GameList.UpdateLayout();
            if (GameList.ContainerFromItem(selected) is UIElement container)
            {
                container.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = false,
                    VerticalAlignmentRatio = 0.5
                });
            }
        });
    }

    private void FeatureNav_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            ShowFeature(tag);
        }
    }

    private void SidebarFeature_OnClick(object sender, RoutedEventArgs e) => FeatureNav_OnClick(sender, e);

    private void ClearSearch_OnClick(object sender, RoutedEventArgs e) => ViewModel.ClearSearch();

    private void ClearFilters_OnClick(object sender, RoutedEventArgs e) { ViewModel.ClearFilters(); HideSidebarFlyouts(); }

    private void ToggleRail_OnClick(object sender, RoutedEventArgs e) => SetRailCompact(!_isRailCompact, true);

    private void RootPage_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isRailCompact && _railTransition is null)
        {
            GameRail.Width = e.NewSize.Width >= 1500 ? 284 : 248;
        }
        if (_isLaunchExpanded && _launchTransition is null)
        {
            LaunchSurface.Width = GetExpandedLaunchWidth();
            LaunchSurface.Height = GetExpandedLaunchHeight();
        }
        UpdateLibraryLayout();
    }

    private void UpdateLibraryLayout()
    {
        if (LibraryColumns is null || LibraryRightColumn is null) return;
        var available = RootPage.ActualWidth - GameRail.Width - 8 - 56;
        var useTwoColumns = available >= 760;
        LibraryColumns.ColumnSpacing = useTwoColumns ? 16 : 0;
        LibraryColumns.ColumnDefinitions[1].Width = useTwoColumns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(LibraryRightColumn, useTwoColumns ? 1 : 0);
        Grid.SetRow(LibraryRightColumn, useTwoColumns ? 0 : 1);
    }

    private double GetExpandedLaunchHeight()
    {
        var available = HeroContent.ActualHeight - HeroContent.Padding.Top - HeroContent.Padding.Bottom
            - PlayTimeButton.Height - 10;
        LaunchExpansionContent.Measure(new Windows.Foundation.Size(GetExpandedLaunchWidth() - 40, double.PositiveInfinity));
        var desired = LaunchExpansionContent.DesiredSize.Height + 96;
        return Math.Max(230, Math.Min(desired, available));
    }
    private async void Launch_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_hoyo.IsSupported)
            {
                if (_hoyo.IsPrimaryDownloading) { _hoyo.Pause(); return; }
                if (_hoyo.IsBusy) return;
                if (_hoyo.CanResume) { await _hoyo.ResumeAsync(); return; }
                if (_hoyo.StartsMaintenance) { await StartHoYoSyncAsync(); return; }
            }
            if (_launcher.CurrentGame?.Id == BuiltInGameIds.Endfield)
            {
                if (_endfield.IsPrimaryDownloading) { _endfield.Pause(); return; }
                if (!_endfield.HasSelectedChannel)
                {
                    if (GameList.ContainerFromItem(ViewModel.CurrentGame) is ListViewItem item)
                    {
                        item.Focus(FocusState.Programmatic);
                        ShowChannelPicker(item);
                    }
                    return;
                }
                if (_endfield.PrimaryStartsMaintenance) { await StartEndfieldSyncAsync(); return; }
            }
            var result = await ViewModel.LaunchAsync();
            if (result is null)
            {
                await ShowLaunchFailureAsync(ViewModel.LaunchStatusText);
            }
            else if (result.Status != LaunchSessionStatus.Completed)
            {
                var stepDetails = string.Join("\n", result.Steps.Where(step => !step.Result.IsSuccess)
                    .Select(step => $"{step.Step.Name}：{step.Result.Message}"));
                await ShowLaunchFailureAsync(string.IsNullOrWhiteSpace(stepDetails)
                    ? result.Message
                    : stepDetails);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Launch button handler failed");
            await ShowLaunchFailureAsync($"启动未完成：{exception.Message}");
        }
    }

    private async Task ShowLaunchFailureAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "无法启动游戏",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            PrimaryButtonText = "查看日志",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) ShowFeature("logs");
    }

    private void PlayTime_OnClick(object sender, RoutedEventArgs e) => ShowFeature("play-time");
    private void LaunchOptionsButton_OnClick(object sender, RoutedEventArgs e) => SetLaunchExpanded(!_isLaunchExpanded);

    private void SetLaunchExpanded(bool expanded)
    {
        _isLaunchExpanded = expanded;
        var revision = ++_launchRevision;
        LaunchOptionsGlyph.Glyph = expanded ? "\uE70E" : "\uE70D";
        AutomationProperties.SetName(LaunchOptionsButton, expanded ? "收起启动选项" : "展开启动选项");
        var fromHeight = LaunchSurface.ActualHeight > 0 ? LaunchSurface.ActualHeight : LaunchSurface.Height;
        var fromWidth = LaunchSurface.ActualWidth > 0 ? LaunchSurface.ActualWidth : LaunchSurface.Width;
        var fromExpansionOpacity = LaunchExpansion.Opacity;
        var fromDetailsOpacity = CommandDetails.Opacity;
        var fromGlassOpacity = LaunchGlass.Opacity;
        var expansionTransform = LaunchExpansion.RenderTransform as TranslateTransform ?? new TranslateTransform();
        var detailsTransform = CommandDetails.RenderTransform as TranslateTransform ?? new TranslateTransform();
        LaunchExpansion.RenderTransform = expansionTransform;
        CommandDetails.RenderTransform = detailsTransform;
        var fromExpansionY = expansionTransform.Y;
        var fromDetailsY = detailsTransform.Y;
        _launchTransition?.Stop();
        _launchTransition = null;
        LaunchSurface.Width = fromWidth;
        LaunchSurface.Height = fromHeight;
        LaunchExpansion.Opacity = fromExpansionOpacity;
        CommandDetails.Opacity = fromDetailsOpacity;
        LaunchGlass.Opacity = fromGlassOpacity;
        LaunchGlassTint.Opacity = fromGlassOpacity;
        expansionTransform.Y = fromExpansionY;
        detailsTransform.Y = fromDetailsY;
        var targetHeight = expanded ? GetExpandedLaunchHeight() : 60d;
        var targetWidth = expanded
            ? GetExpandedLaunchWidth()
            : 234d;
        LaunchExpansion.IsHitTestVisible = expanded;
        CommandDetails.IsHitTestVisible = expanded;
        if (!_uiSettings.AnimationsEnabled)
        {
            LaunchSurface.Width = targetWidth;
            LaunchSurface.Height = targetHeight;
            LaunchGlass.Opacity = expanded ? 1 : 0;
            LaunchGlassTint.Opacity = expanded ? 1 : 0;
            LaunchExpansion.Opacity = expanded ? 1 : 0;
            CommandDetails.Opacity = expanded ? 1 : 0;
            expansionTransform.Y = expanded ? 0 : 10;
            detailsTransform.Y = expanded ? 0 : 6;
            LaunchExpansion.RenderTransform = expanded ? null : expansionTransform;
            CommandDetails.RenderTransform = expanded ? null : detailsTransform;
            return;
        }

        _launchTransition = new Storyboard();
        AddLaunchAnimation(_launchTransition, LaunchSurface, "Height", fromHeight, targetHeight, 250);
        AddLaunchAnimation(_launchTransition, LaunchSurface, "Width", fromWidth, targetWidth, 250);
        AddLaunchAnimation(_launchTransition, LaunchGlass, "Opacity", fromGlassOpacity, expanded ? 1 : 0, 210);
        AddLaunchAnimation(_launchTransition, LaunchGlassTint, "Opacity", fromGlassOpacity, expanded ? 1 : 0, 210);
        AddLaunchAnimation(_launchTransition, LaunchExpansion, "Opacity", fromExpansionOpacity, expanded ? 1 : 0, 190);
        AddLaunchAnimation(_launchTransition, CommandDetails, "Opacity", fromDetailsOpacity, expanded ? 1 : 0, 190);
        AddLaunchAnimation(_launchTransition, expansionTransform, "Y", fromExpansionY, expanded ? 0 : 10, 230);
        AddLaunchAnimation(_launchTransition, detailsTransform, "Y", fromDetailsY, expanded ? 0 : 6, 230);
        var transition = _launchTransition;
        _launchTransition.Completed += (_, _) =>
        {
            if (revision != _launchRevision) return;
            transition.Stop();
            _launchTransition = null;
            LaunchSurface.Width = targetWidth;
            LaunchSurface.Height = targetHeight;
            LaunchGlass.Opacity = expanded ? 1 : 0;
            LaunchGlassTint.Opacity = expanded ? 1 : 0;
            LaunchExpansion.Opacity = expanded ? 1 : 0;
            CommandDetails.Opacity = expanded ? 1 : 0;
            expansionTransform.Y = expanded ? 0 : 10;
            detailsTransform.Y = expanded ? 0 : 6;
            LaunchExpansion.RenderTransform = expanded ? null : expansionTransform;
            CommandDetails.RenderTransform = expanded ? null : detailsTransform;
        };
        _launchTransition.Begin();
    }

    private static void AddLaunchAnimation(Storyboard storyboard, DependencyObject target, string property, double from, double to, int milliseconds)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EnableDependentAnimation = property is "Width" or "Height",
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private async void SetRailCompact(bool compact, bool animate)
    {
        _isRailCompact = compact;
        var revision = ++_railRevision;
        var startWidth = GameRail.ActualWidth > 0 ? GameRail.ActualWidth : GameRail.Width;
        _railTransition?.Stop();
        _railTransition = null;
        _railFadeTransition?.Stop();

        var targetWidth = compact ? 76d : RootPage.ActualWidth >= 1500 ? 284d : 248d;
        GameRail.Width = startWidth;

        if (!animate || !_uiSettings.AnimationsEnabled || Math.Abs(startWidth - targetWidth) < 0.5)
        {
            GameRail.Width = targetWidth;
            CompleteRailTransition(compact);
            RailInner.Opacity = 1;
            return;
        }

        AnimateRailOpacity(0, 65);
        await Task.Delay(65);
        if (revision != _railRevision) return;

        ExpandedRailHeader.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ExpandedAddButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ExpandedRailFooter.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactRailHeader.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        CompactAddButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        CompactRailFooter.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        ApplyAllRailItemStates();

        var duration = new Duration(TimeSpan.FromMilliseconds(190));
        var widthAnimation = new DoubleAnimation
        {
            From = startWidth,
            To = targetWidth,
            Duration = duration,
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(widthAnimation, GameRail);
        Storyboard.SetTargetProperty(widthAnimation, "Width");

        _railTransition = new Storyboard();
        _railTransition.Children.Add(widthAnimation);
        var transition = _railTransition;
        _railTransition.Completed += (_, _) =>
        {
            if (revision != _railRevision) return;
            transition.Stop();
            _railTransition = null;
            CompleteRailTransition(compact);
            AnimateRailOpacity(1, 85);
        };
        _railTransition.Begin();
    }

    private void AnimateRailOpacity(double target, int milliseconds)
    {
        var revision = ++_railFadeRevision;
        _railFadeTransition?.Stop();
        var animation = new DoubleAnimation
        {
            From = RailInner.Opacity,
            To = target,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(animation, RailInner);
        Storyboard.SetTargetProperty(animation, "Opacity");
        _railFadeTransition = new Storyboard();
        _railFadeTransition.Children.Add(animation);
        var transition = _railFadeTransition;
        _railFadeTransition.Completed += (_, _) =>
        {
            if (revision != _railFadeRevision) return;
            transition.Stop();
            RailInner.Opacity = target;
            _railFadeTransition = null;
        };
        _railFadeTransition.Begin();
    }

    private void CompleteRailTransition(bool compact)
    {
        GameRail.Width = compact ? 76 : RootPage.ActualWidth >= 1500 ? 284 : 248;
        UpdateLibraryLayout();
        ScrollViewer.SetVerticalScrollBarVisibility(GameList, compact ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto);
        ExpandedRailHeader.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ExpandedAddButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ExpandedRailFooter.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactRailHeader.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        CompactAddButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        CompactRailFooter.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        ApplyAllRailItemStates();
    }

    private void ApplyAllRailItemStates()
    {
        foreach (var game in ViewModel.FilteredGames)
        {
            if (GameList.ContainerFromItem(game) is SelectorItem container)
            {
                ApplyRailItemState(container);
            }
        }
    }

    private void ApplyRailItemState(SelectorItem container)
    {
        if (container.ContentTemplateRoot is not FrameworkElement root)
        {
            return;
        }

        if (root.FindName("GameDetails") is FrameworkElement details)
        {
            details.Visibility = _isRailCompact ? Visibility.Collapsed : Visibility.Visible;
        }
        if (root.FindName("GameInstallStatus") is FrameworkElement installStatus)
        {
            installStatus.Visibility = _isRailCompact ? Visibility.Collapsed : Visibility.Visible;
        }
        if (root is Grid itemRoot)
        {
            var leftInset = _isRailCompact ? 11d : 4d;
            itemRoot.Padding = new Thickness(leftInset - (container.IsSelected ? 3d : 0d), 2, _isRailCompact ? 11d : 10d, 2);
        }
        container.BorderThickness = container.IsSelected ? new Thickness(3, 0, 0, 0) : new Thickness(0);
        container.BorderBrush = container.IsSelected
            ? (Brush)Application.Current.Resources["ThemeAccentBrush"]
            : new SolidColorBrush(Colors.Transparent);
    }


    private async void Scan_OnClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ScanAsync();
        if (_launcher.LastScanResults.All(result => result.Kind != ScanResultKind.Found))
        {
            await ShowScanDiagnosticsAsync();
        }
    }

    private async void SetPath_OnClick(object sender, RoutedEventArgs e)
    {
        var executablePath = await _filePicker.PickExecutableAsync(App.MainWindow);
        if (executablePath is null)
        {
            _launcher.ReportManualSelectionCancelled();
            return;
        }

        var result = await _launcher.SetManualExecutableAsync(executablePath);
        if (result.Kind != ScanResultKind.Found)
        {
            await ShowMessageAsync("无法使用此路径", result.Message);
        }
    }

    private async void OpenOfficialDownload_OnClick(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(ViewModel.CurrentGame?.OfficialDownloadUri, UriKind.Absolute, out var uri))
        {
            await Launcher.LaunchUriAsync(uri);
            return;
        }

        await ShowMessageAsync("暂无官方下载信息", "这个自定义游戏没有配置官方下载地址。");
    }

    private void NavigateMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            ShowFeature(tag);
        }
    }

    private void OpenAddGame_OnClick(object sender, RoutedEventArgs e)
    {
        OpenAddGamePage();
    }

    private void OpenAddGamePage()
    {
        ++_contentRevision;
        _activeTransition?.Stop();
        _activeTransition = null;
        if (_isLaunchExpanded) SetLaunchExpanded(false);
        GameList.SelectedItem = null;
        foreach (var surface in new UIElement[] { OverviewPanel, FeatureFrame, FeatureTransitionFrame, AddGamePanel })
        {
            surface.Visibility = Visibility.Collapsed;
            surface.IsHitTestVisible = false;
            surface.Opacity = 1;
            surface.RenderTransform = null;
            if (surface is Frame frame) frame.Content = null;
        }
        AddGamePanel.Visibility = Visibility.Visible;
        AddGamePanel.IsHitTestVisible = true;
        (App.MainWindow as MainWindow)?.SetWorkspaceOverlay(true);
        SetSurfaceShades(true);
        _activeSurface = AddGamePanel;
        _activeFeatureTag = "add";
        ResetAddForm();
        UpdateNavigationState(string.Empty);
        AnimateContent(AddGamePanel, 18);
    }

    private async void LibraryVisibleList_OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        await _launcher.PersistDraggedOrderAsync(false, sender.Items.OfType<GameCardViewModel>().Select(game => game.Id));
        GameList.SelectedItem = ViewModel.CurrentGame;
    }

    private async void LibraryHiddenList_OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        await _launcher.PersistDraggedOrderAsync(true, sender.Items.OfType<GameCardViewModel>().Select(game => game.Id));
    }

    private async void ClearUninstalled_OnClick(object sender, RoutedEventArgs e)
    {
        var candidates = _launcher.GetUninstalledGames();
        if (candidates.Count == 0)
        {
            await ShowMessageAsync("无需清理", "左栏没有未安装的游戏。");
            return;
        }

        var builtInCount = candidates.Count(game => !game.Adapter.Definition.IsCustom);
        var customCount = candidates.Count - builtInCount;
        var details = new List<string>();
        if (builtInCount > 0) details.Add($"{builtInCount} 款内置游戏移入游戏库，路径和方案保留");
        if (customCount > 0) details.Add($"{customCount} 款自定义游戏从启动器配置中移除，游戏文件保留");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = $"清除 {candidates.Count} 款未安装游戏？",
            Content = string.Join("\n", details),
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var removed = await _launcher.ClearUninstalledGamesAsync();
        GameList.SelectedItem = ViewModel.CurrentGame;
        LibraryHintText.Text = $"已清理 {removed} 款未安装游戏";
    }

    private async void RestoreGame_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }
            && _launcher.LibraryGames.FirstOrDefault(game => game.Id == id) is { } game)
        {
            await _launcher.RestoreBuiltInGameAsync(game);
            GameList.SelectedItem = ViewModel.CurrentGame;
        }
    }

    private async void RemoveGame_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }
            || _launcher.Games.FirstOrDefault(game => game.Id == id) is not { } game) return;

        if (game.Adapter.Definition.IsCustom)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ElementTheme.Dark,
                Title = $"移除 {game.DisplayName}？",
                Content = "此自定义游戏及其启动方案将从启动器配置中移除。游戏文件不会删除。",
                PrimaryButtonText = "移除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }

        await _launcher.RemoveGameAsync(game);
        GameList.SelectedItem = ViewModel.CurrentGame;
    }

    private void CancelAddGame_OnClick(object sender, RoutedEventArgs e)
    {
        GameList.SelectedItem = ViewModel.CurrentGame;
        ShowFeature("overview");
    }

    private async void ChooseAddExecutable_OnClick(object sender, RoutedEventArgs e)
    {
        var path = await _filePicker.PickExecutableAsync(App.MainWindow);
        if (path is null)
        {
            return;
        }

        ExecutablePathBox.Text = path;
        if (string.IsNullOrWhiteSpace(GameNameBox.Text))
        {
            GameNameBox.Text = Path.GetFileNameWithoutExtension(path);
        }
    }

    private async void ChooseArtwork_OnClick(object sender, RoutedEventArgs e)
    {
        var path = await _filePicker.PickImageAsync(App.MainWindow);
        if (path is not null)
        {
            ArtworkPathBox.Text = path;
        }
    }

    private async void SaveAddGame_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ExecutablePathBox.Text))
        {
            await ShowMessageAsync("尚未选择游戏", "请先选择明确的游戏 EXE。终末地应选择 Endfield.exe。");
            return;
        }

        var result = await _launcher.AddCustomGameAsync(
            GameNameBox.Text,
            PublisherBox.Text,
            ExecutablePathBox.Text,
            IconGlyphBox.Text,
            ArtworkPathBox.Text);

        if (result.Kind != ScanResultKind.Found)
        {
            await ShowMessageAsync("保存失败", result.Message);
            return;
        }

        ViewModel.Refresh();
        GameList.SelectedItem = ViewModel.CurrentGame;
        ShowFeature("overview");
    }

    private void ResetAddForm()
    {
        ExecutablePathBox.Text = string.Empty;
        GameNameBox.Text = string.Empty;
        PublisherBox.Text = string.Empty;
        IconGlyphBox.Text = string.Empty;
        ArtworkPathBox.Text = string.Empty;
    }

    private async void ShowScanDiagnostics_OnClick(object sender, RoutedEventArgs e) => await ShowScanDiagnosticsAsync();

    private async Task ShowScanDiagnosticsAsync()
    {
        var text = _launcher.LastScanResults.Count == 0
            ? "尚未执行自动查找。"
            : string.Join("\n\n", _launcher.LastScanResults.Select(result => $"{GetScanLabel(result.Kind)} · {result.Source}\n{result.Message}"));
        await ShowMessageAsync("自动查找诊断", text);
    }

    private static string GetScanLabel(ScanResultKind kind) => kind switch
    {
        ScanResultKind.Found => "已找到游戏",
        ScanResultKind.NotFound => "没有找到可信规则",
        ScanResultKind.InvalidPath => "路径存在但 EXE 不存在",
        ScanResultKind.AccessDenied => "权限不足",
        ScanResultKind.Error => "扫描异常",
        ScanResultKind.Cancelled => "用户取消",
        _ => "诊断"
    };

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 460,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
            },
            CloseButtonText = "知道了"
        };
        await dialog.ShowAsync();
    }

    private void AnimateContent(UIElement element, double offset)
    {
        _activeTransition?.Stop();
        if (!_uiSettings.AnimationsEnabled)
        {
            element.Opacity = 1;
            element.RenderTransform = null;
            return;
        }

        var transform = new TranslateTransform { X = offset };
        element.RenderTransform = transform;
        element.Opacity = 0;

        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacity = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = easing };
        var translation = new DoubleAnimation { To = 0, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(opacity, element);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(translation, transform);
        Storyboard.SetTargetProperty(translation, "X");

        var transition = new Storyboard();
        transition.Children.Add(opacity);
        transition.Children.Add(translation);
        _activeTransition = transition;
        transition.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_activeTransition, transition)) return;
            transition.Stop();
            element.Opacity = 1;
            element.RenderTransform = null;
            _activeTransition = null;
        };
        transition.Begin();
    }

    private void SetSurfaceShades(bool visible)
    {
        var revision = ++_shadeRevision;
        var from = PageBackgroundShade.Opacity;
        _surfaceShadeTransition?.Stop();
        PageBackgroundShade.Opacity = from;
        var target = visible ? 1d : 0d;
        var targets = new[] { PageBackgroundShade };
        if (!_uiSettings.AnimationsEnabled)
        {
            foreach (var shade in targets) shade.Opacity = target;
            return;
        }

        _surfaceShadeTransition = new Storyboard();
        foreach (var shade in targets)
        {
            var opacity = new DoubleAnimation
            {
                From = from,
                To = target,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(opacity, shade);
            Storyboard.SetTargetProperty(opacity, "Opacity");
            _surfaceShadeTransition.Children.Add(opacity);
        }
        _surfaceShadeTransition.Completed += (_, _) =>
        {
            if (revision != _shadeRevision) return;
            foreach (var shade in targets) shade.Opacity = target;
            _surfaceShadeTransition = null;
        };
        _surfaceShadeTransition.Begin();
    }

    private void UpdateNavigationState(string tag)
    {
        FeatureTitleText.Text = tag switch { "calendar" => "版本日历", "gacha" => "抽卡记录", "tools" => "辅助工具", "profiles" => "启动方案", "game-settings" => "游戏设置", "launcher-settings" => "启动器设置", "logs" => "日志", "play-time" => "游戏时长", "" or "add" => "游戏库", _ => "" };
        FeatureTitleText.Visibility = FeatureTitleText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var button in SecondaryNavigation.Children.OfType<Button>())
        {
            var selected = string.Equals(button.Tag as string, tag, StringComparison.OrdinalIgnoreCase)
                || ((tag is "library" or "home") && string.Equals(button.Tag as string, "overview", StringComparison.OrdinalIgnoreCase));
            button.Opacity = 1;
            if(selected) button.Foreground = (Brush)Application.Current.Resources["PageTitleBrush"];
            else button.ClearValue(Control.ForegroundProperty);
            if (button.Content is IconElement icon)
                icon.SetBinding(IconElement.ForegroundProperty, new Microsoft.UI.Xaml.Data.Binding { Source = button, Path = new PropertyPath("Foreground") });
            button.Background = selected
                ? (Brush)Application.Current.Resources["ThemeAccentSoftBrush"]
                : new SolidColorBrush(Colors.Transparent);
        }
    }
}
