using AsterLauncher.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsterLauncher.Core.Tests;

public sealed class GameLaunchOrchestratorTests
{
    [Fact]
    public async Task LaunchAsync_ExecutesAllPhasesInOrder()
    {
        var processes = new FakeProcessService();
        var orchestrator = CreateOrchestrator(processes);
        var profile = CreateProfile(
            Step("before", LaunchPhase.BeforeGame),
            Step("after", LaunchPhase.AfterGame),
            Step("exit", LaunchPhase.OnGameExit));

        var result = await orchestrator.LaunchAsync(CreateRequest(profile));

        Assert.Equal(LaunchSessionStatus.Completed, result.Status);
        Assert.Equal(
            ["start:before", "start:启动 Test Game", "start:after", "wait:启动 Test Game", "start:exit", "cleanup"],
            processes.Events);
    }

    [Fact]
    public async Task LaunchAsync_TakeoverSuppressesDefaultGameAndUsesDetector()
    {
        var processes = new FakeProcessService();
        var detector = new FakeDetector();
        var orchestrator = CreateOrchestrator(processes);
        var takeover = Step("MAA takeover", LaunchPhase.Game);
        takeover.TakesOverGameLaunch = true;
        var profile = CreateProfile(takeover);

        var result = await orchestrator.LaunchAsync(CreateRequest(profile, detector));

        Assert.Equal(LaunchSessionStatus.Completed, result.Status);
        Assert.DoesNotContain("start:启动 Test Game", processes.Events);
        Assert.Equal(1, detector.StartWaitCount);
        Assert.Equal(1, detector.ExitWaitCount);
    }

    [Fact]
    public async Task LaunchAsync_AbortPolicyStopsPipelineAndCleansOwnedProcesses()
    {
        var processes = new FakeProcessService { FailureStep = "before" };
        var orchestrator = CreateOrchestrator(processes);
        var profile = CreateProfile(Step("before", LaunchPhase.BeforeGame));

        var result = await orchestrator.LaunchAsync(CreateRequest(profile));

        Assert.Equal(LaunchSessionStatus.Aborted, result.Status);
        Assert.Equal(["start:before", "cleanup"], processes.Events);
    }

    [Fact]
    public async Task LaunchAsync_ContinuePolicyContinuesAfterFailure()
    {
        var processes = new FakeProcessService { FailureStep = "optional" };
        var orchestrator = CreateOrchestrator(processes);
        var optional = Step("optional", LaunchPhase.BeforeGame);
        optional.FailurePolicy = LaunchFailurePolicy.Continue;
        var profile = CreateProfile(optional);

        var result = await orchestrator.LaunchAsync(CreateRequest(profile));

        Assert.Equal(LaunchSessionStatus.Completed, result.Status);
        Assert.Contains("start:启动 Test Game", processes.Events);
    }

    [Fact]
    public async Task LaunchAsync_AskPolicyUsesDecisionService()
    {
        var processes = new FakeProcessService { FailureStep = "confirm" };
        var decisions = new FakeDecisionService { Continue = true };
        var orchestrator = new GameLaunchOrchestrator(processes, decisions, NullLogger<GameLaunchOrchestrator>.Instance);
        var step = Step("confirm", LaunchPhase.BeforeGame);
        step.FailurePolicy = LaunchFailurePolicy.Ask;

        var result = await orchestrator.LaunchAsync(CreateRequest(CreateProfile(step)));

        Assert.Equal(LaunchSessionStatus.Completed, result.Status);
        Assert.Equal(1, decisions.CallCount);
    }

    private static GameLaunchOrchestrator CreateOrchestrator(FakeProcessService processes) =>
        new(processes, new FakeDecisionService(), NullLogger<GameLaunchOrchestrator>.Instance);

    private static LaunchProfile CreateProfile(params LaunchStep[] steps) => new()
    {
        GameId = "test",
        Name = "test profile",
        Steps = [.. steps]
    };

    private static LaunchStep Step(string name, LaunchPhase phase) => new()
    {
        Name = name,
        ExecutablePath = $"E:\\{name}.exe",
        Phase = phase,
        FailurePolicy = LaunchFailurePolicy.Abort
    };

    private static GameLaunchRequest CreateRequest(LaunchProfile profile, FakeDetector? detector = null) => new(
        new GameDefinition(
            "test",
            "Test Game",
            "Test Publisher",
            "test",
            "T",
            "#000000",
            "#FFFFFF",
            ["game.exe"],
            [],
            new HashSet<GameFeature> { GameFeature.Launch }),
        "E:\\game.exe",
        profile,
        detector ?? new FakeDetector());

    private sealed class FakeProcessService : ICompanionProcessService
    {
        public List<string> Events { get; } = [];

        public string? FailureStep { get; set; }

        public Task<ProcessLaunchResult> StartAsync(LaunchStep step, Guid sessionId, CancellationToken cancellationToken = default)
        {
            Events.Add($"start:{step.Name}");
            if (step.Name == FailureStep)
            {
                return Task.FromResult(new ProcessLaunchResult(ProcessLaunchStatus.Failed, "simulated"));
            }

            var token = new OwnedProcessToken(Guid.NewGuid(), 42, sessionId, step.Name);
            return Task.FromResult(new ProcessLaunchResult(ProcessLaunchStatus.Started, "started", token));
        }

        public Task WaitForExitAsync(OwnedProcessToken process, CancellationToken cancellationToken = default)
        {
            Events.Add($"wait:{process.StepName}");
            return Task.CompletedTask;
        }

        public Task StopOwnedProcessesAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            Events.Add("cleanup");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDetector : IGameProcessDetector
    {
        public int StartWaitCount { get; private set; }

        public int ExitWaitCount { get; private set; }

        public Task<bool> IsRunningAsync(GameDefinition game, string? expectedExecutablePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> WaitForStartAsync(GameDefinition game, string? expectedExecutablePath, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            StartWaitCount++;
            return Task.FromResult(true);
        }

        public Task WaitForExitAsync(GameDefinition game, string? expectedExecutablePath, CancellationToken cancellationToken = default)
        {
            ExitWaitCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDecisionService : ILaunchDecisionService
    {
        public bool Continue { get; init; }

        public int CallCount { get; private set; }

        public Task<bool> ShouldContinueAsync(LaunchStep failedStep, ProcessLaunchResult failure, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Continue);
        }
    }
}
