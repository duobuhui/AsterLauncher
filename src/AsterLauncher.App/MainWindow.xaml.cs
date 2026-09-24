using AsterLauncher.App.Services;
using AsterLauncher.App.ViewModels;
using AsterLauncher.App.Views;
using AsterLauncher.Core;
using AsterLauncher.Tray;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Runtime.InteropServices;
using WinRT.Interop;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace AsterLauncher.App;

public sealed partial class MainWindow : Window
{
    private const int DwmwaBorderColor = 34;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const uint WmSizing = 0x0214;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const double MinimumClientWidth = 960d;
    private const double MinimumClientHeight = 540d;
    private const double WallpaperAspect = 16d / 9d;

    private readonly DialogLaunchDecisionService _decisionService;
    private readonly GameLibraryPage _gameLibraryPage;
    private readonly IFilePickerService _filePicker;
    private readonly LauncherViewModel _launcher;
    private readonly ILogger<MainWindow> _logger;
    private readonly UISettings _uiSettings = new();
    private readonly TrayIconService _trayIcon;
    private bool _allowExit;
    private GameCardViewModel? _observedGame;
    private ImageSource? _requestedWallpaper;
    private readonly Dictionary<ImageSource, Image> _wallpaperLayers = [];
    private Storyboard? _wallpaperTransition;
    private Action? _pendingWallpaperReveal;
    private long _wallpaperRevision;
    private Storyboard? _workspaceTransition;
    private long _workspaceRevision;
    private bool _initialized;
    private readonly WindowSubclassProc _windowSubclass;
    private IntPtr _windowHandle;

    public MainWindow(IServiceProvider services, DialogLaunchDecisionService decisionService)
    {
        _windowSubclass = OnWindowSizing;
        _decisionService = decisionService;
        _logger = services.GetRequiredService<ILogger<MainWindow>>();
        _launcher = services.GetRequiredService<LauncherViewModel>();
        _filePicker = services.GetRequiredService<IFilePickerService>();
        InitializeComponent();
        RootGrid.DataContext = _launcher;
        _gameLibraryPage = services.GetRequiredService<GameLibraryPage>();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        ConfigureWindowChrome();
        _trayIcon = new TrayIconService();
        _trayIcon.RestoreRequested += (_, _) => DispatcherQueue.TryEnqueue(RestoreFromTray);
        _trayIcon.ExitRequested += (_, _) => DispatcherQueue.TryEnqueue(ExitFromTray);
        AppWindow.Closing += OnAppWindowClosing;
        Closed += (_, _) =>
        {
            RemoveWindowSubclass(_windowHandle, _windowSubclass, UIntPtr.Zero);
            _trayIcon.Dispose();
        };
        _launcher.ThemeChanged += (_, _) => ApplyTheme();
        _launcher.PropertyChanged += Launcher_OnPropertyChanged;
        UpdateWallpaper();
    }

