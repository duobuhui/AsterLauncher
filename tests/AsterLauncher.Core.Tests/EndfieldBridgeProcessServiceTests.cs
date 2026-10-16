using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldBridgeProcessServiceTests
{
    [Fact]
    public async Task ManagedStepNeverRunsThroughOrdinaryProcessLauncher()
    {
        var inner = new FakeProcesses();
        var step = new LaunchStep();
        var calls = 0;
        var service = new EndfieldBridgeProcessService(inner, step.Id, () =>
        {
            calls++;
            return Task.FromResult(new EndfieldBridgeLaunchResult(81, 82, false));
        });
        var result = await service.StartAsync(step, Guid.NewGuid());
        Assert.Equal(ProcessLaunchStatus.Started, result.Status);
        Assert.Null(result.Process); // Lifecycle is observed by the adapter, not companion ownership.
        Assert.Equal(1, calls);
        Assert.Empty(inner.Started);
    }

    [Fact]
    public async Task CompletedBeforeDetectionDoesNotRetryOrFallThrough()
    {
        var inner = new FakeProcesses();
        var step = new LaunchStep();
        var service = new EndfieldBridgeProcessService(inner, step.Id,
            () => Task.FromResult(new EndfieldBridgeLaunchResult(81, 82, true)));
        var result = await service.StartAsync(step, Guid.NewGuid());
        Assert.Equal(ProcessLaunchStatus.Failed, result.Status);
        Assert.Contains("很快退出", result.Message);
        Assert.Empty(inner.Started);
    }

    [Fact]
    public async Task BridgeErrorsDoNotLeakArgumentsOrAutomaticallyOpenLauncher()
    {
        var inner = new FakeProcesses();
        var step = new LaunchStep();
        var service = new EndfieldBridgeProcessService(inner, step.Id,
            () => Task.FromException<EndfieldBridgeLaunchResult>(new IOException("private-auth-placeholder")));
        var result = await service.StartAsync(step, Guid.NewGuid());
        Assert.Equal(ProcessLaunchStatus.Failed, result.Status);
        Assert.DoesNotContain("private-auth", result.Message);
        Assert.Empty(inner.Started);
    }

    [Fact]
    public async Task CancelWaitingDoesNotCancelKeeperCreationOrTerminateGame()
    {
        var inner = new FakeProcesses();
        var step = new LaunchStep();
        var pending = new TaskCompletionSource<EndfieldBridgeLaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new EndfieldBridgeProcessService(inner, step.Id, () => pending.Task);
        using var cancellation = new CancellationTokenSource();
        var waiting = service.StartAsync(step, Guid.NewGuid(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(pending.Task.IsCompleted);
        pending.SetResult(new(81, 82, false));
        Assert.Empty(inner.Started);
        Assert.Equal(0, inner.CleanupCount);
    }

    [Fact]
    public async Task OrdinaryCompanionsAndCleanupStillUseExistingService()
    {
        var inner = new FakeProcesses();
        var step = new LaunchStep();
        var service = new EndfieldBridgeProcessService(inner, Guid.NewGuid(),
            () => throw new InvalidOperationException("must not run"));
        var session = Guid.NewGuid();
        var result = await service.StartAsync(step, session);
        await service.WaitForExitAsync(result.Process!);
        await service.StopOwnedProcessesAsync(session);
        Assert.Equal(new[] { step.Id }, inner.Started);
        Assert.Equal(1, inner.WaitCount);
        Assert.Equal(1, inner.CleanupCount);
    }

    [Fact]
    public async Task CustomTakeoverRemainsCompatibleWithoutManagedLauncherConfiguration()
    {
        var inner = new FakeProcesses();
        var profile = new LaunchProfile { GameId = BuiltInGameIds.Endfield, Steps = [new()
        {
            TakesOverGameLaunch = true, Phase = LaunchPhase.Game, ExecutablePath = @"E:\existing-tool.exe"
        }] };
        var game = new GameDefinition(BuiltInGameIds.Endfield, "Endfield", "HG", "EF", "E", "#000000", "#FFFFFF",
            ["Endfield.exe"], [], new HashSet<GameFeature> { GameFeature.Launch });
        var coordinator = new EndfieldLaunchCoordinator(inner, new Decisions(), NullLogger<GameLaunchOrchestrator>.Instance,
            new EndfieldLauncherDiscovery(), null!, @"E:\unused", @"E:\unused\StartEndfieldBridge.ps1");
        var result = await coordinator.LaunchAsync(new(game, @"E:\Endfield.exe", profile, new Detector()),
            new() { Channel = EndfieldChannel.Bilibili });
        Assert.Equal(LaunchSessionStatus.Completed, result.Status);
        Assert.Equal(new[] { profile.Steps[0].Id }, inner.Started);
        Assert.Single(profile.Steps);
    }

    [Fact]
    public async Task GameCreatedWithIsolationWarningIsReportedAndNeverLaunchedAgain()
    {
        var inner = new FakeProcesses();
        var step = new LaunchStep();
        var service = new EndfieldBridgeProcessService(inner, step.Id,
            () => Task.FromResult(new EndfieldBridgeLaunchResult(81, 82, false, "game_job_isolation_unavailable")));
        var result = await service.StartAsync(step, Guid.NewGuid());
        Assert.Equal(ProcessLaunchStatus.Failed, result.Status);
        Assert.Contains("进程已创建", result.Message);
        Assert.Contains("勿重复启动", result.Message);
        Assert.Empty(inner.Started);
    }
    private sealed class FakeProcesses : ICompanionProcessService
    {
        public List<Guid> Started { get; } = [];
        public int CleanupCount { get; private set; }
        public int WaitCount { get; private set; }
        public Task<ProcessLaunchResult> StartAsync(LaunchStep step, Guid sessionId, CancellationToken cancellationToken = default)
        {
            Started.Add(step.Id);
            return Task.FromResult(new ProcessLaunchResult(ProcessLaunchStatus.Started, "fixture", new(Guid.NewGuid(), 1, sessionId, "fixture")));
        }
        public Task WaitForExitAsync(OwnedProcessToken process, CancellationToken cancellationToken = default)
        { WaitCount++; return Task.CompletedTask; }
        public Task StopOwnedProcessesAsync(Guid sessionId, CancellationToken cancellationToken = default)
        { CleanupCount++; return Task.CompletedTask; }
    }
    private sealed class Decisions : ILaunchDecisionService
    {
        public Task<bool> ShouldContinueAsync(LaunchStep failedStep, ProcessLaunchResult failure, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
    private sealed class Detector : IGameProcessDetector
    {
        public Task<bool> IsRunningAsync(GameDefinition game, string? expectedExecutablePath, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> WaitForStartAsync(GameDefinition game, string? expectedExecutablePath, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task WaitForExitAsync(GameDefinition game, string? expectedExecutablePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
