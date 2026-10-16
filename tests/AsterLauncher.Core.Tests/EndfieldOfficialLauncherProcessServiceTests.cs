using System.ComponentModel;
using System.Diagnostics;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldOfficialLauncherProcessServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "handoff-tests", Guid.NewGuid().ToString("N"));
    private readonly string _launcher;
    public EndfieldOfficialLauncherProcessServiceTests()
    {
        Directory.CreateDirectory(_root);
        _launcher = Path.Combine(_root, "Launcher.exe");
        File.WriteAllText(_launcher, "owned fixture only");
    }
    [Fact]
    public async Task PermissionFailureDoesNotRetryWithElevation()
    {
        var step = new LaunchStep { ExecutablePath = _launcher };
        var calls = 0;
        var service = new EndfieldOfficialLauncherProcessService(new UnusedProcesses(), step.Id, info =>
        {
            calls++;
            Assert.False(info.UseShellExecute);
            Assert.Empty(info.Verb);
            Assert.Empty(info.ArgumentList);
            throw new Win32Exception(740);
        });
        var result = await service.StartAsync(step, Guid.NewGuid());
        Assert.Equal(1, calls);
        Assert.Equal(ProcessLaunchStatus.Failed, result.Status);
        Assert.Contains("不会自动申请提权", result.Message);
    }
    [Fact]
    public async Task HandoffDoesNotAdoptOrOwnOfficialLauncher()
    {
        var step = new LaunchStep { ExecutablePath = _launcher, CloseOnGameExit = false };
        var service = new EndfieldOfficialLauncherProcessService(new UnusedProcesses(), step.Id,
            info => { Assert.Equal(Path.GetDirectoryName(_launcher), info.WorkingDirectory); return Process.GetCurrentProcess(); });
        var result = await service.StartAsync(step, Guid.NewGuid());
        Assert.Equal(ProcessLaunchStatus.Started, result.Status);
        Assert.Null(result.Process);
    }
    public void Dispose() => Directory.Delete(_root, true);
    private sealed class UnusedProcesses : ICompanionProcessService
    {
        public Task<ProcessLaunchResult> StartAsync(LaunchStep step, Guid sessionId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("must not fall through");
        public Task WaitForExitAsync(OwnedProcessToken process, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopOwnedProcessesAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