    private void RootGrid_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _decisionService.Attach(RootGrid.XamlRoot, DispatcherQueue);
            ApplyTheme();
            if (!_initialized)
            {
                _initialized = true;
                var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1d;
                var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
                _ = GetWindowRect(_windowHandle, out var outerBounds);
                _ = GetClientRect(_windowHandle, out var clientBounds);
                var chromeWidth = Math.Max(0, outerBounds.Right - outerBounds.Left - clientBounds.Right);
                var chromeHeight = Math.Max(0, outerBounds.Bottom - outerBounds.Top - clientBounds.Bottom);
                var targetWidth = (int)Math.Min(1280 * scale, displayArea.WorkArea.Width - 40 * scale);
                var targetHeight = (int)Math.Round((targetWidth - chromeWidth) / WallpaperAspect) + chromeHeight;
                if (targetHeight > displayArea.WorkArea.Height - 40 * scale)
                {
                    targetHeight = (int)(displayArea.WorkArea.Height - 40 * scale);
                    targetWidth = (int)Math.Round((targetHeight - chromeHeight) * WallpaperAspect) + chromeWidth;
                }
                targetWidth = Math.Max(targetWidth, (int)Math.Ceiling(MinimumClientWidth * scale) + chromeWidth);
                targetHeight = (int)Math.Round((targetWidth - chromeWidth) / WallpaperAspect) + chromeHeight;
                var targetX = displayArea.WorkArea.X + Math.Max(0, (displayArea.WorkArea.Width - targetWidth) / 2);
                var targetY = displayArea.WorkArea.Y + Math.Max(0, (displayArea.WorkArea.Height - targetHeight) / 2);
                if (_launcher.NeedsFirstRunGuide)
                {
                    WallpaperHost.Visibility = Visibility.Collapsed;
                    GuideBackdrop.Visibility = Visibility.Visible;
                    ContentFrame.Content = new FirstRunGuidePage(_launcher, _filePicker, () =>
                    {
                        GuideBackdrop.Visibility = Visibility.Collapsed;
                        WallpaperHost.Visibility = Visibility.Visible;
                        ContentFrame.Content = _gameLibraryPage;
                    });
                }
                else
                {
                    ContentFrame.Content = _gameLibraryPage;
                }
                DispatcherQueue.TryEnqueue(() =>
                {
                    _logger.LogInformation("Applying initial window bounds {X},{Y} {Width}x{Height} at rasterization scale {Scale}", targetX, targetY, targetWidth, targetHeight, scale);
                    AppWindow.MoveAndResize(new RectInt32(targetX, targetY, targetWidth, targetHeight));
                });

            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Root visual initialization failed");
            throw;
        }
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = _launcher.ThemePreference switch
        {
            LauncherThemePreference.Dark => ElementTheme.Dark,
            LauncherThemePreference.Light => ElementTheme.Light,
            LauncherThemePreference.TyphonPurple or LauncherThemePreference.ElysiaPink or LauncherThemePreference.PaimonWhite => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        var (title, button, buttonText, glass, backing, accent, soft, stroke) = _launcher.ThemePreference switch
        {
            LauncherThemePreference.TyphonPurple => ("#CBB9FF", "#7452B7", "#FFFFFF", "#AA3B2764", "#99513B7E", "#B99CFF", "#665A3999", "#A78364CB"),
            LauncherThemePreference.ElysiaPink => ("#FFC1DE", "#DB6AA6", "#23141E", "#AA65304D", "#995F304B", "#FF9ACB", "#667C3A60", "#A8E891BD"),
            LauncherThemePreference.PaimonWhite => ("#FFF4D9", "#F7F0DE", "#2D2A37", "#AA666977", "#99818491", "#FFF0CD", "#667C7983", "#A8E6DFCE"),
            _ => ("#C8BFFF", "#8975FF", "#FFFFFF", "#991B173A", "#664A3C88", "#A797FF", "#558975FF", "#889E8BFF")
        };
        SetPaletteBrush("PageTitleBrush", title);
        SetPaletteBrush("LaunchButtonBrush", button);
        SetPaletteBrush("LaunchButtonTextBrush", buttonText);
        SetPaletteBrush("ThemeGlassTintBrush", glass);
        SetPaletteBrush("ThemeTextBackingBrush", backing);
        SetPaletteBrush("ThemeAccentBrush", accent);
        SetPaletteBrush("ThemeAccentSoftBrush", soft);
        SetPaletteBrush("ThemeStrokeBrush", stroke);
    }

