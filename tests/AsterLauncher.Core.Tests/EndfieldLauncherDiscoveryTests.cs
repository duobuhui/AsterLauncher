using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldLauncherDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "endfield-launcher-discovery-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GenericGryphlinkEvidenceRemainsUnknownDespiteRequestedOfficialChannelAndDirectoryName()
    {
        var path = FileAt("Official", "Launcher.exe");
        var service = Service(new EndfieldLauncherRegistration("registry", "GRYPHLINK", Path.GetDirectoryName(path), path));
        var candidate = Assert.Single(await service.DiscoverAsync(EndfieldChannel.Official));
        Assert.Equal(EndfieldChannel.Unknown, candidate.Channel);
        Assert.False(candidate.IsManualSelection);
    }

    [Fact]
    public async Task ExplicitOppositeChannelEvidenceIsNotAdopted()
    {
        var path = FileAt("Launcher.exe");
        var service = Service(new EndfieldLauncherRegistration("registry", "终末地 bilibili", _root, path, EndfieldChannel.Bilibili));
        Assert.Empty(await service.DiscoverAsync(EndfieldChannel.Official));
    }

    [Fact]
    public async Task ExplicitManualChoiceAssociatesSelectedChannelAndCanRemainUnrecognizedHandoffOnly()
    {
        var path = FileAt("user-chosen-custom-name.exe");
        var service = Service();
        var candidate = Assert.Single(await service.DiscoverAsync(EndfieldChannel.Bilibili, path));
        Assert.Equal(EndfieldChannel.Bilibili, candidate.Channel);
        Assert.True(candidate.IsManualSelection);
        Assert.False(candidate.Trust.IsRecognizedPublisher);
        Assert.False(candidate.CanUseAsBridgeParent);
        Assert.Null(candidate.BridgeParentPath);
    }

    [Fact]
    public async Task ManualUnknownChannelIsNeverPromotedToOfficial()
    {
        var path = FileAt("Launcher.exe");
        var candidate = Assert.Single(await Service().DiscoverAsync(EndfieldChannel.Unknown, path));
        Assert.Equal(EndfieldChannel.Unknown, candidate.Channel);
    }

    [Fact]
    public async Task ManualCandidateWinsWhenRegistryContainsTheSamePath()
    {
        var path = FileAt("Launcher.exe");
        var candidate = Assert.Single(await Service(new EndfieldLauncherRegistration("registry", "GRYPHLINK", _root, path))
            .DiscoverAsync(EndfieldChannel.Bilibili, path));
        Assert.True(candidate.IsManualSelection);
        Assert.Equal(EndfieldChannel.Bilibili, candidate.Channel);
    }

    [Fact]
    public async Task RootLauncherIsLinkedToNewestVerifiedGamesComponent()
    {
        var launcher = FileAt("Launcher.exe");
        FileAt("1.5.0", "Games.exe");
        var expected = FileAt("1.6.0", "Games.exe");
        FileAt("9.0.0", "Games.exe");
        var trust = new EndfieldLauncherTrust(path => new(!path.Contains("9.0.0"),
            "Shanghai Hypergryph Network Technology Co., Ltd.", "O=Hypergryph", "AB", 0));
        var service = ServiceWithTrust(trust, new EndfieldLauncherRegistration("registry", "GRYPHLINK", _root, launcher));
        var candidates = await service.DiscoverAsync(EndfieldChannel.Official);
        Assert.Equal(expected, Assert.Single(candidates, candidate => candidate.ExecutablePath == launcher).BridgeParentPath);
        Assert.Equal(expected, (await service.ResolveBridgeParentAsync(launcher))?.ExecutablePath);
    }

    [Fact]
    public async Task BridgeResolutionNeverRecursesOrUsesUnsignedSameNamedComponent()
    {
        var launcher = FileAt("Launcher.exe");
        FileAt("1.6.0", "Games.exe");
        FileAt("nested", "1.9.0", "Games.exe");
        var service = Service(new EndfieldLauncherRegistration("registry", "GRYPHLINK", _root, launcher));
        Assert.Null(await service.ResolveBridgeParentAsync(launcher));
        Assert.DoesNotContain(await service.DiscoverAsync(EndfieldChannel.Unknown), candidate => candidate.ExecutablePath.Contains("1.9.0"));
    }

    [Fact]
    public async Task UnrelatedExecutableFromRegistryIsNotAnAutomaticLauncherCandidate()
    {
        var unrelated = FileAt("Endfield.exe");
        Assert.Empty(await Service(new EndfieldLauncherRegistration("registry", "终末地", _root, unrelated)).DiscoverAsync(EndfieldChannel.Official));
    }

    [Fact]
    public async Task CancelledDiscoveryDoesNotReadRegistry()
    {
        var evidence = new FakeEvidence([]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EndfieldLauncherDiscovery(evidence)
            .DiscoverAsync(EndfieldChannel.Official, cancellationToken: cancellation.Token));
        Assert.Equal(0, evidence.Reads);
    }

    [Theory]
    [InlineData("鹰角启动器", EndfieldChannel.Unknown)]
    [InlineData("GRYPHLINK", EndfieldChannel.Unknown)]
    [InlineData("终末地", EndfieldChannel.Unknown)]
    [InlineData("Arknights Endfield bilibili", EndfieldChannel.Bilibili)]
    [InlineData("终末地 官服", EndfieldChannel.Official)]
    [InlineData("bilibili 客户端", EndfieldChannel.Unknown)]
    public void ChannelClassificationNeedsExplicitGameAndChannelLabels(string name, EndfieldChannel expected) =>
        Assert.Equal(expected, WindowsEndfieldLauncherEvidenceSource.ReadExplicitChannel(name));

    [Theory]
    [InlineData("\"E:\\Games\\Hypergryph Launcher\\Launcher.exe\" \"%1\"", "E:\\Games\\Hypergryph Launcher\\Launcher.exe")]
    [InlineData("E:\\Games\\Hypergryph Launcher\\Launcher.exe,0", "E:\\Games\\Hypergryph Launcher\\Launcher.exe")]
    [InlineData("\"E:\\Games\\Launcher.exe\",0", "E:\\Games\\Launcher.exe")]
    [InlineData("cmd.exe /c E:\\Games\\Launcher.exe", null)]
    [InlineData("\"E:\\Games\\Endfield.exe\"", null)]
    [InlineData("\"\\\\server\\share\\Launcher.exe\"", null)]
    [InlineData("\"E:\\Games\\Launcher.exe:payload.exe\"", null)]
    public void ProtocolCommandParsingDoesNotExecuteCommandsOrAcceptOtherExecutables(string command, string? expected) =>
        Assert.Equal(expected, WindowsEndfieldLauncherEvidenceSource.ReadCommandExecutable(command));

    private static EndfieldLauncherDiscovery Service(params EndfieldLauncherRegistration[] registrations) =>
        ServiceWithTrust(new EndfieldLauncherTrust(_ => new(false, null, null, null, -1)), registrations);
    private static EndfieldLauncherDiscovery ServiceWithTrust(EndfieldLauncherTrust trust, params EndfieldLauncherRegistration[] registrations) =>
        new(new FakeEvidence(registrations), trust);
    private string FileAt(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x4D, 0x5A]);
        return path;
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class FakeEvidence(IReadOnlyList<EndfieldLauncherRegistration> registrations) : IEndfieldLauncherEvidenceSource
    {
        public int Reads { get; private set; }
        public IReadOnlyList<EndfieldLauncherRegistration> GetRegistrations() { Reads++; return registrations; }
    }
}
