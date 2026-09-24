using Microsoft.Extensions.Logging;

namespace AsterLauncher.Core;

public sealed class GameLaunchOrchestrator(
    ICompanionProcessService processService,
    ILaunchDecisionService decisionService,
    ILogger<GameLaunchOrchestrator> logger)
{
    public async Task<LaunchSessionResult> LaunchAsync(
        GameLaunchRequest request,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sessionId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var results = new List<LaunchStepResult>();

        logger.LogInformation(
            "Launch session {SessionId} started for {GameId} using profile {ProfileName}",
            sessionId,
            request.Game.Id,
            request.Profile.Name);

        try
        {
            var enabledSteps = request.Profile.Steps.Where(step => step.IsEnabled).ToList();
            var takeover = enabledSteps.Any(step => step.TakesOverGameLaunch);

            if (!await ExecutePhaseAsync(LaunchPhase.BeforeGame).ConfigureAwait(false))
            {
                return await AbortAsync("启动前步骤失败，方案已中止。").ConfigureAwait(false);
            }

            OwnedProcessToken? gameProcess = null;
            foreach (var step in enabledSteps.Where(step => step.Phase == LaunchPhase.Game))
            {
                var outcome = await ExecuteStepAsync(step).ConfigureAwait(false);
                if (!outcome.ShouldContinue)
                {
                    return await AbortAsync("游戏阶段步骤失败，方案已中止。").ConfigureAwait(false);
                }

            }

            if (!takeover)
            {
                var gameArguments = request.Game.DefaultArguments
                    .Concat(ArgumentTokenizer.Parse(request.Profile.GameArguments));
                var defaultGameStep = new LaunchStep
                {
                    Name = $"启动 {request.Game.DisplayName}",
                    ExecutablePath = request.ExecutablePath,
                    Arguments = ArgumentTokenizer.Join(gameArguments),
                    WorkingDirectory = string.IsNullOrWhiteSpace(request.Profile.GameWorkingDirectory)
                        ? Path.GetDirectoryName(request.ExecutablePath)
                        : request.Profile.GameWorkingDirectory,
                    Phase = LaunchPhase.Game,
                    TimeoutSeconds = 15,
                    SkipIfAlreadyRunning = true,
                    FailurePolicy = LaunchFailurePolicy.Abort
                };

                var outcome = await ExecuteStepAsync(defaultGameStep).ConfigureAwait(false);
                if (!outcome.ShouldContinue)
                {
                    return await AbortAsync("游戏进程未能启动。").ConfigureAwait(false);
                }

                gameProcess ??= outcome.Result.Process;
            }

            if (gameProcess is null)
            {
                progress?.Report(new LaunchProgress(LaunchPhase.Game, "正在等待适配器识别游戏进程…"));
                var detected = await request.ProcessDetector.WaitForStartAsync(
                    request.Game,
                    request.ExecutablePath,
                    TimeSpan.FromSeconds(Math.Max(1, request.Profile.GameDetectionTimeoutSeconds)),
                    cancellationToken).ConfigureAwait(false);

                if (!detected)
                {
                    logger.LogWarning("Game process was not detected for launch session {SessionId}", sessionId);
                    await processService.StopOwnedProcessesAsync(sessionId, cancellationToken).ConfigureAwait(false);
                    return Finish(LaunchSessionStatus.GameNotDetected, "启动步骤已执行，但在超时前没有识别到游戏进程。");
                }
            }

            if (!await ExecutePhaseAsync(LaunchPhase.AfterGame).ConfigureAwait(false))
            {
                return await AbortAsync("启动后步骤失败，方案已中止。").ConfigureAwait(false);
            }

            progress?.Report(new LaunchProgress(LaunchPhase.Game, "游戏运行中，正在等待退出…"));
            if (gameProcess is not null)
            {
                await processService.WaitForExitAsync(gameProcess, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await request.ProcessDetector.WaitForExitAsync(
                    request.Game,
                    request.ExecutablePath,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!await ExecutePhaseAsync(LaunchPhase.OnGameExit).ConfigureAwait(false))
            {
                return await AbortAsync("退出后任务失败，方案已中止。").ConfigureAwait(false);
            }

            await processService.StopOwnedProcessesAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return Finish(LaunchSessionStatus.Completed, "游戏会话已结束。");
        }
        catch (OperationCanceledException)
        {
            await processService.StopOwnedProcessesAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            return Finish(LaunchSessionStatus.Cancelled, "启动方案已取消。");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled error in launch session {SessionId}", sessionId);
            await processService.StopOwnedProcessesAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            return Finish(LaunchSessionStatus.Aborted, $"启动编排发生异常：{exception.Message}");
        }

        async Task<bool> ExecutePhaseAsync(LaunchPhase phase)
        {
            foreach (var step in request.Profile.Steps.Where(step => step.IsEnabled && step.Phase == phase))
            {
                var outcome = await ExecuteStepAsync(step).ConfigureAwait(false);
                if (!outcome.ShouldContinue)
                {
                    return false;
                }
            }

            return true;
        }

        async Task<(bool ShouldContinue, ProcessLaunchResult Result)> ExecuteStepAsync(LaunchStep step)
        {
            progress?.Report(new LaunchProgress(step.Phase, $"正在执行：{step.Name}", step));
            if (step.DelayMilliseconds > 0)
            {
                await Task.Delay(step.DelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }

            ProcessLaunchResult result;
            try
            {
                result = await processService.StartAsync(step, sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Step {StepName} threw during launch", step.Name);
                result = new ProcessLaunchResult(ProcessLaunchStatus.Failed, exception.Message);
            }

            results.Add(new LaunchStepResult(step.Clone(), result));
            if (result.IsSuccess)
            {
                return (true, result);
            }

            logger.LogWarning("Step {StepName} failed with {Status}: {Message}", step.Name, result.Status, result.Message);
            var shouldContinue = step.FailurePolicy switch
            {
                LaunchFailurePolicy.Continue => true,
                LaunchFailurePolicy.Abort => false,
                LaunchFailurePolicy.Ask => await decisionService.ShouldContinueAsync(step, result, cancellationToken)
                    .ConfigureAwait(false),
                _ => false
            };

            return (shouldContinue, result);
        }

        async Task<LaunchSessionResult> AbortAsync(string message)
        {
            await processService.StopOwnedProcessesAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            return Finish(LaunchSessionStatus.Aborted, message);
        }

        LaunchSessionResult Finish(LaunchSessionStatus status, string message)
        {
            var endedAt = DateTimeOffset.UtcNow;
            logger.LogInformation(
                "Launch session {SessionId} ended with {Status} after {Duration}",
                sessionId,
                status,
                endedAt - startedAt);
            return new LaunchSessionResult(sessionId, status, startedAt, endedAt, results.AsReadOnly(), message);
        }
    }
}
