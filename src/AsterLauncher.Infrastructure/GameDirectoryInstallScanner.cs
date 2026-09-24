using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

/// <summary>Searches only the user-selected game directory and never follows directory links.</summary>
public static class GameDirectoryInstallScanner
{
    private const int MaximumDirectories = 20000;

    public static Task<InstallScanResult> ScanAsync(
        IGameAdapter adapter,
        string directory,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(adapter, directory, cancellationToken), cancellationToken);

    public static InstallScanResult Scan(IGameAdapter adapter, string directory, CancellationToken cancellationToken = default)
    {
        const string source = "设定的游戏目录";
        ArgumentNullException.ThrowIfNull(adapter);
        if (adapter.Definition.ExecutableNames.Count == 0)
        {
            return new InstallScanResult(ScanResultKind.NotFound, source, "此游戏没有可信的 EXE 文件名，请手动选择。");
        }

        try
        {
            var root = Path.GetFullPath(directory);
            if (!Directory.Exists(root))
            {
                return new InstallScanResult(ScanResultKind.InvalidPath, source, $"游戏目录不存在：{root}");
            }

            var pending = new Queue<string>();
            pending.Enqueue(root);
            var matches = new List<InstallScanResult>();
            var checkedDirectories = 0;
            var inaccessibleDirectories = 0;
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++checkedDirectories > MaximumDirectories)
                {
                    return new InstallScanResult(ScanResultKind.Error, source, "目录数量超过 20000，已停止搜索；请缩小范围或手动选择 EXE。");
                }

                var current = pending.Dequeue();
                foreach (var name in adapter.Definition.ExecutableNames)
                {
                    var candidate = Path.Combine(current, name);
                    if (!File.Exists(candidate)) continue;
                    var validation = adapter.ValidateManualExecutable(candidate);
                    if (validation.Kind == ScanResultKind.Found && validation.Installation is not null)
                    {
                        matches.Add(validation);
                    }
                }

                if (matches.Count > 1)
                {
                    return new InstallScanResult(ScanResultKind.InvalidPath, source,
                        $"找到多个 {adapter.Definition.DisplayName} 游戏 EXE，请手动选择正确版本。", null);
                }

                try
                {
                    foreach (var child in Directory.EnumerateDirectories(current))
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Enqueue(child);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    inaccessibleDirectories++;
                }
                catch (IOException)
                {
                    inaccessibleDirectories++;
                }
            }

            if (inaccessibleDirectories > 0)
            {
                return new InstallScanResult(ScanResultKind.AccessDenied, source,
                    $"有 {inaccessibleDirectories} 个目录无法检查；请手动选择 EXE 或调整目录权限。");
            }

            if (matches.Count == 1)
            {
                var installation = matches[0].Installation!;
                return new InstallScanResult(ScanResultKind.Found, source,
                    $"已在游戏目录找到 {adapter.Definition.DisplayName}：{installation.ExecutablePath}",
                    installation with { Source = source });
            }

            return new InstallScanResult(ScanResultKind.NotFound, source,
                $"未在游戏目录找到 {string.Join(" / ", adapter.Definition.ExecutableNames)}：{root}");
        }
        catch (OperationCanceledException exception)
        {
            return new InstallScanResult(ScanResultKind.Cancelled, source, "已取消搜索。", Exception: exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new InstallScanResult(ScanResultKind.AccessDenied, source, "读取游戏目录时权限不足。", Exception: exception);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return new InstallScanResult(ScanResultKind.Error, source, $"游戏目录无法搜索：{exception.Message}", Exception: exception);
        }
    }
}
