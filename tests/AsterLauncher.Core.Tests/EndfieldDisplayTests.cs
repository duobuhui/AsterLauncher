using AsterLauncher.Infrastructure;
namespace AsterLauncher.Core.Tests;

public sealed class EndfieldDisplayTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "AsterLauncher.Tests", Guid.NewGuid().ToString("N"));
    private sealed class Store : IEndfieldDisplayStore
    {
        public Dictionary<string, int> Values = new()
        {
            ["video_resolution_width_h583690364"] = 2560,
            ["video_resolution_height_h2517654917"] = 1440,
            ["video_full_screen_h1998742411"] = 1,
            ["Screenmanager Resolution Width_h182942802"] = 2560,
            ["Screenmanager Resolution Height_h2627697771"] = 1440,
            ["Screenmanager Fullscreen mode_h3630240806"] = 1,
            ["video_quality_main_h2648490162"] = 1,
            ["video_quality_shadowmap_1_h128234669"] = 1000,
            ["account_h123"] = 567,
            ["video_unknown_future_h999"] = 888
        };
        public int FailOnWrite;
        private int _writes;
        public Dictionary<string, int> Read() => new(Values);
        public void Write(string name, int value)
        {
            if (++_writes == FailOnWrite) throw new IOException("Fixture write failure");
            Values[name] = value;
        }
    }
    [Fact]
    public async Task ResolutionUpdatesOnlyExistingDisplayValuesAndUndoPreservesQualityAndAccount()
    {
        var store = new Store(); var service = new EndfieldDisplayService(store, _root, () => false);
        var before = service.Read();
        await service.ApplyResolutionAsync(before, 1920, 1080, false);
        var after = service.Read();
        Assert.Equal(1920, after.Width); Assert.Equal(1080, after.Height); Assert.False(after.Fullscreen);
        Assert.Equal(3, after.Values["Screenmanager Fullscreen mode_h3630240806"]);
        Assert.Equal(1, store.Values["video_quality_main_h2648490162"]);
        Assert.Equal(567, store.Values["account_h123"]);
        var backup = await File.ReadAllTextAsync(Path.Combine(_root, "game-settings", "endfield-display-backup.json"));
        Assert.DoesNotContain("account", backup); Assert.DoesNotContain("unknown", backup);
        await service.RestoreAsync(after);
        Assert.Equal(before.Values, service.Read().Values);
    }
    [Fact]
    public async Task PartialWriteFailureRollsBackPreviousValues()
    {
        var store = new Store { FailOnWrite = 2 }; var service = new EndfieldDisplayService(store, _root, () => false);
        var before = service.Read();
        await Assert.ThrowsAsync<IOException>(() => service.ApplyResolutionAsync(before, 1280, 720, true));
        Assert.Equal(before.Values, service.Read().Values);
    }
    [Fact]
    public async Task RunningGameAndStaleReadRefuseToWrite()
    {
        var store = new Store(); var running = new EndfieldDisplayService(store, _root, () => true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => running.ApplyResolutionAsync(running.Read(), 1280, 720, true));
        Assert.False(Directory.Exists(_root));
        var service = new EndfieldDisplayService(store, _root, () => false); var before = service.Read();
        store.Values["video_resolution_width_h583690364"] = 1920;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyResolutionAsync(before, 1280, 720, true));
        Assert.False(Directory.Exists(_root));
    }
    [Fact]
    public async Task QualityPresetRestoresExactGameValuesWithoutChangingResolution()
    {
        var store = new Store(); var service = new EndfieldDisplayService(store, _root, () => false);
        service.SaveQuality(1, service.Read());
        store.Values["video_quality_main_h2648490162"] = 3;
        store.Values["video_resolution_width_h583690364"] = 3840;
        await service.ApplyQualityAsync(1, service.Read());
        Assert.Equal(1, store.Values["video_quality_main_h2648490162"]);
        Assert.Equal(3840, service.Read().Width);
        var json = await File.ReadAllTextAsync(Path.Combine(_root, "game-settings", "endfield-quality-1.json"));
        Assert.DoesNotContain("resolution", json); Assert.DoesNotContain("account", json);
    }
    [Fact]
    public async Task TamperedPresetCannotWriteAccountValues()
    {
        var store = new Store(); var service = new EndfieldDisplayService(store, _root, () => false);
        Directory.CreateDirectory(Path.Combine(_root, "game-settings"));
        await File.WriteAllTextAsync(Path.Combine(_root, "game-settings", "endfield-quality-1.json"), "{\"account_h123\":99}");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ApplyQualityAsync(1, service.Read()));
        Assert.Equal(567, store.Values["account_h123"]);
    }
    [Fact]
    public void PhotoScanIsReadOnlyAndExcludesNonImagesAndEmptyFiles()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "photo.png"), [1,2,3]);
        File.WriteAllBytes(Path.Combine(_root, "empty.jpg"), []);
        File.WriteAllBytes(Path.Combine(_root, "settings.json"), [1]);
        var photos = EndfieldPhotoService.Scan(_root);
        Assert.Equal("photo.png", Assert.Single(photos).Name);
        Assert.Equal(3, Directory.GetFiles(_root).Length);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}