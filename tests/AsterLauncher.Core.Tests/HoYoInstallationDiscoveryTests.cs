using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class HoYoInstallationDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hoyo-discovery-" + Guid.NewGuid().ToString("N"));
    public HoYoInstallationDiscoveryTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "StarRail.exe"), [0]);
    }
    private void Metadata(string channel = "1", string sub = "1", string cps = "hyp_mihoyo", string biz = "hkrpg_cn")
        => File.WriteAllText(Path.Combine(_root, "config.ini"),
            $"[General]\nchannel={channel}\nsub_channel={sub}\ncps={cps}\ngame_biz={biz}\ngame_version=4.6.0\n");
    private GameUserState State(HoYoChannel channel = HoYoChannel.Official)
    {
        var state = new GameUserState { GameId = BuiltInGameIds.HonkaiStarRail, SelectedHoYoChannel = channel };
        HoYoInstallationIdentity.Restore(state);
        return state;
    }

    [Theory]
    [InlineData("1", "1", "hyp_mihoyo", "hkrpg_cn", HoYoChannel.Official)]
    [InlineData("1", "1", "mihoyo", "hkrpg_cn", HoYoChannel.Official)]
    [InlineData("14", "0", "bilibili", "hkrpg_cn", HoYoChannel.Bilibili)]
    [InlineData("1", "0", "hyp_mihoyo", "hkrpg_cn", HoYoChannel.Unknown)]
    [InlineData("14", "0", "hyp_mihoyo", "hkrpg_cn", HoYoChannel.Unknown)]
    [InlineData("1", "1", "hyp_mihoyo", "hkrpg_global", HoYoChannel.Unknown)]
    [InlineData("1", "1", "hyp_mihoyo", "hk4e_cn", HoYoChannel.Unknown)]
    [InlineData("1", "1", "hyp_mihoyo", "", HoYoChannel.Unknown)]
    [InlineData("", "1", "hyp_mihoyo", "hkrpg_cn", HoYoChannel.Unknown)]
    [InlineData("1", "1", "unknown", "hkrpg_cn", HoYoChannel.Unknown)]
    public void ChannelRequiresConsistentLocalIdentity(string channel, string sub, string cps, string biz, HoYoChannel expected)
    {
        Metadata(channel, sub, cps, biz);
        Assert.Equal(expected, InstalledServerDeclaration.ReadVerifiedHoYoChannel(_root, "StarRail.exe"));
    }

    [Fact]
    public void VerifiedScanBindsSelectedServerAndPreservesInstallationIdentityAndHistory()
    {
        Metadata();
        var state = State();
        var id = state.HoYoInstallation!.InstallationId;
        state.TotalPlaySeconds = 12345;
        var config = new LauncherConfiguration { Games = [state], HiddenGameIds = [BuiltInGameIds.GenshinImpact] };
        var first = HoYoInstallationDiscovery.BindVerified(config, state, Path.Combine(_root, "StarRail.exe"), HoYoChannel.Official);
        var second = HoYoInstallationDiscovery.BindVerified(config, state, Path.Combine(_root, "StarRail.exe"), HoYoChannel.Official);
        Assert.Same(first, second);
        Assert.Equal(id, first.InstallationId);
        Assert.Equal(_root, first.InstallRoot);
        Assert.Equal(Path.Combine(_root, "StarRail.exe"), state.ExecutablePath);
        Assert.Equal(state.ExecutablePath, state.HoYoInstallation!.ExecutablePath);
        Assert.True(first.ChannelConfirmed && first.OfficialChannelConfirmed);
        Assert.Equal("4.6.0", first.InstalledVersion);
        Assert.Single(config.LaunchProfiles);
        Assert.Equal(first.InstallationId, config.LaunchProfiles[0].HoYoInstallationId);
        Assert.Equal(12345, state.TotalPlaySeconds);
        Assert.Equal([BuiltInGameIds.GenshinImpact], config.HiddenGameIds);
        HoYoMaintenanceService.ValidateExistingRoot(_root, "StarRail.exe");
        InstalledServerDeclaration.ValidateHoYo(_root, "StarRail.exe", HoYoChannel.Official);
    }

    [Fact]
    public void MissingMetadataCannotDefaultToOfficial()
    {
        var state = State();
        var config = new LauncherConfiguration { Games = [state] };
        Assert.Throws<InvalidOperationException>(() => HoYoInstallationDiscovery.BindVerified(config, state,
            Path.Combine(_root, "StarRail.exe"), HoYoChannel.Official));
        Assert.Null(state.HoYoInstallation!.ExecutablePath);
        Assert.Null(state.ExecutablePath);
        Assert.Empty(config.LaunchProfiles);
    }

    [Fact]
    public void OtherChannelAndChannelSwitchNeverBindScanToCurrentServer()
    {
        Metadata("14", "0", "bilibili");
        var state = State();
        var config = new LauncherConfiguration { Games = [state] };
        Assert.Throws<InvalidOperationException>(() => HoYoInstallationDiscovery.BindVerified(config, state,
            Path.Combine(_root, "StarRail.exe"), HoYoChannel.Official));
        state.SelectedHoYoChannel = HoYoChannel.Bilibili;
        Assert.Throws<InvalidOperationException>(() => HoYoInstallationDiscovery.BindVerified(config, state,
            Path.Combine(_root, "StarRail.exe"), HoYoChannel.Official));
        Assert.Null(state.ExecutablePath);
        Assert.Empty(config.LaunchProfiles);
    }

    [Fact]
    public void OccupiedChannelDirectoryAndUnfinishedMaintenanceArePreserved()
    {
        Metadata();
        var state = State();
        state.HoYoInstallation!.InstallRoot = Path.Combine(_root, "other");
        var config = new LauncherConfiguration { Games = [state] };
        Assert.Throws<InvalidOperationException>(() => HoYoInstallationDiscovery.BindVerified(config, state,
            Path.Combine(_root, "StarRail.exe"), HoYoChannel.Official));
        Assert.Equal(Path.Combine(_root, "other"), state.HoYoInstallation.InstallRoot);
        state.HoYoInstallation.InstallRoot = null;
        File.WriteAllText(Path.Combine(_root, ".aster-hoyo-maintenance.json"), "{}");
        Assert.Throws<InvalidOperationException>(() => HoYoInstallationDiscovery.BindVerified(config, state,
            Path.Combine(_root, "StarRail.exe"), HoYoChannel.Official));
        Assert.Null(state.HoYoInstallation.ExecutablePath);
    }

    [Fact]
    public void DuplicateOrContradictoryHypMetadataCannotEnableMaintenance()
    {
        Metadata();
        File.AppendAllText(Path.Combine(_root, "config.ini"), "CHANNEL=14\n");
        Assert.Throws<InvalidDataException>(() => InstalledServerDeclaration.ReadVerifiedHoYoChannel(_root, "StarRail.exe"));
        Assert.Throws<InvalidOperationException>(() => HoYoMaintenanceService.ValidateExistingRoot(_root, "StarRail.exe"));
        Metadata(sub: "0");
        Assert.Throws<InvalidOperationException>(() => HoYoMaintenanceService.ValidateExistingRoot(_root, "StarRail.exe"));
        Assert.Throws<InvalidOperationException>(() => InstalledServerDeclaration.ValidateHoYo(_root, "StarRail.exe", HoYoChannel.Official));
    }

    [Fact]
    public void UnknownArchiveProfileReferencesMoveToVerifiedInstallation()
    {
        Metadata();
        var state = State();
        var unknown = HoYoInstallationIdentity.Get(state, HoYoChannel.Unknown);
        unknown.ExecutablePath = Path.Combine(_root, "StarRail.exe");
        unknown.InstallRoot = _root;
        var profile = new LaunchProfile { GameId = state.GameId, HoYoInstallationId = unknown.InstallationId };
        unknown.SelectedLaunchProfileId = profile.Id;
        var config = new LauncherConfiguration { Games = [state], LaunchProfiles = [profile] };
        var installation = HoYoInstallationDiscovery.BindVerified(config, state, unknown.ExecutablePath, HoYoChannel.Official);
        Assert.Equal(installation.InstallationId, profile.HoYoInstallationId);
        Assert.Equal(profile.Id, installation.SelectedLaunchProfileId);
        Assert.Null(unknown.ExecutablePath);
        Assert.Single(config.LaunchProfiles);
    }

    public void Dispose()
    {
        var fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Fixtures")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(fixtures, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture escaped test output.");
        Directory.Delete(_root, true);
    }
}