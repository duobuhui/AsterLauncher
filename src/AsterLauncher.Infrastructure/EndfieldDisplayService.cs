using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AsterLauncher.Infrastructure;

public interface IEndfieldDisplayStore
{
    Dictionary<string, int> Read();
    void Write(string name, int value);
}

public sealed class EndfieldRegistryDisplayStore : IEndfieldDisplayStore
{
    public const string KeyPath = @"Software\Hypergryph\Endfield";
    public Dictionary<string, int> Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        if (key is null) return values;
        foreach (var name in key.GetValueNames())
            if (EndfieldDisplayService.IsDisplayValue(name) && key.GetValueKind(name) == RegistryValueKind.DWord
                && key.GetValue(name) is int value) values[name] = value;
        return values;
    }
    public void Write(string name, int value)
    {
        if (!EndfieldDisplayService.IsDisplayValue(name)) throw new InvalidDataException("Unsupported display value.");
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("请先运行一次游戏，创建画面设置。");
        if (key.GetValueKind(name) != RegistryValueKind.DWord) throw new InvalidDataException("画面设置格式已变化，请重新读取。");
        key.SetValue(name, value, RegistryValueKind.DWord);
        key.Flush();
    }
}

public sealed record EndfieldDisplaySnapshot(Dictionary<string, int> Values)
{
    public int? Width => Find("video_resolution_width", "Screenmanager Resolution Width");
    public int? Height => Find("video_resolution_height", "Screenmanager Resolution Height");
    public bool? Fullscreen => Find("video_full_screen") is int value ? value != 0 : null;
    private int? Find(params string[] names) => names.Select(name => Values.FirstOrDefault(v => EndfieldDisplayService.BaseName(v.Key) == name))
        .Where(v => v.Key is not null).Select(v => (int?)v.Value).FirstOrDefault();
}

