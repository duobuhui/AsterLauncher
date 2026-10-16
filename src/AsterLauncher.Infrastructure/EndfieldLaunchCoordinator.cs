using System.ComponentModel;
using System.Diagnostics;
using AsterLauncher.Core;
using Microsoft.Extensions.Logging;

namespace AsterLauncher.Infrastructure;

/// <summary>Adapts only Endfield's managed game step; the shared orchestrator remains authoritative.</summary>
public sealed class EndfieldLaunchCoordinator(
    ICompanionProcessService processes,
    ILaunchDecisionService decisions,
    ILogger<GameLaunchOrchestrator> logger,
    EndfieldLauncherDiscovery discovery,
    EndfieldLaunchBridgeService bridge,
    string dataRoot,
    string helperScript,
    IEndfieldBridgeElevationConsent? elevationConsent = null)
{
    private readonly object _pendingGate = new();
    private readonly HashSet<Task> _pendingStarts = [];
    private bool _stopping;

    public bool HasPendingStarts
    {
        get { lock (_pendingGate) return _pendingStarts.Count > 0; }
    }
    public async Task<LaunchSessionResult> LaunchAsync(GameLaunchRequest request, EndfieldInstallation installation,
        IProgress<LaunchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (request.Game.Id != BuiltInGameIds.Endfield
            || installation.Channel is not (EndfieldChannel.Official or EndfieldChannel.Bilibili))
            throw new InvalidOperationException("请先确认终末地安装所属服务器。");

        lock (_pendingGate)
        {
            if (_stopping) throw new InvalidOperationException("启动器正在退出，已停止新的启动请求。");
            if (_pendingStarts.Count > 0)
                throw new InvalidOperationException("上一次实验性启动仍在确认中，请稍候并检查游戏状态，避免重复启动。");
        }
        var custom = request.Profile.Steps.Any(step => step.IsEnabled && step.TakesOverGameLaunch);
        string launcher = installation.LauncherExecutablePath ?? string.Empty;
        if (!custom && (!installation.LauncherAssociationConfirmed
            || !EndfieldLauncherTrust.TryNormalizeExecutable(launcher, out launcher) || !File.Exists(launcher)))
            throw new InvalidOperationException("请在启动方式中选择并确认当前服务器的官方启动器。");

        string? bridgeParent = null;
        if (!custom && installation.LaunchMode == EndfieldLaunchMode.ExperimentalBridge)
        {
            bridgeParent = (await discovery.ResolveBridgeParentAsync(launcher, cancellationToken))?.ExecutablePath;
            if (bridgeParent is null)
                throw new InvalidOperationException("没有找到签名可信的官方 Games.exe，请切回官方启动器接管。");
        }
        var plan = EndfieldLaunchPlan.Create(request.Profile, installation, request.ExecutablePath, launcher);
        var contextProcesses = plan.ManagedStepId is Guid managedId && bridgeParent is not null
            ? new EndfieldBridgeProcessService(processes, managedId, () => EndfieldBridgeElevationRetry.RunAsync(
                () => TrackStartAsync(() => bridge.LaunchAsync(request.ExecutablePath, bridgeParent,
                    plan.Profile.GameWorkingDirectory, GameArguments(), dataRoot, helperScript, CancellationToken.None)),
                () => TrackStartAsync(() => bridge.LaunchElevatedAsync(request.ExecutablePath, bridgeParent,
                    plan.Profile.GameWorkingDirectory, GameArguments(), dataRoot, helperScript, CancellationToken.None)),
                elevationConsent, cancellationToken))
            : plan.ManagedStepId is Guid officialStepId
                ? new EndfieldOfficialLauncherProcessService(processes, officialStepId)
                : processes;
        string[] GameArguments() => request.Game.DefaultArguments.Concat(ArgumentTokenizer.Parse(request.Profile.GameArguments)).ToArray();
        var orchestrator = new GameLaunchOrchestrator(contextProcesses, decisions, logger);
        var contextProgress = new InlineProgress(item =>
        {
            if (plan.ManagedStepId is not null && item.Message == "正在等待适配器识别游戏进程…")
                item = item with { Message = bridgeParent is null
                    ? "已打开官方启动器，请点击开始游戏；正在等待游戏进程…"
                    : "实验性启动桥已执行，正在等待游戏进程…" };
            progress?.Report(item);
        });
        return await orchestrator.LaunchAsync(request with { Profile = plan.Profile }, contextProgress, cancellationToken);
    }

    // On GUI exit, finish only initial creation handshakes before the play-time observer's final scan.
    public async Task WaitForPendingStartsAsync()
    {
        Task[] pending;
        lock (_pendingGate)
        {
            _stopping = true;
            pending = _pendingStarts.ToArray();
        }
        try { await Task.WhenAll(pending); }
        catch (Exception) { /* Fixed startup errors are already returned to the launch session. */ }
    }

    private async Task<EndfieldBridgeLaunchResult> TrackStartAsync(Func<Task<EndfieldBridgeLaunchResult>> start)
    {
        var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate)
        {
            if (_stopping) throw new EndfieldBridgeLaunchException("launcher_stopping", "启动器正在退出，未创建新的辅助进程。");
            _pendingStarts.Add(marker.Task);
        }
        try { return await start(); }
        finally
        {
            marker.TrySetResult();
            lock (_pendingGate) _pendingStarts.Remove(marker.Task);
        }
    }

    private sealed class InlineProgress(Action<LaunchProgress> report) : IProgress<LaunchProgress>
    {
        public void Report(LaunchProgress value) => report(value);
    }
}

