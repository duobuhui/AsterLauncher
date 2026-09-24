using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class GameDirectoryInstallScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aster-game-directory-{Guid.NewGuid():N}");

    public GameDirectoryInstallScannerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Scan_FindsStarRailInNestedDirectory()
    {
        var directory = Path.Combine(_root, "HoYoPlay", "games", "Star Rail Game");
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "StarRail.exe");
        File.WriteAllBytes(executable, [0]);

        var adapter = BuiltInGameCatalog.CreateAdapters().Single(item => item.Definition.Id == BuiltInGameIds.HonkaiStarRail);
        var result = GameDirectoryInstallScanner.Scan(adapter, _root);

        Assert.Equal(ScanResultKind.Found, result.Kind);
        Assert.Equal(executable, result.Installation?.ExecutablePath);
    }

    [Fact]
    public void Scan_DoesNotChooseBetweenTwoInstallations()
    {
        foreach (var name in new[] { "version-a", "version-b" })
        {
            var directory = Path.Combine(_root, name);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "StarRail.exe"), [0]);
        }

        var adapter = BuiltInGameCatalog.CreateAdapters().Single(item => item.Definition.Id == BuiltInGameIds.HonkaiStarRail);
        Assert.Equal(ScanResultKind.InvalidPath, GameDirectoryInstallScanner.Scan(adapter, _root).Kind);
    }

    [Fact]
    public void Scan_UnknownExecutableNameRequiresManualSelection()
    {
        var adapter = BuiltInGameCatalog.CreateAdapters().Single(item => item.Definition.Id == BuiltInGameIds.PetitPlanet);
        Assert.Equal(ScanResultKind.NotFound, GameDirectoryInstallScanner.Scan(adapter, _root).Kind);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
