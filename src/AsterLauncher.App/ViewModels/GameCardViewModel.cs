using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;

namespace AsterLauncher.App.ViewModels;

public sealed class GameCardViewModel : ObservableObject
{
    private static readonly Dictionary<string, CachedImage> ImageCache = new(StringComparer.OrdinalIgnoreCase);
    private sealed record CachedImage(DateTime LastWriteUtc, long Length, BitmapImage Image);
    private bool _isRunning;
    private ImageSource? _backgroundImage;
    private ImageSource? _iconImage;

    public GameCardViewModel(IGameAdapter adapter, GameUserState state)
    {
        Adapter = adapter;
        State = state;
        HeroBrush = CreateGradient(adapter.Definition.GradientStart, adapter.Definition.GradientEnd);
        _backgroundImage = ResolveArtwork(adapter.Definition, state.ArtworkPath);
        _iconImage = ResolvePackagedImage(adapter.Definition.IconAssetPath);
    }

    public IGameAdapter Adapter { get; }

    public GameUserState State { get; }

    public string Id => Adapter.Definition.Id;

    public string DisplayName => string.IsNullOrWhiteSpace(State.DisplayNameOverride) ? Adapter.Definition.DisplayName : State.DisplayNameOverride;

    public string Publisher => string.IsNullOrWhiteSpace(State.PublisherOverride) ? Adapter.Definition.Publisher : State.PublisherOverride;

    public string Description => Adapter.Definition.Description;

    public string IconGlyph => string.IsNullOrWhiteSpace(State.IconGlyphOverride) ? Adapter.Definition.IconGlyph : State.IconGlyphOverride;

    public string GradientStart => Adapter.Definition.GradientStart;

    public string GradientEnd => Adapter.Definition.GradientEnd;

    public Brush HeroBrush { get; }

    public ImageSource? BackgroundImage
    {
        get => _backgroundImage;
        private set => SetProperty(ref _backgroundImage, value);
    }

    public ImageSource? IconImage
    {
        get => _iconImage;
        private set
        {
            if (SetProperty(ref _iconImage, value))
            {
                OnPropertyChanged(nameof(IconImageVisibility));
                OnPropertyChanged(nameof(IconFallbackVisibility));
            }
        }
    }

    public Visibility IconImageVisibility => IconImage is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility IconFallbackVisibility => IconImage is null ? Visibility.Visible : Visibility.Collapsed;

    public string? OfficialDownloadUri => Adapter.Definition.OfficialDownloadUri;

    public string DownloadDescription => string.IsNullOrWhiteSpace(Adapter.Definition.DownloadDescription)
        ? "暂无官方下载信息。"
        : Adapter.Definition.DownloadDescription;

    public bool IsInstalled => State.IsInstalled;

    public string InstallStatus => IsInstalled ? "已安装" : "未找到";

    public string InstallIconGlyph => IsInstalled ? "\uE73E" : "\uE783";

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(RunningStatusText));
                OnPropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public string RunningStatusText => IsRunning ? "运行中" : "未运行";

    public Brush StatusBrush => new SolidColorBrush(IsRunning ? ColorHelper.FromArgb(255, 85, 214, 160) : ColorHelper.FromArgb(255, 145, 154, 177));

    public string ExecutablePath => string.IsNullOrWhiteSpace(State.ExecutablePath) ? "尚未指定" : State.ExecutablePath;

    public string LastPlayedText => State.LastPlayedAt is null
        ? "从未启动"
        : State.LastPlayedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string TotalPlayTimeText
    {
        get
        {
            var duration = TimeSpan.FromSeconds(State.TotalPlaySeconds);
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分钟"
                : $"{Math.Max(0, duration.Minutes)} 分钟";
        }
    }

    public string LastSessionPlayTimeText => State.GetLastSessionPlaySeconds() is { } seconds
        ? FormatDuration(seconds)
        : "暂无数据";

    public string ThisWeekPlayTimeText => FormatDuration(State.GetThisWeekPlaySeconds(DateTimeOffset.Now));

    public string CompactPlayTimeText
    {
        get
        {
            var duration = TimeSpan.FromSeconds(Math.Max(0, State.TotalPlaySeconds));
            return duration.TotalHours >= 1 ? $"{duration.TotalHours:0.#} 小时" : $"{duration.Minutes} 分钟";
        }
    }

    public string VersionText
    {
        get
        {
            if (!IsInstalled)
            {
                return "暂无数据";
            }

            try
            {
                var info = FileVersionInfo.GetVersionInfo(State.ExecutablePath!);
                return string.IsNullOrWhiteSpace(info.ProductVersion)
                    ? string.IsNullOrWhiteSpace(info.FileVersion) ? "暂无数据" : info.FileVersion
                    : info.ProductVersion;
            }
            catch
            {
                return "暂无数据";
            }
        }
    }

    public void SetRunning(bool isRunning) => IsRunning = isRunning;

    public void Refresh()
    {
        BackgroundImage = ResolveArtwork(Adapter.Definition, State.ArtworkPath);
        IconImage = ResolvePackagedImage(Adapter.Definition.IconAssetPath);
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Publisher));
        OnPropertyChanged(nameof(IconGlyph));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(InstallStatus));
        OnPropertyChanged(nameof(InstallIconGlyph));
        OnPropertyChanged(nameof(ExecutablePath));
        OnPropertyChanged(nameof(LastPlayedText));
        OnPropertyChanged(nameof(TotalPlayTimeText));
        OnPropertyChanged(nameof(LastSessionPlayTimeText));
        OnPropertyChanged(nameof(ThisWeekPlayTimeText));
        OnPropertyChanged(nameof(CompactPlayTimeText));
        OnPropertyChanged(nameof(VersionText));
    }

    private static string FormatDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分钟";
        }

        return $"{Math.Max(0, duration.Minutes)} 分钟";
    }

    private static Brush CreateGradient(string start, string end) => new LinearGradientBrush
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(1, 1),
        GradientStops =
        {
            new GradientStop { Color = ParseColor(start), Offset = 0 },
            new GradientStop { Color = ParseColor(end), Offset = 1 }
        }
    };

    private static Windows.UI.Color ParseColor(string value)
    {
        var hex = value.TrimStart('#');
        if (hex.Length == 6
            && byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var red)
            && byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var green)
            && byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var blue))
        {
            return ColorHelper.FromArgb(255, red, green, blue);
        }

        return ColorHelper.FromArgb(255, 47, 53, 78);
    }

    private static ImageSource? ResolveArtwork(GameDefinition definition, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return GetCachedImage(explicitPath);
        }

        var dataRoot = LauncherDataPaths.ResolveDataDirectory();

        var safeId = string.Concat(definition.Id.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        foreach (var extension in new[] { ".jpg", ".png", ".webp" })
        {
            var path = Path.Combine(dataRoot, "artwork", safeId + extension);
            if (File.Exists(path))
            {
                return GetCachedImage(path);
            }
        }

        return ResolvePackagedImage(definition.HeroAssetPath);
    }

    private static ImageSource? ResolvePackagedImage(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(fullPath) ? GetCachedImage(fullPath) : null;
    }

    private static BitmapImage GetCachedImage(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (ImageCache.TryGetValue(fullPath, out var cached)
            && cached.LastWriteUtc == file.LastWriteTimeUtc
            && cached.Length == file.Length)
        {
            return cached.Image;
        }

        var image = new BitmapImage(new Uri(fullPath));
        ImageCache[fullPath] = new CachedImage(file.LastWriteTimeUtc, file.Length, image);
        return image;
    }
}