/// <summary>The managed bridge step cannot fall through to ordinary EXE launch, including on failure.</summary>
internal sealed class EndfieldBridgeProcessService(ICompanionProcessService inner, Guid managedStepId,
    Func<Task<EndfieldBridgeLaunchResult>> start) : ICompanionProcessService
{
    public async Task<ProcessLaunchResult> StartAsync(LaunchStep step, Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (step.Id != managedStepId) return await inner.StartAsync(step, sessionId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Cancellation ends orchestration only; the bounded handshake and owned keeper finish independently.
            var result = await start().WaitAsync(cancellationToken);
            if (result.ErrorCode is not null)
                return new(ProcessLaunchStatus.Failed,
                    "游戏进程已创建，但系统隔离检查异常。请先检查游戏状态，勿重复启动或强制结束辅助进程；游戏退出后可切回官方接管。（错误：" + result.ErrorCode + "）");
            return result.HasExited
                ? new(ProcessLaunchStatus.Failed, "启动桥已创建游戏，但游戏很快退出。请改用官方启动器接管查看提示。")
                : new(ProcessLaunchStatus.Started, "实验性启动桥已创建游戏进程。");
        }
        catch (OperationCanceledException) { throw; }
        catch (EndfieldBridgeLaunchException exception)
        {
            if (exception.Code is "elevation_declined" or "elevation_cancelled")
                return new(ProcessLaunchStatus.UacCancelled, "已取消本次管理员授权，未重试启动。");
            var reason = exception.NativeError switch
            {
                740 => "官方组件需要管理员权限。请确认本次授权，或使用官方启动器接管。",
                5 => "Windows 拒绝访问，请检查权限或系统拦截提示。",
                2 or 3 => "启动组件或目录不存在，请重新选择官方启动器。",
                32 => "启动文件正被其他程序占用，请稍后重试。",
                193 or 216 => "启动组件的格式或系统架构不兼容。",
                _ => "请先检查游戏状态，可切回官方接管；不会自动重复启动。"
            };
            var detail = exception.Code;
            if (exception.NativeStage is not null) detail += " / " + exception.NativeStage;
            if (exception.NativeError is int native) detail += " / Win32 " + native;
            return new(ProcessLaunchStatus.Failed, "实验性启动桥未能确认启动。" + reason + "（错误：" + detail + "）");
        }
        catch (Exception)
        {
            // Do not expose command lines, arbitrary helper errors or authorization arguments to logs.
            return new(ProcessLaunchStatus.Failed, "实验性启动桥未能确认启动。请检查游戏是否已打开；可改用官方启动器接管，不会自动重复启动。");
        }
    }
    public Task WaitForExitAsync(OwnedProcessToken process, CancellationToken cancellationToken = default)
        => inner.WaitForExitAsync(process, cancellationToken);
    public Task StopOwnedProcessesAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => inner.StopOwnedProcessesAsync(sessionId, cancellationToken);
}

/// <summary>Ordinary handoff opens only the explicitly associated EXE; it never retries with elevation.</summary>
internal sealed class EndfieldOfficialLauncherProcessService(ICompanionProcessService inner, Guid managedStepId,
    Func<ProcessStartInfo, Process?>? start = null) : ICompanionProcessService
{
    public Task<ProcessLaunchResult> StartAsync(LaunchStep step, Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (step.Id != managedStepId) return inner.StartAsync(step, sessionId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!EndfieldLauncherTrust.TryNormalizeExecutable(step.ExecutablePath, out var path) || !File.Exists(path))
            return Task.FromResult(new ProcessLaunchResult(ProcessLaunchStatus.FileNotFound, "官方启动器不存在，请重新设置启动方式。"));
        try
        {
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path), CreateNoWindow = false
            };
            using var process = (start ?? Process.Start)(info);
            return Task.FromResult(process is null
                ? new ProcessLaunchResult(ProcessLaunchStatus.Failed, "没有收到官方启动器进程，请手动打开后重试。")
                : new ProcessLaunchResult(ProcessLaunchStatus.Started, "已打开官方启动器，请点击开始游戏。"));
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 740)
        {
            return Task.FromResult(new ProcessLaunchResult(ProcessLaunchStatus.Failed,
                "此官方启动器要求管理员权限，请主动打开官方启动器后开始游戏。Aster 不会自动申请提权。"));
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Task.FromResult(new ProcessLaunchResult(ProcessLaunchStatus.Failed, "官方启动器未能打开，请检查所选 EXE 或主动打开它。"));
        }
    }
    public Task WaitForExitAsync(OwnedProcessToken process, CancellationToken cancellationToken = default)
        => inner.WaitForExitAsync(process, cancellationToken);
    public Task StopOwnedProcessesAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => inner.StopOwnedProcessesAsync(sessionId, cancellationToken);
}
