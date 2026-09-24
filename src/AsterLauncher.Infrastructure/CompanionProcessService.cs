using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using AsterLauncher.Core;
using Microsoft.Extensions.Logging;

namespace AsterLauncher.Infrastructure;

public sealed class CompanionProcessService(ILogger<CompanionProcessService> logger) : ICompanionProcessService, IDisposable
{
    private readonly ConcurrentDictionary<Guid, OwnedProcess> _ownedProcesses = new();
    private readonly ConcurrentDictionary<Guid, byte> _closedSessions = new();
    private bool _disposed;

    public async Task<ProcessLaunchResult> StartAsync(
        LaunchStep step,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(step);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(step.ExecutablePath);
        }
        catch (Exception exception)
        {
            return new ProcessLaunchResult(ProcessLaunchStatus.FileNotFound, $"可执行文件路径无效：{exception.Message}");
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessLaunchResult(ProcessLaunchStatus.Failed, "MVP 只直接启动明确的 .exe 文件，不执行 PowerShell、CMD 或脚本字符串。");
        }

        if (!File.Exists(fullPath))
        {
            return new ProcessLaunchResult(ProcessLaunchStatus.FileNotFound, $"找不到可执行文件：{fullPath}");
        }

        if (step.SkipIfAlreadyRunning && IsExecutableAlreadyRunning(fullPath))
        {
            logger.LogInformation("Skipped {StepName}; an existing process with the same executable path is already running", step.Name);
            return new ProcessLaunchResult(ProcessLaunchStatus.SkippedAlreadyRunning, "检测到用户已经运行了同一路径的程序；已跳过且不会取得或关闭该进程。");
        }

        IReadOnlyList<string> arguments;
        try
        {
            arguments = ArgumentTokenizer.Parse(step.Arguments);
        }
        catch (FormatException exception)
        {
            return new ProcessLaunchResult(ProcessLaunchStatus.Failed, exception.Message);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = ResolveWorkingDirectory(step.WorkingDirectory, fullPath),
            UseShellExecute = step.RunAsAdministrator,
            Verb = step.RunAsAdministrator ? "runas" : string.Empty
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, step.TimeoutSeconds));
        var startTask = Task.Run(() =>
        {
            try
            {
                return Process.Start(startInfo);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 740 && !step.RunAsAdministrator)
            {
                // The executable manifest requires elevation. Only retry when Windows explicitly reports this.
                logger.LogInformation("Windows requires elevation for {ExecutablePath}; requesting UAC consent", fullPath);
                startInfo.UseShellExecute = true;
                startInfo.Verb = "runas";
                return Process.Start(startInfo);
            }
        }, CancellationToken.None);
        var completed = await Task.WhenAny(startTask, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        if (completed != startTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = TrackLateStartAsync(startTask, step, sessionId);
            return new ProcessLaunchResult(ProcessLaunchStatus.TimedOut, $"启动请求在 {timeout.TotalSeconds:0} 秒内没有完成。");
        }

        try
        {
            var process = await startTask.ConfigureAwait(false);
            if (process is null)
            {
                return new ProcessLaunchResult(ProcessLaunchStatus.Failed, "系统没有返回进程句柄。");
            }

            return Track(process, step, sessionId);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223 || exception.HResult == unchecked((int)0x800704C7))
        {
            return new ProcessLaunchResult(ProcessLaunchStatus.UacCancelled, "用户取消了管理员权限请求，步骤未启动。");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == unchecked((int)0xC0000142))
        {
            logger.LogError(exception, "Windows could not initialize the requested process {ExecutablePath}", fullPath);
            return new ProcessLaunchResult(ProcessLaunchStatus.Failed,
                "Windows 在启动或提权阶段返回 0xC0000142，未能创建游戏进程。请尝试从游戏的官方启动器运行；若仍失败，检查游戏安装和系统权限。");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to start {ExecutablePath}", fullPath);
            return new ProcessLaunchResult(ProcessLaunchStatus.Failed, $"启动失败：{exception.Message}");
        }
    }

    public async Task WaitForExitAsync(OwnedProcessToken process, CancellationToken cancellationToken = default)
    {
        if (!_ownedProcesses.TryGetValue(process.Value, out var entry))
        {
            return;
        }

        try
        {
            await entry.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The process already exited before a wait handle could be registered.
        }
        finally
        {
            if (_ownedProcesses.TryRemove(process.Value, out var removed))
            {
                removed.Process.Dispose();
            }
        }
    }

    public async Task StopOwnedProcessesAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        _closedSessions.TryAdd(sessionId, 0);
        var candidates = _ownedProcesses
            .Where(pair => pair.Value.SessionId == sessionId && pair.Value.CloseOnGameExit)
            .ToArray();

        foreach (var (tokenId, owned) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!owned.Process.HasExited)
                {
                    owned.Process.Kill(entireProcessTree: false);
                    await owned.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("Closed owned companion {StepName} ({ProcessId})", owned.StepName, owned.Process.Id);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                logger.LogWarning(exception, "Could not close owned companion {StepName}", owned.StepName);
            }
            finally
            {
                if (_ownedProcesses.TryRemove(tokenId, out var removed))
                {
                    removed.Process.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var (_, owned) in _ownedProcesses)
        {
            owned.Process.Dispose();
        }

        _ownedProcesses.Clear();
    }

    private ProcessLaunchResult Track(Process process, LaunchStep step, Guid sessionId)
    {
        var token = new OwnedProcessToken(Guid.NewGuid(), process.Id, sessionId, step.Name);
        _ownedProcesses[token.Value] = new OwnedProcess(process, sessionId, step.Name, step.CloseOnGameExit);
        logger.LogInformation("Started owned process {StepName} ({ProcessId}) from {ExecutablePath}", step.Name, process.Id, step.ExecutablePath);
        if (step.CloseOnGameExit && _closedSessions.ContainsKey(sessionId))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: false);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                logger.LogWarning(exception, "Could not close late-owned companion {StepName}", step.Name);
            }

            if (_ownedProcesses.TryRemove(token.Value, out var removed))
            {
                removed.Process.Dispose();
            }
        }

        return new ProcessLaunchResult(ProcessLaunchStatus.Started, $"已启动：{step.Name}", token);
    }

    private async Task TrackLateStartAsync(Task<Process?> startTask, LaunchStep step, Guid sessionId)
    {
        try
        {
            var process = await startTask.ConfigureAwait(false);
            if (process is not null)
            {
                Track(process, step, sessionId);
                logger.LogWarning("Process {StepName} started after the configured timeout and remains owned", step.Name);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Late start task failed for {StepName}", step.Name);
        }
    }

    private static string ResolveWorkingDirectory(string? configuredDirectory, string executablePath)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Path.GetFullPath(configuredDirectory);
        }

        return Path.GetDirectoryName(executablePath)!;
    }

    private static bool IsExecutableAlreadyRunning(string executablePath)
    {
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? string.Empty), executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Access denied means path equality is unknown, so it must not be treated as a match.
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private sealed record OwnedProcess(Process Process, Guid SessionId, string StepName, bool CloseOnGameExit);
}
