using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class PlaytimeTrackingTests : IDisposable
{
    private readonly string _root;

    public PlaytimeTrackingTests()
    {
        var parent = Environment.GetEnvironmentVariable("ASTERLAUNCHER_TEST_TEMP_ROOT")
            ?? Path.Combine(RepositoryRoot(), ".artifacts", "tests");
        _root = Path.GetFullPath(Path.Combine(parent, "playtime-tracking-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void TargetsIncludeHiddenGamesAndEveryRegisteredLocalChannelWithoutDuplicates()
    {
        var starRailOfficial = Exe("starrail-official", "StarRail.exe");
        var starRailBilibili = Exe("starrail-bilibili", "StarRail.exe");
        var endfieldOfficial = Exe("endfield-official", "Endfield.exe");
        var endfieldBilibili = Exe("endfield-bilibili", "Endfield.exe");
        var configuration = new LauncherConfiguration
        {
            HiddenGameIds = [BuiltInGameIds.HonkaiStarRail],
            Games = [new GameUserState
            {
                GameId = BuiltInGameIds.HonkaiStarRail,
                ExecutablePath = starRailOfficial,
                HoYoInstallation = new HoYoInstallation { ExecutablePath = starRailOfficial },
                HoYoInstallations =
                [
                    new HoYoInstallation { Channel = HoYoChannel.Official, ExecutablePath = starRailOfficial.ToUpperInvariant() },
                    new HoYoInstallation { Channel = HoYoChannel.Bilibili, ExecutablePath = starRailBilibili }
                ]
            }],
            EndfieldInstallations =
            [
                new EndfieldInstallation { Channel = EndfieldChannel.Official, ExecutablePath = endfieldOfficial },
                new EndfieldInstallation { Channel = EndfieldChannel.Bilibili, ExecutablePath = endfieldBilibili }
            ]
        };

        var targets = PlaytimeTargetCatalog.Build(configuration);

        Assert.Equal(4, targets.Count);
        AssertTarget(targets, BuiltInGameIds.HonkaiStarRail, starRailOfficial);
        AssertTarget(targets, BuiltInGameIds.HonkaiStarRail, starRailBilibili);
        AssertTarget(targets, BuiltInGameIds.Endfield, endfieldOfficial);
        AssertTarget(targets, BuiltInGameIds.Endfield, endfieldBilibili);
    }

    [Fact]
    public void EmulatorAndCompanionProcessesAreNotGameTargets()
    {
        var emulator = Exe("emulator", "MuMuPlayer.exe");
        var localGame = Exe("local", "BH3.exe");
        var companion = Exe("tools", "helper.exe");
        var configuration = new LauncherConfiguration
        {
            Games =
            [
                new GameUserState
                {
                    GameId = BuiltInGameIds.Arknights, LaunchTarget = GameLaunchTarget.Emulator,
                    ExecutablePath = emulator, EmulatorExecutablePath = emulator
                },
                new GameUserState { GameId = BuiltInGameIds.HonkaiImpact3rd, ExecutablePath = localGame }
            ],
            LaunchProfiles = [new LaunchProfile
            {
                GameId = BuiltInGameIds.HonkaiImpact3rd,
                Steps = [new LaunchStep { ExecutablePath = companion, Phase = LaunchPhase.AfterGame }]
            }],
            CompanionTools = [new CompanionTool { ExecutablePath = companion }]
        };

        var target = Assert.Single(PlaytimeTargetCatalog.Build(configuration));

        Assert.Equal(BuiltInGameIds.HonkaiImpact3rd, target.GameId);
        Assert.Equal(localGame, target.ExecutablePath);
    }

    [Fact]
    public void CloudBrowserIsExcludedWhileSavedLocalInstallationsRemainObservable()
    {
        var browser = Exe("browser", "msedge.exe");
        var local = Exe("starrail-official", "StarRail.exe");
        var endfield = Exe("endfield-official", "Endfield.exe");
        var configuration = new LauncherConfiguration
        {
            Games =
            [
                new GameUserState
                {
                    GameId = BuiltInGameIds.HonkaiStarRail, LaunchTarget = GameLaunchTarget.OfficialCloud,
                    ExecutablePath = browser,
                    HoYoInstallations = [new HoYoInstallation { ExecutablePath = local }]
                },
                new GameUserState
                {
                    GameId = BuiltInGameIds.Endfield, LaunchTarget = GameLaunchTarget.OfficialCloud,
                    ExecutablePath = browser
                },
                new GameUserState
                {
                    GameId = BuiltInGameIds.GenshinImpact, LaunchTarget = GameLaunchTarget.OfficialCloud,
                    ExecutablePath = browser
                }
            ],
            EndfieldInstallations = [new EndfieldInstallation { ExecutablePath = endfield }],
            LaunchProfiles = [new LaunchProfile
            {
                GameId = BuiltInGameIds.HonkaiStarRail,
                Steps = [new LaunchStep { ExecutablePath = browser }]
            }]
        };

        var targets = PlaytimeTargetCatalog.Build(configuration);

        Assert.Equal(2, targets.Count);
        AssertTarget(targets, BuiltInGameIds.HonkaiStarRail, local);
        AssertTarget(targets, BuiltInGameIds.Endfield, endfield);
        Assert.DoesNotContain(targets, target => target.ExecutablePath == browser);
    }

    [Fact]
    public void CloudSelectionRetainsAnOriginalLocalExecutableAndCustomLocalPathsStaySupported()
    {
        var local = Exe("genshin", "YuanShen.exe");
        var custom = Exe("custom", "A Different Game.exe");
        var configuration = new LauncherConfiguration
        {
            Games =
            [
                new GameUserState
                {
                    GameId = BuiltInGameIds.GenshinImpact, LaunchTarget = GameLaunchTarget.OfficialCloud,
                    ExecutablePath = local
                },
                new GameUserState { GameId = "custom-game", IsCustom = true, ExecutablePath = custom }
            ]
        };

        var targets = PlaytimeTargetCatalog.Build(configuration);

        Assert.Equal(2, targets.Count);
        AssertTarget(targets, BuiltInGameIds.GenshinImpact, local);
        AssertTarget(targets, "custom-game", custom);
    }

    [Fact]
    public async Task PollFiltersOnlyExistingAbsoluteExecutableTargetsAndPreservesExactDirectories()
    {
        var first = Exe("official", "StarRail.exe");
        var second = Exe("bilibili", "StarRail.exe");
        IReadOnlyList<PlaytimeTarget>? received = null;
        var observerStarts = 0;
        var service = new PlaytimeTrackingService(new PlaytimeJournal(_root), targets =>
        {
            received = targets;
            return new([], new HashSet<string>());
        }, () => observerStarts++, () => true);

        var result = await service.PollAsync(
        [
            new(BuiltInGameIds.HonkaiStarRail, first),
            new(BuiltInGameIds.HonkaiStarRail.ToUpperInvariant(), first.ToUpperInvariant()),
            new(BuiltInGameIds.HonkaiStarRail, second),
            new("", first),
            new("relative", "relative.exe"),
            new("missing", Path.Combine(_root, "missing.exe"))
        ]);

        Assert.NotNull(received);
        Assert.Equal(2, received.Count);
        AssertTarget(received, BuiltInGameIds.HonkaiStarRail, first);
        AssertTarget(received, BuiltInGameIds.HonkaiStarRail, second);
        Assert.Empty(result.RunningGameIds);
        Assert.Empty(result.Observations);
        Assert.Equal(0, observerStarts);
    }

    [Fact]
    public async Task LiveObserverIsNotRestartedAndRepeatedPollDoesNotRequeueAConsumedIdentity()
    {
        var path = Exe("game", "StarRail.exe");
        var observation = Observation(path);
        var journal = new PlaytimeJournal(_root);
        var observerStarts = 0;
        var service = new PlaytimeTrackingService(journal, _ => new([observation], new HashSet<string>()),
            () => observerStarts++, () => true);

        var first = await service.PollAsync([new(observation.GameId, path)]);
        var queued = Assert.Single(Directory.GetFiles(journal.RequestDirectory, "*.json"));
        var request = JsonSerializer.Deserialize<PlaytimeObservation>(await File.ReadAllTextAsync(queued), PlaytimeJournal.JsonOptions)!;
        Assert.Equal(observation.SessionId, request.SessionId);
        File.Delete(queued); // Simulate the already running helper consuming its request.
        var second = await service.PollAsync([new(observation.GameId, path)]);

        Assert.Empty(Directory.GetFiles(journal.RequestDirectory, "*.json"));
        Assert.Contains(observation.GameId, first.RunningGameIds);
        Assert.Contains(observation.GameId, second.RunningGameIds);
        Assert.Equal(0, observerStarts);
    }

    [Fact]
    public async Task MissingObserverIsStartedOnceDuringRetryCooldownAndDuplicatePollsKeepOneRequest()
    {
        var path = Exe("game", "StarRail.exe");
        var observation = Observation(path);
        var journal = new PlaytimeJournal(_root);
        var observerStarts = 0;
        var service = new PlaytimeTrackingService(journal, _ => new([observation], new HashSet<string>()),
            () => observerStarts++, () => false);

        await service.PollAsync([new(observation.GameId, path)]);
        await service.PollAsync([new(observation.GameId, path)]);

        Assert.Single(Directory.GetFiles(journal.RequestDirectory, "*.json"));
        Assert.Equal(1, observerStarts);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("win32")]
    [InlineData("invalid")]
    public async Task ObserverFailureRemainsUnavailableThroughoutCooldownWithoutAnEscalationAttempt(string failure)
    {
        var path = Exe("game", "StarRail.exe");
        var observation = Observation(path);
        var attempts = 0;
        var service = new PlaytimeTrackingService(new PlaytimeJournal(_root),
            _ => new([observation], new HashSet<string>()),
            () =>
            {
                attempts++;
                Exception exception = failure switch
                {
                    "access" => new UnauthorizedAccessException("fixture"),
                    "win32" => new Win32Exception(5),
                    "invalid" => new InvalidOperationException("fixture"),
                    _ => new IOException("fixture")
                };
                throw exception;
            }, () => false);

        var first = await service.PollAsync([new(observation.GameId, path)]);
        var second = await service.PollAsync([new(observation.GameId, path)]);

        Assert.Contains(observation.GameId, first.UnavailableGameIds);
        Assert.Contains(observation.GameId, second.UnavailableGameIds);
        Assert.Contains(observation.GameId, first.RunningGameIds);
        Assert.Equal(1, attempts); // Failure does not trigger another launch or privilege retry.
    }

    [Fact]
    public async Task ScannerAccessFailuresAreVisibleWithoutStartingAnObserver()
    {
        var path = Exe("protected", "StarRail.exe");
        var starts = 0;
        var service = new PlaytimeTrackingService(new PlaytimeJournal(_root),
            _ => new([], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BuiltInGameIds.HonkaiStarRail }),
            () => starts++, () => false);

        var result = await service.PollAsync([new(BuiltInGameIds.HonkaiStarRail, path)]);

        Assert.Empty(result.RunningGameIds);
        Assert.Contains(BuiltInGameIds.HonkaiStarRail, result.UnavailableGameIds);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task ReusedPidWithANewCreationTimeQueuesANewIdentity()
    {
        var path = Exe("game", "StarRail.exe");
        var oldProcess = Observation(path);
        var replacement = oldProcess with { StartedAt = oldProcess.StartedAt.AddMinutes(3), ObservedUntil = oldProcess.StartedAt.AddMinutes(3) };
        replacement = replacement with
        {
            SessionId = PlaytimeIdentity.CreateSessionId(replacement.GameId, path, replacement.ProcessId, replacement.StartedAt)
        };
        var journal = new PlaytimeJournal(_root);
        await journal.WriteAsync(oldProcess with { ObservedUntil = oldProcess.StartedAt.AddMinutes(1), IsComplete = true });
        var service = new PlaytimeTrackingService(journal,
            _ => new([replacement], new HashSet<string>()), () => { }, () => true);

        var result = await service.PollAsync([new(replacement.GameId, path)]);

        Assert.NotEqual(oldProcess.SessionId, replacement.SessionId);
        Assert.Equal(oldProcess.SessionId, Assert.Single(result.Observations).SessionId);
        var request = Assert.Single(Directory.GetFiles(journal.RequestDirectory, "*.json"));
        Assert.Equal(replacement.SessionId + ".json", Path.GetFileName(request));
    }

    [Fact]
    public async Task AlreadyCompletedIdentityIsReturnedForSettlementWithoutRequeuing()
    {
        var path = Exe("game", "StarRail.exe");
        var process = Observation(path);
        var completed = process with { ObservedUntil = process.StartedAt.AddMinutes(20), IsComplete = true };
        var journal = new PlaytimeJournal(_root);
        await journal.WriteAsync(completed);
        var service = new PlaytimeTrackingService(journal, _ => new([process], new HashSet<string>()), () => { }, () => true);

        var result = await service.PollAsync([new(process.GameId, path)]);

        Assert.Equal(completed, Assert.Single(result.Observations));
        Assert.False(Directory.Exists(journal.RequestDirectory));
    }

    [Fact]
    public async Task RealOwnedProcessScanUsesItsExactImagePathAndKernelCreationTime()
    {
        var node = Path.Combine(RepositoryRoot(), ".artifacts", "vendor", "node-v24.19.0", "node.exe");
        Assert.True(File.Exists(node), "The existing E-drive Node maintenance runtime is required for this isolated process check.");
        var sameNameOtherDirectory = Exe("unrelated", "node.exe");
        var start = new ProcessStartInfo(node)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            WorkingDirectory = _root
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("process.stdout.write('ready\\n');setTimeout(()=>{},30000)");
        start.Environment["TEMP"] = _root;
        start.Environment["TMP"] = _root;
        using var owned = Process.Start(start) ?? throw new InvalidOperationException("Could not start the owned process fixture.");
        try
        {
            Assert.Equal("ready", await owned.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var scannedAt = DateTimeOffset.UtcNow;
            var scan = PlaytimeTrackingService.Scan(
            [
                new("owned-node-fixture", node),
                new("different-directory", sameNameOtherDirectory)
            ]);
            var observation = Assert.Single(scan.Processes, item => item.ProcessId == owned.Id);

            Assert.Equal("owned-node-fixture", observation.GameId);
            Assert.True(PlaytimeTrackingService.SameExecutablePath(node, observation.ExecutablePath));
            Assert.Equal(new DateTimeOffset(owned.StartTime.ToUniversalTime()), observation.StartedAt);
            Assert.True(observation.StartedAt <= scannedAt);
            Assert.Equal(observation.StartedAt, observation.ObservedUntil);
            Assert.Equal(PlaytimeIdentity.CreateSessionId(observation.GameId, observation.ExecutablePath,
                owned.Id, observation.StartedAt), observation.SessionId);
            Assert.DoesNotContain(scan.Processes, item => item.GameId == "different-directory");
        }
        finally
        {
            if (!owned.HasExited) owned.Kill(); // Only the process created by this test is owned.
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ExitCheckpointWaitsForAFreshObserverReceiptWithoutGuessingDuration()
    {
        var path = Exe("checkpoint", "StarRail.exe");
        var observation = Observation(path);
        var journal = new PlaytimeJournal(_root);
        var scans = 0;
        using var service = new PlaytimeTrackingService(journal, _ =>
        {
            if (++scans == 2)
                journal.WriteAsync(observation with { ObservedUntil = DateTimeOffset.UtcNow }).GetAwaiter().GetResult();
            return new([observation], new HashSet<string>());
        }, () => { }, () => true);

        var result = await service.WaitForObserverAsync([new(observation.GameId, path)]);

        Assert.Equal(2, scans);
        Assert.Empty(result.PendingSessionIds);
        var saved = Assert.Single(result.Observations);
        Assert.False(saved.IsComplete);
        Assert.True(saved.ObservedUntil > saved.StartedAt);
    }

    [Fact]
    public async Task ExitCheckpointWithNoDiscoveredProcessesReturnsWithoutStartingAnObserver()
    {
        var starts = 0;
        using var service = new PlaytimeTrackingService(new PlaytimeJournal(_root),
            _ => new([], new HashSet<string>()), () => starts++, () => false);

        var result = await service.WaitForObserverAsync([]);

        Assert.Empty(result.PendingSessionIds);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task NativeHandoffPreservesAShortProcessThatExitsBeforeTheObserverOpensIt()
    {
        var repository = RepositoryRoot();
        var runtime = Path.Combine(repository, ".artifacts", "vendor", "node-v24.19.0", "node.exe");
        var executable = Path.Combine(_root, "AsterShortPlaytimeFixture.exe");
        File.Copy(runtime, executable);
        var wrapper = Path.Combine(_root, "delayed-observer.ps1");
        var observer = Path.Combine(repository, "src", "AsterLauncher.App", "Services", "WatchPlaytime.ps1");
        await File.WriteAllTextAsync(wrapper, "param([string]$DataRoot)\nStart-Sleep -Milliseconds 1200\n& '"
            + observer.Replace("'", "''") + "' -DataRoot $DataRoot\n");
        using var service = new PlaytimeTrackingService(_root, wrapper);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WorkingDirectory = _root
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("process.stdout.write('ready\\n');setTimeout(()=>{},30000)");
        start.Environment["TEMP"] = _root;
        start.Environment["TMP"] = _root;
        Process? owned = Process.Start(start) ?? throw new InvalidOperationException("Owned short process failed.");
        try
        {
            Assert.Equal("ready", await owned.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var first = await service.PollAsync([new("short-owned-game", executable)]);
            Assert.Single(first.PendingSessionIds);
            owned.Kill();
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            long exited = 0;
            var exitDeadline = DateTimeOffset.UtcNow.AddSeconds(2);
            do
            {
                Assert.True(GetOwnedProcessTimes(owned.SafeHandle, out _, out exited, out _, out _));
                if (exited == 0) await Task.Delay(10);
            }
            while (exited == 0 && DateTimeOffset.UtcNow < exitDeadline);
            Assert.True(exited > 0, "Windows must have confirmed a real process exit timestamp.");
            var exit = DateTimeOffset.FromFileTime(exited).ToUniversalTime();
            owned.Dispose();
            owned = null; // The controller pin must bridge the gap, not this test's process handle.

            var result = await service.WaitForObserverAsync([new("short-owned-game", executable)]);

            Assert.Empty(result.PendingSessionIds);
            var completed = Assert.Single(result.Observations);
            Assert.True(completed.IsComplete);
            Assert.Equal(exit, completed.ObservedUntil);
            var request = Path.Combine(_root, "playtime", "requests", completed.SessionId + ".json");
            var acknowledgedBy = DateTimeOffset.UtcNow.AddSeconds(10);
            while (File.Exists(request) && DateTimeOffset.UtcNow < acknowledgedBy) await Task.Delay(100);
            Assert.False(File.Exists(request));
            Assert.False(File.Exists(request + ".unavailable"));
        }
        finally
        {
            if (owned is not null)
            {
                if (!owned.HasExited) owned.Kill();
                await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                owned.Dispose();
            }
            // DeleteOnClose removes this exact fixture lock once its bounded observer goes idle.
            var lockPath = Path.Combine(_root, "playtime", "observer.lock");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
            do { await Task.Delay(100); }
            while (File.Exists(lockPath) && DateTimeOffset.UtcNow < deadline);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetProcessTimes", SetLastError = true)]
    private static extern bool GetOwnedProcessTimes(Microsoft.Win32.SafeHandles.SafeProcessHandle process,
        out long created, out long exited, out long kernel, out long user);

    [Fact]
    public void PhysicalPathNormalizationKeepsOrdinaryAbsoluteExecutablePathsUsable()
    {
        var path = Exe("physical-path", "game.exe");
        var physical = PlaytimeTrackingService.ResolvePhysicalExecutablePath(path);
        Assert.True(Path.IsPathFullyQualified(physical));
        Assert.False(physical.StartsWith(@"\\?\", StringComparison.Ordinal));
        Assert.True(PlaytimeTrackingService.SameExecutablePath(path, physical));
    }

    [Fact]
    public void EqualFileIdsInDifferentHardLinkEntriesDoNotAttributeAnotherInstallation()
    {
        var first = Exe("first-installation", "game.exe");
        var second = Path.Combine(_root, "second-installation", "game.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        Assert.True(WindowsHardLink.TryCreate(second, first));
        Assert.Equal(WindowsHardLink.Identity(first), WindowsHardLink.Identity(second));
        Assert.False(PlaytimeTrackingService.SameExecutablePath(first, second));
    }

    private string Exe(string directory, string name)
    {
        var folder = Path.Combine(_root, directory);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, []);
        return path;
    }

    private static PlaytimeObservation Observation(string path)
    {
        var started = DateTimeOffset.UtcNow.AddHours(-2);
        return new PlaytimeObservation
        {
            GameId = BuiltInGameIds.HonkaiStarRail,
            ExecutablePath = path,
            ProcessId = 12345,
            StartedAt = started,
            ObservedUntil = started,
            SessionId = PlaytimeIdentity.CreateSessionId(BuiltInGameIds.HonkaiStarRail, path, 12345, started)
        };
    }

    private static void AssertTarget(IReadOnlyList<PlaytimeTarget> targets, string gameId, string path)
        => Assert.Single(targets, target => string.Equals(target.GameId, gameId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AsterLauncher.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the existing launcher repository for E-drive fixtures.");
    }

    public void Dispose()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                // Windows can signal a terminated process just before its current-directory handle closes.
                Thread.Sleep(50);
            }
        }
    }
}