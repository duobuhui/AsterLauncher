using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldInstallLocatorTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"aster-endfield-locator-{Guid.NewGuid():N}");

    public EndfieldInstallLocatorTests() => Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public async Task ScanAsync_SavedExecutableWinsBeforeOtherEvidence()
    {
        var executablePath = CreateExecutable(Path.Combine(_temporaryDirectory, EndfieldInstallLocator.VerifiedExecutableName));
        var evidence = new FakeEvidenceSource
        {
            Running = new RunningExecutableEvidence([@"Z:\missing\Endfield.exe"], 0, []),
            Candidates = [new LauncherInstallCandidate("should-not-run", @"Z:\missing")]
        };

        var results = await new EndfieldInstallLocator(executablePath, evidence).ScanAsync();

        var found = Assert.Single(results);
        Assert.Equal(ScanResultKind.Found, found.Kind);
        Assert.Equal(executablePath, found.Installation?.ExecutablePath);
        Assert.Equal(0, evidence.RunningReadCount);
        Assert.Equal(0, evidence.RegistryReadCount);
    }

    [Fact]
    public async Task ScanAsync_FindsRunningExecutablePath()
    {
        var executablePath = CreateExecutable(Path.Combine(_temporaryDirectory, "running", EndfieldInstallLocator.VerifiedExecutableName));
        var evidence = new FakeEvidenceSource
        {
            Running = new RunningExecutableEvidence([executablePath], 0, [])
        };

        var results = await new EndfieldInstallLocator(evidenceSource: evidence).ScanAsync();

        Assert.Contains(results, result => result.Kind == ScanResultKind.Found && result.Installation?.ExecutablePath == executablePath);
        Assert.Equal(0, evidence.RegistryReadCount);
    }

    [Fact]
    public async Task ScanAsync_UsesVerifiedLauncherRelativePath()
    {
        var launcherRoot = Path.Combine(_temporaryDirectory, "launcher");
        var executablePath = CreateExecutable(Path.Combine(launcherRoot, EndfieldInstallLocator.VerifiedRelativePath));
        var evidence = new FakeEvidenceSource
        {
            Candidates = [new LauncherInstallCandidate("GRYPHLINK test evidence", launcherRoot)]
        };

        var results = await new EndfieldInstallLocator(evidenceSource: evidence).ScanAsync();

        Assert.Contains(results, result => result.Kind == ScanResultKind.Found && result.Installation?.ExecutablePath == executablePath);
    }

    [Fact]
    public async Task ScanAsync_ReportsExistingRootWithoutExecutableAsInvalidPath()
    {
        var launcherRoot = Path.Combine(_temporaryDirectory, "launcher-without-game");
        Directory.CreateDirectory(launcherRoot);
        var evidence = new FakeEvidenceSource
        {
            Candidates = [new LauncherInstallCandidate("鹰角启动器 test evidence", launcherRoot)]
        };

        var results = await new EndfieldInstallLocator(evidenceSource: evidence).ScanAsync();

        Assert.Contains(results, result => result.Kind == ScanResultKind.InvalidPath && result.Source.Contains("鹰角启动器"));
    }

    [Fact]
    public async Task ScanAsync_ReturnsCancelledDiagnostic()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var results = await new EndfieldInstallLocator(evidenceSource: new FakeEvidenceSource())
            .ScanAsync(cancellationToken: cancellation.Token);

        var cancelled = Assert.Single(results);
        Assert.Equal(ScanResultKind.Cancelled, cancelled.Kind);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private static string CreateExecutable(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x4D, 0x5A]);
        return path;
    }

    private sealed class FakeEvidenceSource : IEndfieldInstallEvidenceSource
    {
        public RunningExecutableEvidence Running { get; init; } = new([], 0, []);

        public IReadOnlyList<LauncherInstallCandidate> Candidates { get; init; } = [];

        public int RunningReadCount { get; private set; }

        public int RegistryReadCount { get; private set; }

        public RunningExecutableEvidence GetRunningExecutables()
        {
            RunningReadCount++;
            return Running;
        }

        public IReadOnlyList<LauncherInstallCandidate> GetLauncherInstallCandidates()
        {
            RegistryReadCount++;
            return Candidates;
        }
    }
}
