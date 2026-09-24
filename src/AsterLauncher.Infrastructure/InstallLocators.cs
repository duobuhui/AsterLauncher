using AsterLauncher.Core;
using Microsoft.Win32;
using System.Diagnostics;

namespace AsterLauncher.Infrastructure;

public sealed class ManualOnlyInstallLocator(string source, string reason) : IGameInstallLocator
{
    public Task<IReadOnlyList<InstallScanResult>> ScanAsync(
        IProgress<ScanObservation>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScanObservation(source, $"正在检查：{source}"));
        var result = new InstallScanResult(ScanResultKind.NotFound, source, reason);
        progress?.Report(new ScanObservation(source, result.Message, result.Kind));
        return Task.FromResult<IReadOnlyList<InstallScanResult>>([result]);
    }
}

public sealed class ExplicitPathInstallLocator(string? executablePath) : IGameInstallLocator, IUpdatableInstallLocator
{
    private string? _executablePath = executablePath;

    public void UpdateSavedPath(string? executablePath) => _executablePath = executablePath;

    public Task<IReadOnlyList<InstallScanResult>> ScanAsync(
        IProgress<ScanObservation>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string source = "已保存的手动路径";
        progress?.Report(new ScanObservation(source, $"正在检查：{source}"));
        var result = string.IsNullOrWhiteSpace(_executablePath)
            ? new InstallScanResult(ScanResultKind.NotFound, source, "尚未保存游戏路径；请手动选择游戏 EXE。")
            : ManualExecutableValidator.Validate(_executablePath, source);
        progress?.Report(new ScanObservation(source, result.Message, result.Kind));
        return Task.FromResult<IReadOnlyList<InstallScanResult>>([result]);
    }
}

public sealed record RunningExecutableEvidence(
    IReadOnlyList<string> ExecutablePaths,
    int AccessDeniedCount,
    IReadOnlyList<string> Errors);

public sealed record LauncherInstallCandidate(string Source, string InstallRoot);

public interface IEndfieldInstallEvidenceSource
{
    RunningExecutableEvidence GetRunningExecutables();

    IReadOnlyList<LauncherInstallCandidate> GetLauncherInstallCandidates();
}

/// <summary>
/// Locates Endfield only through evidence verified against a real installation:
/// Endfield.exe itself and the GRYPHLINK/鹰角启动器 install root followed by
/// games\Endfield Game\Endfield.exe. It never performs a disk-wide scan.
/// </summary>
public sealed class EndfieldInstallLocator : IGameInstallLocator, IUpdatableInstallLocator
{
    public const string VerifiedExecutableName = "Endfield.exe";
    public static readonly string VerifiedRelativePath = Path.Combine("games", "Endfield Game", VerifiedExecutableName);

    private readonly IEndfieldInstallEvidenceSource _evidenceSource;
    private string? _savedExecutablePath;

    public EndfieldInstallLocator(
        string? savedExecutablePath = null,
        IEndfieldInstallEvidenceSource? evidenceSource = null)
    {
        _savedExecutablePath = savedExecutablePath;
        _evidenceSource = evidenceSource ?? new WindowsEndfieldInstallEvidenceSource();
    }

    public void UpdateSavedPath(string? executablePath) => _savedExecutablePath = executablePath;

    public Task<IReadOnlyList<InstallScanResult>> ScanAsync(
        IProgress<ScanObservation>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<InstallScanResult>();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckExecutablePath("已保存的游戏路径", _savedExecutablePath, results, progress);
            if (results.Any(result => result.Kind == ScanResultKind.Found))
            {
                return Task.FromResult<IReadOnlyList<InstallScanResult>>(results);
            }

            cancellationToken.ThrowIfCancellationRequested();
            const string processSource = "正在运行的 Endfield 进程";
            progress?.Report(new ScanObservation(processSource, $"正在检查：{processSource}"));
            var running = _evidenceSource.GetRunningExecutables();
            foreach (var executablePath in running.ExecutablePaths)
            {
                CheckExecutablePath(processSource, executablePath, results, progress);
                if (results.Any(result => result.Kind == ScanResultKind.Found))
                {
                    return Task.FromResult<IReadOnlyList<InstallScanResult>>(results);
                }
            }

            foreach (var error in running.Errors)
            {
                AddResult(results, progress, new InstallScanResult(ScanResultKind.Error, processSource, error));
            }

            if (running.AccessDeniedCount > 0)
            {
                AddResult(results, progress, new InstallScanResult(
                    ScanResultKind.AccessDenied,
                    processSource,
                    $"有 {running.AccessDeniedCount} 个同名进程无法读取可执行文件路径。"));
            }

            if (running.ExecutablePaths.Count == 0 && running.AccessDeniedCount == 0 && running.Errors.Count == 0)
            {
                AddResult(results, progress, new InstallScanResult(
                    ScanResultKind.NotFound,
                    processSource,
                    "未发现正在运行的 Endfield.exe。"));
            }

            cancellationToken.ThrowIfCancellationRequested();
            const string launcherSource = "系统卸载信息中的官方启动器";
            progress?.Report(new ScanObservation(launcherSource, $"正在检查：{launcherSource}"));
            var candidates = _evidenceSource.GetLauncherInstallCandidates();
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var executablePath = Path.Combine(candidate.InstallRoot, VerifiedRelativePath);
                CheckExecutablePath(candidate.Source, executablePath, results, progress, candidate.InstallRoot);
                if (results.Any(result => result.Kind == ScanResultKind.Found))
                {
                    return Task.FromResult<IReadOnlyList<InstallScanResult>>(results);
                }
            }

