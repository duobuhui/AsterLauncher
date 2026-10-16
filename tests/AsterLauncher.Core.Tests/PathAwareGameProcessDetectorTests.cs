using System.Diagnostics;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class PathAwareGameProcessDetectorTests
{
    [Fact]
    public async Task OwnedProcessMatchesOnlyItsFullPathAndDetectsItsNaturalExit()
    {
        var repository = RepositoryRoot();
        var parent = Environment.GetEnvironmentVariable("ASTERLAUNCHER_TEST_TEMP_ROOT")
            ?? Path.Combine(repository, ".artifacts", "tests");
        var root = Path.GetFullPath(Path.Combine(parent, "path-aware-process-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        Process? owned = null;
        try
        {
            var name = "aster-detector-" + Guid.NewGuid().ToString("N") + ".exe";
            var executable = Path.Combine(root, name);
            // A private copy gives this test a unique process name and no dependency on
            // another test's Node instance or any game installed on the machine.
            File.Copy(Path.Combine(repository, ".artifacts", "vendor", "node-v24.19.0", "node.exe"), executable);
            var other = Path.Combine(root, "other", name);
            Directory.CreateDirectory(Path.GetDirectoryName(other)!);
            File.WriteAllBytes(other, []);
            var ready = Path.Combine(root, "ready.txt");
            var info = new ProcessStartInfo(executable)
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
            };
            info.Environment["TEMP"] = root;
            info.Environment["TMP"] = root;
            info.ArgumentList.Add("-e");
            info.ArgumentList.Add("require('fs').writeFileSync(process.argv[1],'ready');process.stdin.resume();process.stdin.on('end',()=>process.exit(0));");
            info.ArgumentList.Add(ready);
            owned = Process.Start(info) ?? throw new InvalidOperationException("Owned detector fixture did not start.");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (!File.Exists(ready) && !owned.HasExited && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
            Assert.True(File.Exists(ready));
            var definition = new GameDefinition("owned-detector", "Owned fixture", "Tests", "fixture", "T", "#000000", "#FFFFFF",
                [name], [], new HashSet<GameFeature> { GameFeature.Launch });
            var detector = new PathAwareGameProcessDetector();

            Assert.True(await detector.IsRunningAsync(definition, executable));
            Assert.True(await detector.IsRunningAsync(definition, executable.ToUpperInvariant()));
            // CI may create E: through SUBST; both aliases must identify the same image path.
            Assert.True(await detector.IsRunningAsync(definition, PlaytimeTrackingService.ResolvePhysicalExecutablePath(executable)));
            Assert.False(await detector.IsRunningAsync(definition, other));
            Assert.True(await detector.WaitForStartAsync(definition, executable, TimeSpan.FromSeconds(2)));
            var waitingForExit = detector.WaitForExitAsync(definition, executable);
            Assert.False(waitingForExit.IsCompleted);
            owned.StandardInput.Close();
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await waitingForExit.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await detector.IsRunningAsync(definition, executable));
        }
        finally
        {
            if (owned is not null)
            {
                if (!owned.HasExited) owned.Kill(); // This original handle belongs only to this test's private fixture.
                await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                owned.Dispose();
            }
            Directory.Delete(root, true);
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AsterLauncher.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("The test repository is unavailable.");
    }
}