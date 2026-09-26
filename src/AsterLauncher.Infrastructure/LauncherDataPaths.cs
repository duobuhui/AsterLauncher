using System.Text.Json;

namespace AsterLauncher.Infrastructure;

public static class LauncherDataPaths
{
    private const string LocationFileName = "asterlauncher.data-location.json";
    private const string EnvironmentVariableName = "ASTERLAUNCHER_DATA_HOME";

    public static string ExecutableDirectory => Path.GetDirectoryName(Environment.ProcessPath)
        ?? AppContext.BaseDirectory;

    public static string DefaultDataDirectory => Path.Combine(ExecutableDirectory, "Data");

    public static string SelectionFilePath => Path.Combine(ExecutableDirectory, LocationFileName);

    public static string ResolveDataDirectory() => ResolveDataDirectory(
        ExecutableDirectory, Environment.GetEnvironmentVariable(EnvironmentVariableName));

    public static string ResolveDataDirectory(string executableDirectory, string? environmentOverride)
    {
        var selectionFile = Path.Combine(executableDirectory, LocationFileName);
        if (File.Exists(selectionFile))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(selectionFile));
                var selected = document.RootElement.GetProperty("directory").GetString();
                if (!string.IsNullOrWhiteSpace(selected) && Path.IsPathFullyQualified(selected))
                    return Path.GetFullPath(selected);
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException
                                               or KeyNotFoundException or ArgumentException)
            {
                throw new InvalidDataException("启动器数据位置文件无效，请检查 EXE 旁的 asterlauncher.data-location.json。", exception);
            }

            throw new InvalidDataException("启动器数据位置不是完整路径，请检查 EXE 旁的 asterlauncher.data-location.json。");
        }

        return !string.IsNullOrWhiteSpace(environmentOverride)
            ? Path.GetFullPath(environmentOverride)
            : Path.GetFullPath(Path.Combine(executableDirectory, "Data"));
    }

    public static bool MigrateLegacyBundleDataIfNeeded(string? executableDirectory = null, string? temporaryDirectory = null)
    {
        var appDirectory = executableDirectory ?? ExecutableDirectory;
        if (File.Exists(Path.Combine(appDirectory, LocationFileName))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariableName))) return false;

        var target = Path.Combine(appDirectory, "Data");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) return false;

        var bundleRoot = Path.Combine(temporaryDirectory ?? Path.GetTempPath(), ".net", "AsterLauncher");
        if (!Directory.Exists(bundleRoot)) return false;

        var source = Directory.EnumerateDirectories(bundleRoot)
            .Select(directory => Path.Combine(directory, "Data"))
            .Where(directory => File.Exists(Path.Combine(directory, "launcher.settings.json")))
            .OrderByDescending(directory => File.GetLastWriteTimeUtc(Path.Combine(directory, "launcher.settings.json")))
            .FirstOrDefault();
        if (source is null) return false;

        CopyDirectory(source, target, skipUpdates: true);
        return true;
    }

    public static async Task<bool> RelocateAsync(
        string selectedDirectory,
        CancellationToken cancellationToken = default,
        string? executableDirectory = null,
        string? sourceDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(selectedDirectory) || !Path.IsPathFullyQualified(selectedDirectory))
            throw new ArgumentException("请选择完整的数据目录路径。", nameof(selectedDirectory));

        var target = Path.GetFullPath(selectedDirectory.Trim());
        var source = sourceDirectory ?? ResolveDataDirectory();
        var appDirectory = executableDirectory ?? ExecutableDirectory;
        if (string.Equals(source.TrimEnd(Path.DirectorySeparatorChar),
                target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return false;
        if (IsInside(target, source) || IsInside(source, target))
            throw new InvalidOperationException("新旧数据目录不能互相包含。");
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException("请选择已经存在的空文件夹。");
        if (Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException("目标文件夹不是空的，请选择空文件夹以避免覆盖现有数据。");

        CopyDirectory(source, target, skipUpdates: false);
        var selectionFile = Path.Combine(appDirectory, LocationFileName);
        var temporaryFile = selectionFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(new { directory = target }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(temporaryFile, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryFile, selectionFile, true);
        }
        finally
        {
            if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
        }

        return true;
    }

    private static bool IsInside(string path, string possibleParent)
    {
        var parent = possibleParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string source, string target, bool skipUpdates)
    {
        if (!Directory.Exists(source)) return;
        var pending = new Queue<(string Source, string Target)>();
        pending.Enqueue((source, target));
        while (pending.TryDequeue(out var pair))
        {
            Directory.CreateDirectory(pair.Target);
            foreach (var entry in Directory.EnumerateFileSystemEntries(pair.Source))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
                var destination = Path.Combine(pair.Target, Path.GetFileName(entry));
                if (Directory.Exists(entry))
                {
                    if (skipUpdates && string.Equals(Path.GetFileName(entry), "updates", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(pair.Source, source, StringComparison.OrdinalIgnoreCase)) continue;
                    pending.Enqueue((entry, destination));
                }
                else
                {
                    File.Copy(entry, destination, overwrite: false);
                }
            }
        }
    }
}
