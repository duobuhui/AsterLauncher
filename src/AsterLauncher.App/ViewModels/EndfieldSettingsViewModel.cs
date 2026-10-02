using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
using Microsoft.UI.Xaml;
namespace AsterLauncher.App.ViewModels;

public sealed class EndfieldSettingsViewModel(LauncherViewModel launcher, EndfieldDisplayService service) : ObservableObject
{
    private EndfieldDisplaySnapshot? _snapshot;
    private bool _busy;
    private string _status = "";
    private IReadOnlyList<EndfieldPhoto> _photos = [];
    private string _photoDirectory = "";
    private int _revision;
    public Visibility Visibility => launcher.CurrentGame?.Id == BuiltInGameIds.Endfield ? Visibility.Visible : Visibility.Collapsed;
    public double Width { get; set; } = 1920;
    public double Height { get; set; } = 1080;
    public bool Fullscreen { get; set; } = true;
    public double PresetSlot { get; set; } = 1;
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); OnPropertyChanged(nameof(CanEdit)); } }
    public bool CanEdit => !IsBusy && _snapshot?.Width is not null && _snapshot.Height is not null && _snapshot.Fullscreen is not null;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public IReadOnlyList<EndfieldPhoto> Photos { get => _photos; private set => SetProperty(ref _photos, value); }
    public string PhotoDirectory { get => _photoDirectory; private set => SetProperty(ref _photoDirectory, value); }
    public string PhotoSummary => Photos.Count == 0 ? "还没有照片。使用游戏内拍照功能保存后，点击刷新；也可选择自己的照片文件夹。" : $"{Photos.Count} 张 · 最新在前（最多显示 500 张）";
    public async Task RefreshAsync()
    {
        var revision = ++_revision;
        OnPropertyChanged(nameof(Visibility));
        if (launcher.CurrentGame?.Id != BuiltInGameIds.Endfield) return;
        var install = launcher.SelectedEndfieldInstallation;
        var directory = install?.ScreenshotDirectory ?? EndfieldPhotoService.DefaultDirectory;
        IsBusy = true;
        try
        {
            var result = await Task.Run(() => (Snapshot: service.Read(), Photos: EndfieldPhotoService.Scan(directory)));
            if (revision != _revision || launcher.CurrentGame?.Id != BuiltInGameIds.Endfield || launcher.SelectedEndfieldInstallation != install) return;
            _snapshot = result.Snapshot;
            Width = _snapshot.Width ?? 1920; Height = _snapshot.Height ?? 1080; Fullscreen = _snapshot.Fullscreen ?? true;
            Photos = result.Photos; PhotoDirectory = directory;
            Status = _snapshot.Width is null ? "未找到游戏的画面设置。请先启动一次游戏，再重新读取。" : $"当前保存：{(int)Width}×{(int)Height} · {(Fullscreen ? "全屏窗口" : "窗口")}";
            foreach (var name in new[] { nameof(Width), nameof(Height), nameof(Fullscreen), nameof(CanEdit), nameof(PhotoSummary) }) OnPropertyChanged(name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { if (revision == _revision) Status = e.Message; }
        finally { if (revision == _revision) IsBusy = false; }
    }
    public async Task ExecuteAsync(string action)
    {
        if (IsBusy || _snapshot is null) return;
        var snapshot = _snapshot;
        var widthValue = Width; var heightValue = Height; var fullscreen = Fullscreen; var slotValue = PresetSlot;
        if (launcher.IsLaunching || launcher.SelectedEndfieldInstallation?.MaintenanceInProgress == true || launcher.OtherEndfieldInstallation?.MaintenanceInProgress == true)
        { Status = "请等待游戏启动或维护结束，再修改画面设置。"; return; }
        IsBusy = true;
        try
        {
            if (!double.IsFinite(widthValue) || !double.IsFinite(heightValue) || !double.IsFinite(slotValue)
                || widthValue < 640 || widthValue > 16384 || heightValue < 480 || heightValue > 16384
                || slotValue < 1 || slotValue > 3)
                throw new ArgumentException("请填写有效的分辨率和方案编号。");
            var width = (int)widthValue; var height = (int)heightValue; var slot = (int)slotValue;
            await Task.Run(async () =>
            {
                switch (action)
                {
                    case "resolution": await service.ApplyResolutionAsync(snapshot, width, height, fullscreen); break;
                    case "save-quality": service.SaveQuality(slot, snapshot); break;
                    case "apply-quality": await service.ApplyQualityAsync(slot, snapshot); break;
                    case "restore": await service.RestoreAsync(snapshot); break;
                }
            });
            await RefreshAsync();
            Status = action == "save-quality" ? $"当前画质已保存到方案 {(int)slotValue}。" : "已保存。下次启动游戏时使用这些设置；游戏内再次调整会覆盖它们。";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { Status = e.Message; }
        finally { IsBusy = false; }
    }
    public async Task SelectPhotoDirectoryAsync(string directory)
    {
        var install = launcher.SelectedEndfieldInstallation;
        if (install is null) { Status = "请先选择终末地服务器。"; return; }
        install.ScreenshotDirectory = directory;
        await launcher.PersistSelectedProfileAsync();
        await RefreshAsync();
    }
}