    private static void SetPaletteBrush(string key, string hex)
    {
        if (Application.Current.Resources[key] is SolidColorBrush brush)
        {
            var value = Convert.ToUInt32(hex[1..], 16);
            var alpha = hex.Length == 7 ? (byte)255 : (byte)(value >> 24);
            brush.Color = ColorHelper.FromArgb(alpha, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        }
    }

    private void Launcher_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LauncherViewModel.CurrentGame))
        {
            UpdateWallpaper();
        }
    }

    private void ObservedGame_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameCardViewModel.BackgroundImage))
        {
            UpdateWallpaper();
        }
    }

    private void UpdateWallpaper()
    {
        var game = _launcher.CurrentGame;
        if (!ReferenceEquals(_observedGame, game))
        {
            if (_observedGame is not null)
            {
                _observedGame.PropertyChanged -= ObservedGame_OnPropertyChanged;
            }
            _observedGame = game;
            if (game is not null)
            {
                game.PropertyChanged += ObservedGame_OnPropertyChanged;
            }
        }

        var next = game?.BackgroundImage;
        RootGrid.Background = game?.HeroBrush ?? new SolidColorBrush(ColorHelper.FromArgb(255, 16, 24, 36));
        if (ReferenceEquals(_requestedWallpaper, next)) return;

        _requestedWallpaper = next;
        var revision = ++_wallpaperRevision;
        _wallpaperTransition?.Stop();
        _wallpaperTransition = null;
        if (next is null)
        {
            _pendingWallpaperReveal = null;
            foreach (var layer in _wallpaperLayers.Values) layer.Opacity = 0;
            return;
        }

        if (!_wallpaperLayers.TryGetValue(next, out var image))
        {
            image = new Image { Stretch = Stretch.Uniform, Opacity = 0, IsHitTestVisible = false };
            _wallpaperLayers[next] = image;
            image.ImageOpened += (_, _) =>
            {
                if (ReferenceEquals(_requestedWallpaper, next)) _pendingWallpaperReveal?.Invoke();
            };
            image.ImageFailed += (_, _) =>
            {
                WallpaperHost.Children.Remove(image);
                _wallpaperLayers.Remove(next);
                if (!ReferenceEquals(_requestedWallpaper, next)) return;
                _logger.LogWarning("Wallpaper image could not be loaded for {GameId}; using gradient fallback", game?.Id);
                foreach (var layer in _wallpaperLayers.Values) layer.Opacity = 0;
            };
            WallpaperHost.Children.Add(image);
            image.Source = next;
            _logger.LogInformation("Wallpaper source opened for {GameId}", game?.Id);
        }
        else
        {
            WallpaperHost.Children.Remove(image);
            WallpaperHost.Children.Add(image);
        }

        var revealed = false;
        void RevealImage()
        {
            if (revealed) return;
            revealed = true;
            if (revision != _wallpaperRevision) return;

            if (!_uiSettings.AnimationsEnabled)
            {
                image.Opacity = 1;
                HideOtherWallpaperLayers(image);
                return;
            }

            var reveal = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(reveal, image);
            Storyboard.SetTargetProperty(reveal, "Opacity");
            _wallpaperTransition = new Storyboard();
            _wallpaperTransition.Children.Add(reveal);
            _wallpaperTransition.Completed += (_, _) =>
            {
                if (revision == _wallpaperRevision)
                {
                    image.Opacity = 1;
                    HideOtherWallpaperLayers(image);
                    _wallpaperTransition = null;
                }
            };
            _wallpaperTransition.Begin();
        }
        _pendingWallpaperReveal = RevealImage;
        // An already-decoded BitmapImage may not raise ImageOpened again when attached to a new Image.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (next is Microsoft.UI.Xaml.Media.Imaging.BitmapImage { PixelWidth: > 0 }) RevealImage();
        });
    }

    private void HideOtherWallpaperLayers(Image current)
    {
        foreach (var layer in _wallpaperLayers.Values)
        {
            if (!ReferenceEquals(layer, current)) layer.Opacity = 0;
        }
    }

    public void SetWorkspaceOverlay(bool visible)
    {
        var revision = ++_workspaceRevision;
        var from = WorkspaceScrim.Opacity;
        _workspaceTransition?.Stop();
        WorkspaceScrim.Visibility = Visibility.Visible;
        WorkspaceScrim.Opacity = from;
        var target = visible ? 1d : 0d;
        if (!_uiSettings.AnimationsEnabled)
        {
            WorkspaceScrim.Opacity = target;
            WorkspaceScrim.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        var opacity = new DoubleAnimation
        {
            From = from,
            To = target,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(opacity, WorkspaceScrim);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        _workspaceTransition = new Storyboard();
        _workspaceTransition.Children.Add(opacity);
        _workspaceTransition.Completed += (_, _) =>
        {
            if (revision != _workspaceRevision) return;
            WorkspaceScrim.Opacity = target;
            if (!visible) WorkspaceScrim.Visibility = Visibility.Collapsed;
        };
        _workspaceTransition.Begin();
    }

    public void NavigateTo(string tag)
    {
        ContentFrame.Content = _gameLibraryPage;
        _gameLibraryPage.ShowFeature(tag);
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowExit || _launcher.CloseButtonBehavior != CloseButtonBehavior.MinimizeToTray) return;
        args.Cancel = true;
        try
        {
            _trayIcon.Show();
            ShowWindow(_windowHandle, 0);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not add system tray icon; keeping launcher window visible");
        }
    }

    private void RestoreFromTray()
    {
        ShowWindow(_windowHandle, 9);
        _ = SetForegroundWindow(_windowHandle);
        _trayIcon.Hide();
    }

    private void ExitFromTray()
    {
        _allowExit = true;
        _trayIcon.Hide();
        Close();
    }

    private void ConfigureWindowChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
        }
        AppWindow.TitleBar.BackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.InactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(170, 255, 255, 255);
        AppWindow.TitleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(42, 255, 255, 255);
        AppWindow.TitleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(66, 255, 255, 255);

        var windowHandle = WindowNative.GetWindowHandle(this);
        _windowHandle = windowHandle;
        if (!SetWindowSubclass(windowHandle, _windowSubclass, UIntPtr.Zero, UIntPtr.Zero))
        {
            _logger.LogWarning("Could not enforce the 16:9 aspect ratio during interactive resizing");
        }
        var borderColor = DwmColorNone;
        _ = DwmSetWindowAttribute(windowHandle, DwmwaBorderColor, ref borderColor, sizeof(uint));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref uint attributeValue, int attributeSize);

    private IntPtr OnWindowSizing(IntPtr windowHandle, uint message, IntPtr edge, IntPtr rectangle, UIntPtr subclassId, UIntPtr referenceData)
    {
        if (message == WmGetMinMaxInfo && rectangle != IntPtr.Zero)
        {
            var defaultResult = DefSubclassProc(windowHandle, message, edge, rectangle);
            var limits = Marshal.PtrToStructure<NativeMinMaxInfo>(rectangle);
            var scale = GetDpiForWindow(windowHandle) / 96d;
            _ = GetWindowRect(windowHandle, out var outer);
            _ = GetClientRect(windowHandle, out var client);
            var chromeWidth = Math.Max(0, outer.Right - outer.Left - client.Right);
            var chromeHeight = Math.Max(0, outer.Bottom - outer.Top - client.Bottom);
            limits.MinTrackSize.X = (int)Math.Ceiling(MinimumClientWidth * scale) + chromeWidth;
            limits.MinTrackSize.Y = (int)Math.Ceiling(MinimumClientHeight * scale) + chromeHeight;
            Marshal.StructureToPtr(limits, rectangle, false);
            return defaultResult;
        }
        if (message == WmSizing && rectangle != IntPtr.Zero)
        {
            var bounds = Marshal.PtrToStructure<NativeRect>(rectangle);
            if (GetWindowRect(windowHandle, out var previous) && GetClientRect(windowHandle, out var client))
            {
                var chromeWidth = Math.Max(0, previous.Right - previous.Left - client.Right);
                var chromeHeight = Math.Max(0, previous.Bottom - previous.Top - client.Bottom);
                var requestedWidth = Math.Max(1, bounds.Right - bounds.Left - chromeWidth);
                var requestedHeight = Math.Max(1, bounds.Bottom - bounds.Top - chromeHeight);
                var side = edge.ToInt32();
                var resizeFromHeight = side is 3 or 6;
                var minimumWidth = (int)Math.Ceiling(MinimumClientWidth * GetDpiForWindow(windowHandle) / 96d);
                var clientWidth = resizeFromHeight
                    ? Math.Max(minimumWidth, (int)Math.Round(requestedHeight * WallpaperAspect))
                    : Math.Max(minimumWidth, requestedWidth);
                var clientHeight = (int)Math.Round(clientWidth / WallpaperAspect);
                var width = clientWidth + chromeWidth;
                var height = clientHeight + chromeHeight;
                if (side is 1 or 4 or 7) bounds.Left = bounds.Right - width;
                else bounds.Right = bounds.Left + width;
                if (side is 3 or 4 or 5) bounds.Top = bounds.Bottom - height;
                else bounds.Bottom = bounds.Top + height;
                Marshal.StructureToPtr(bounds, rectangle, false);
                return new IntPtr(1);
            }
        }

        return DefSubclassProc(windowHandle, message, edge, rectangle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }


    private delegate IntPtr WindowSubclassProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr windowHandle, WindowSubclassProc callback, UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr windowHandle, WindowSubclassProc callback, UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr windowHandle, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int showCommand);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

}
