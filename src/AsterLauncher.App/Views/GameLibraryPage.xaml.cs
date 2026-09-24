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
        _logger = logger;
        InitializeComponent();
        DataContext = ViewModel;
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
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Game library page initialization failed");
            throw;
        }
    }

    public void ShowFeature(string tag)
    {
        if (_isLaunchExpanded) SetLaunchExpanded(false);
        UpdateNavigationState(tag);
        var isOverview = tag is "library" or "home" or "overview";
        var nextTag = isOverview ? "overview" : tag;
        if (_activeFeatureTag == nextTag && _activeSurface is not null && AddGamePanel.Visibility == Visibility.Collapsed)
        {
            return;
        }

        var outgoing = _activeSurface;
        var outgoingOpacity = outgoing?.Opacity ?? 1;
        var outgoingOffset = (outgoing?.RenderTransform as TranslateTransform)?.Y ?? 0;
        var revision = ++_contentRevision;
        _activeTransition?.Stop();
        foreach (var surface in new UIElement[] { OverviewPanel, FeatureFrame, FeatureTransitionFrame, AddGamePanel })
        {
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

        if (!changed && _activeFeatureTag == "add") return;
        ShowFeature("overview");
    }

    private void GameList_OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            return;
        }

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

    private void ClearFilters_OnClick(object sender, RoutedEventArgs e) => ViewModel.ClearFilters();

    private void ToggleRail_OnClick(object sender, RoutedEventArgs e) => SetRailCompact(!_isRailCompact, true);

    private void RootPage_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isRailCompact && _railTransition is null)
        {
            GameRail.Width = e.NewSize.Width >= 1500 ? 284 : 248;
        }
        if (_isLaunchExpanded && _launchTransition is null)
        {
            LaunchSurface.Width = Math.Min(560, Math.Max(234, HeroContent.ActualWidth - HeroContent.Padding.Left - HeroContent.Padding.Right));
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
        return Math.Max(230d, Math.Min(HeroContent.ActualWidth < 900 ? 302d : 264d, available));
    }

    private async void Launch_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
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
        var expansionTransform = (TranslateTransform)LaunchExpansion.RenderTransform;
        var detailsTransform = (TranslateTransform)CommandDetails.RenderTransform;
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
            ? Math.Min(560, Math.Max(234, HeroContent.ActualWidth - HeroContent.Padding.Left - HeroContent.Padding.Right))
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
        _launchTransition.Completed += (_, _) =>
        {
            if (revision != _launchRevision) return;
            _launchTransition = null;
            LaunchSurface.Width = targetWidth;
            LaunchSurface.Height = targetHeight;
            LaunchGlass.Opacity = expanded ? 1 : 0;
            LaunchGlassTint.Opacity = expanded ? 1 : 0;
            LaunchExpansion.Opacity = expanded ? 1 : 0;
            CommandDetails.Opacity = expanded ? 1 : 0;
            expansionTransform.Y = expanded ? 0 : 10;
            detailsTransform.Y = expanded ? 0 : 6;
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
        _railTransition.Completed += (_, _) =>
        {
            if (revision != _railRevision) return;
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
        _railFadeTransition.Completed += (_, _) =>
        {
            if (revision == _railFadeRevision) RailInner.Opacity = target;
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
        GameList.SelectedItem = null;
        OverviewPanel.Visibility = Visibility.Collapsed;
        FeatureFrame.Visibility = Visibility.Collapsed;
        AddGamePanel.Visibility = Visibility.Visible;
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

        _activeTransition = new Storyboard();
        _activeTransition.Children.Add(opacity);
        _activeTransition.Children.Add(translation);
        _activeTransition.Begin();
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
        foreach (var button in SecondaryNavigation.Children.OfType<Button>())
        {
            var selected = string.Equals(button.Tag as string, tag, StringComparison.OrdinalIgnoreCase)
                || ((tag is "library" or "home") && string.Equals(button.Tag as string, "overview", StringComparison.OrdinalIgnoreCase));
            button.Opacity = 1;
            button.Foreground = selected
                ? (Brush)Application.Current.Resources["PageTitleBrush"]
                : new SolidColorBrush(Colors.White);
            if (button.Content is IconElement icon) icon.Foreground = button.Foreground;
            button.Background = selected
                ? (Brush)Application.Current.Resources["ThemeAccentSoftBrush"]
                : new SolidColorBrush(Colors.Transparent);
        }
    }
}