            if (candidates.Count == 0)
            {
                AddResult(results, progress, new InstallScanResult(
                    ScanResultKind.NotFound,
                    launcherSource,
                    "未找到名称为“鹰角启动器”或“GRYPHLINK”的标准卸载信息；当前没有更多可信规则。"));
            }
        }
        catch (OperationCanceledException exception)
        {
            AddResult(results, progress, new InstallScanResult(
                ScanResultKind.Cancelled,
                "自动查找",
                "用户取消了自动查找。",
                Exception: exception));
        }
        catch (UnauthorizedAccessException exception)
        {
            AddResult(results, progress, new InstallScanResult(
                ScanResultKind.AccessDenied,
                "自动查找",
                "读取安装证据时权限不足。",
                Exception: exception));
        }
        catch (Exception exception)
        {
            AddResult(results, progress, new InstallScanResult(
                ScanResultKind.Error,
                "自动查找",
                $"扫描发生异常：{exception.Message}",
                Exception: exception));
        }

        return Task.FromResult<IReadOnlyList<InstallScanResult>>(results);
    }

    private static void CheckExecutablePath(
        string source,
        string? executablePath,
        List<InstallScanResult> results,
        IProgress<ScanObservation>? progress,
        string? candidateRoot = null)
    {
        progress?.Report(new ScanObservation(source, $"正在检查：{source}"));
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            AddResult(results, progress, new InstallScanResult(
                ScanResultKind.NotFound,
                source,
                "没有可供检查的路径。"));
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(executablePath);
            if (File.Exists(fullPath))
            {
                if (!string.Equals(Path.GetFileName(fullPath), VerifiedExecutableName, StringComparison.OrdinalIgnoreCase))
                {
                    AddResult(results, progress, new InstallScanResult(
                        ScanResultKind.InvalidPath,
                        source,
                        $"路径存在，但文件名不是已验证的 {VerifiedExecutableName}：{fullPath}"));
                    return;
                }

                AddResult(results, progress, new InstallScanResult(
                    ScanResultKind.Found,
                    source,
                    $"已找到游戏：{fullPath}",
                    new GameInstallation(fullPath, Path.GetDirectoryName(fullPath)!, source)));
                return;
            }

            var exists = Directory.Exists(candidateRoot)
                || Directory.Exists(Path.GetDirectoryName(fullPath));
            AddResult(results, progress, new InstallScanResult(
                exists ? ScanResultKind.InvalidPath : ScanResultKind.NotFound,
                source,
                exists
                    ? $"路径存在，但未找到 {VerifiedExecutableName}：{fullPath}"
                    : $"候选路径不存在：{fullPath}"));
        }
        catch (UnauthorizedAccessException exception)
        {
            AddResult(results, progress, new InstallScanResult(
                ScanResultKind.AccessDenied,
                source,
                $"没有权限检查路径：{executablePath}",
                Exception: exception));
        }
        catch (Exception exception)
        {
            AddResult(results, progress, new InstallScanResult(
                ScanResultKind.Error,
                source,
                $"检查路径时发生异常：{exception.Message}",
                Exception: exception));
        }
    }

    private static void AddResult(
        ICollection<InstallScanResult> results,
        IProgress<ScanObservation>? progress,
        InstallScanResult result)
    {
        results.Add(result);
        progress?.Report(new ScanObservation(result.Source, result.Message, result.Kind));
    }
}

public sealed class WindowsEndfieldInstallEvidenceSource : IEndfieldInstallEvidenceSource
{
    private static readonly HashSet<string> TrustedLauncherNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "鹰角启动器",
        "GRYPHLINK"
    };

    public RunningExecutableEvidence GetRunningExecutables()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var accessDenied = 0;

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(EndfieldInstallLocator.VerifiedExecutableName)))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        paths.Add(path);
                    }
                }
                catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 5)
                {
                    accessDenied++;
                }
                catch (InvalidOperationException exception)
                {
                    errors.Add($"进程 {process.Id} 在读取路径前已退出：{exception.Message}");
                }
                catch (Exception exception)
                {
                    errors.Add($"读取进程 {process.Id} 路径失败：{exception.Message}");
                }
            }
        }

        return new RunningExecutableEvidence(paths.ToArray(), accessDenied, errors);
    }

    public IReadOnlyList<LauncherInstallCandidate> GetLauncherInstallCandidates()
    {
        var candidates = new List<LauncherInstallCandidate>();
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstallKey is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    using var appKey = uninstallKey.OpenSubKey(subKeyName);
                    var displayName = appKey?.GetValue("DisplayName") as string;
                    var installLocation = appKey?.GetValue("InstallLocation") as string;
                    if (displayName is null
                        || installLocation is null
                        || !TrustedLauncherNames.Contains(displayName)
                        || string.IsNullOrWhiteSpace(installLocation))
                    {
                        continue;
                    }

                    var root = Environment.ExpandEnvironmentVariables(installLocation.Trim().Trim('"'));
                    if (seenRoots.Add(root))
                    {
                        candidates.Add(new LauncherInstallCandidate(
                            $"{displayName} 卸载信息 ({hive}/{view})",
                            root));
                    }
                }
            }
        }

        return candidates;
    }
}