// Only existing DWORD display values are touched. Account, platform and SDK values never enter backups.
public sealed class EndfieldDisplayService(IEndfieldDisplayStore store, string dataRoot, Func<bool>? isRunning = null)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<string> ResolutionNames = new(StringComparer.Ordinal)
    {
        "video_resolution_width", "video_resolution_height", "video_full_screen",
        "Screenmanager Resolution Width", "Screenmanager Resolution Height",
        "Screenmanager Resolution Width Default", "Screenmanager Resolution Height Default",
        "Screenmanager Resolution Window Width", "Screenmanager Resolution Window Height",
        "Screenmanager Fullscreen mode", "Screenmanager Fullscreen mode Default",
        "Screenmanager Resolution Use Native", "Screenmanager Resolution Use Native Default"
    };
    private static readonly HashSet<string> QualityNames = new(StringComparer.Ordinal)
    {
        "video_frame_rate_8", "video_quality_vsync_v2_2", "video_quality_upscaler_2",
        "video_quality_sharpness_1", "video_quality_anisoLevel_1", "video_quality_reflex_1",
        "video_quality_dlss_mode_1", "video_custom_quality", "video_quality_main",
        "video_texture_quality_1", "video_quality_contactshadow_1", "video_quality_effect_1",
        "video_quality_shadowmap_1", "video_quality_volumetricfog_1", "video_quality_ao_1",
        "video_quality_scene_detail_1", "video_quality_environment_renderfeature_1",
        "video_quality_grass_sparsity_1", "video_quality_screenspacereflection_1",
        "video_quality_framegen_1", "video_quality_dlssg_mode_1"
    };
    public static string BaseName(string name) => Regex.Replace(name, @"_h\d+$", "", RegexOptions.CultureInvariant);
    public static bool IsDisplayValue(string name) => Regex.IsMatch(name, @"_h\d+$", RegexOptions.CultureInvariant)
        && (ResolutionNames.Contains(BaseName(name)) || QualityNames.Contains(BaseName(name)));
    public EndfieldDisplaySnapshot Read() => new(store.Read().Where(v => IsDisplayValue(v.Key)).ToDictionary());
    private string BackupPath => Path.Combine(dataRoot, "game-settings", "endfield-display-backup.json");
    private string PresetPath(int slot) => slot is >= 1 and <= 3
        ? Path.Combine(dataRoot, "game-settings", $"endfield-quality-{slot}.json") : throw new ArgumentOutOfRangeException(nameof(slot));
    public bool HasBackup => File.Exists(BackupPath);
    public bool HasPreset(int slot) => File.Exists(PresetPath(slot));
    public void SaveQuality(int slot, EndfieldDisplaySnapshot snapshot)
    {
        EnsureStopped();
        var values = snapshot.Values.Where(v => QualityNames.Contains(BaseName(v.Key))).ToDictionary();
        if (values.Count == 0) throw new InvalidOperationException("未读取到可保存的画质设置，请先在游戏中设置画质。");
        var current = Read().Values;
        if (values.Any(v => !current.TryGetValue(v.Key, out var actual) || actual != v.Value)) throw new InvalidOperationException("游戏设置已变化，请重新读取后保存方案。");
        Save(PresetPath(slot), values);
    }
    public async Task ApplyQualityAsync(int slot, EndfieldDisplaySnapshot snapshot)
    {
        var values = Load(PresetPath(slot));
        if (values.Count == 0 || values.Keys.Any(k => !QualityNames.Contains(BaseName(k))))
            throw new InvalidDataException("画质方案格式无效。");
        await CommitAsync(snapshot, values).ConfigureAwait(false);
    }
    public Task RestoreAsync(EndfieldDisplaySnapshot snapshot) => CommitAsync(snapshot, Load(BackupPath));
    public Task ApplyResolutionAsync(EndfieldDisplaySnapshot snapshot, int width, int height, bool fullscreen)
    {
        if (width is < 640 or > 16384 || height is < 480 or > 16384) throw new ArgumentOutOfRangeException(nameof(width), "分辨率应在 640×480 至 16384×16384 范围内。");
        if (snapshot.Width is null || snapshot.Height is null || snapshot.Fullscreen is null)
            throw new InvalidOperationException("尚未读取到完整画面设置，请先运行一次游戏。");
        var values = snapshot.Values.Where(v => ResolutionNames.Contains(BaseName(v.Key))).ToDictionary(v => v.Key, v => BaseName(v.Key) switch
        {
            "video_full_screen" => fullscreen ? 1 : 0,
            "Screenmanager Fullscreen mode" or "Screenmanager Fullscreen mode Default" => fullscreen ? 1 : 3,
            "Screenmanager Resolution Use Native" or "Screenmanager Resolution Use Native Default" => 0,
            var name when name.Contains("Width", StringComparison.Ordinal) || name == "video_resolution_width" => width,
            _ => height
        });
        return CommitAsync(snapshot, values);
    }
    private async Task CommitAsync(EndfieldDisplaySnapshot snapshot, Dictionary<string, int> changes)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureStopped();
            var current = Read().Values;
            if (changes.Count == 0 || changes.Keys.Any(k => !IsDisplayValue(k) || !snapshot.Values.ContainsKey(k)
                || !current.TryGetValue(k, out var actual) || actual != snapshot.Values[k]))
                throw new InvalidOperationException("游戏设置已变化，请重新读取后保存。");
            var original = changes.ToDictionary(v => v.Key, v => current[v.Key]);
            Save(BackupPath, original);
            EnsureStopped();
            var written = new List<string>();
            try
            {
                foreach (var (name, value) in changes) { store.Write(name, value); written.Add(name); }
                var result = Read().Values;
                if (changes.Any(v => !result.TryGetValue(v.Key, out var actual) || actual != v.Value))
                    throw new IOException("画面设置保存后核对失败。");
            }
            catch
            {
                foreach (var name in written.AsEnumerable().Reverse()) store.Write(name, original[name]);
                throw;
            }
        }
        finally { Gate.Release(); }
    }
    private void EnsureStopped()
    {
        if (isRunning?.Invoke() ?? IsGameRunning()) throw new InvalidOperationException("请先关闭两服终末地，再修改画面设置。");
    }
    public static bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName("Endfield");
        try { return processes.Length != 0; } finally { foreach (var process in processes) process.Dispose(); }
    }
    private static Dictionary<string, int> Load(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("还没有保存此方案或修改备份。");
        if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("画面设置文件过大。");
        return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path)) ?? throw new InvalidDataException("画面设置文件无效。");
    }
    private static void Save(string path, Dictionary<string, int> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(values)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed record EndfieldPhoto(string Path, string Name, DateTime LastModified)
{
    public string DateText => LastModified.ToString("yyyy/MM/dd HH:mm");
}
public static class EndfieldPhotoService
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ENDFIELD");
    public static IReadOnlyList<EndfieldPhoto> Scan(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("请直接选择照片的实际文件夹。");
        return Directory.EnumerateFiles(directory).Where(path => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg")
            .Select(path => new FileInfo(path)).Where(file => file.Length is > 0 and <= 64 * 1024 * 1024 && (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(file => file.LastWriteTimeUtc).Take(500)
            .Select(file => new EndfieldPhoto(file.FullName, file.Name, file.LastWriteTime)).ToArray();
    }
}