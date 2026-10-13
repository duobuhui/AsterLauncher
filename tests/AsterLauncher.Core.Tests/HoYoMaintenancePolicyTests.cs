using AsterLauncher.Core;

namespace AsterLauncher.Core.Tests;

public sealed class HoYoMaintenancePolicyTests
{
    [Theory]
    [InlineData(null, "9.1.0", HoYoVersionRelation.Unknown)]
    [InlineData("", "9.1.0", HoYoVersionRelation.Unknown)]
    [InlineData("未安装 / 未确认", "9.1.0", HoYoVersionRelation.Unknown)]
    [InlineData("9.1.0", null, HoYoVersionRelation.Unknown)]
    [InlineData("9.1.0", "检查失败", HoYoVersionRelation.Unknown)]
    [InlineData("9.1.0-beta", "9.1.0", HoYoVersionRelation.Unknown)]
    [InlineData("9.1.0", "9.1.0", HoYoVersionRelation.Same)]
    [InlineData("9.1", "9.1.0", HoYoVersionRelation.Same)]
    [InlineData("9.1.0.0", "9.1.0", HoYoVersionRelation.Same)]
    [InlineData(" 9.1.0 ", "9.1.0", HoYoVersionRelation.Same)]
    [InlineData("9.0.0", "9.1.0", HoYoVersionRelation.RemoteNewer)]
    [InlineData("9.9.0", "9.10.0", HoYoVersionRelation.RemoteNewer)]
    [InlineData("9.1.0", "9.1.0.1", HoYoVersionRelation.RemoteNewer)]
    [InlineData("9.2.0", "9.1.0", HoYoVersionRelation.LocalNewer)]
    [InlineData("10.0.0", "9.10.0", HoYoVersionRelation.LocalNewer)]
    public void OnlyAnActuallyNewerKnownRemoteVersionRequiresUpdate(string? local, string? remote, HoYoVersionRelation expected)
    {
        Assert.Equal(expected, HoYoMaintenancePolicy.CompareVersions(local, remote));
    }

    [Theory]
    [InlineData(true, true, false, false, null, "9.1.0", false)]
    [InlineData(true, true, false, false, "9.1.0", null, false)]
    [InlineData(true, true, false, false, "9.1.0", "9.1.0", false)]
    [InlineData(true, true, false, false, "9.2.0", "9.1.0", false)]
    [InlineData(true, true, false, false, "9.0.0", "9.1.0", true)]
    [InlineData(true, false, false, false, null, "9.1.0", false)]
    [InlineData(true, false, false, false, "9.0.0", "9.1.0", false)]
    [InlineData(true, false, true, true, "9.0.0", "9.1.0", false)]
    [InlineData(true, true, true, false, null, null, true)]
    [InlineData(true, true, false, true, null, null, true)]
    [InlineData(true, true, true, false, "9.1.0", "9.1.0", true)]
    [InlineData(false, false, false, false, null, "9.1.0", true)]
    [InlineData(false, true, true, false, "9.0.0", null, true)]
    public void UnboundLocalGameNeverBecomesAnImplicitReinstallAndExplicitBoundTasksRemainAvailable(
        bool localGame, bool bound, bool pending, bool audio, string? local, string? remote, bool expected)
    {
        Assert.Equal(expected, HoYoMaintenancePolicy.ShouldStartMaintenance(localGame, bound, pending, audio, local, remote));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(@"D:\Games\NewOfficial\BH3.exe", @"D:\Games\NewOfficial", false)]
    [InlineData(@"D:\Games\bh3_cn\BH3.exe", null, false)]
    [InlineData(null, @"D:\Games\bh3_cn", false)]
    [InlineData(@"D:\Games\bh3_cn\BH3.exe", @"D:\Games\NewOfficial", false)]
    [InlineData(@"D:\Games\bh3_cn\BH3.exe", @"D:\Games\bh3_cn", true)]
    [InlineData(@"d:\games\BH3_CN\bh3.exe", @"D:\Games\bh3_cn\", true)]
    public void ExistingExecutableMustMatchBothMaintenanceEntryAndDirectory(string? maintenanceExecutable, string? root, bool expected)
    {
        var installation = new HoYoInstallation { ExecutablePath = maintenanceExecutable, InstallRoot = root };
        Assert.Equal(expected, HoYoMaintenancePolicy.IsBoundToExecutable(@"D:\Games\bh3_cn\BH3.exe", installation));
    }

    [Theory]
    [InlineData(@"D:\Games\bh3_cn", true)]
    [InlineData(@"d:\games\BH3_CN\", true)]
    [InlineData(@"D:\Games\崩坏3\Official", false)]
    [InlineData(@"D:\Games", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("relative-directory", false)]
    public void ExplicitDirectoryCorrectionMustUseTheExistingGameDirectory(string? root, bool expected)
    {
        Assert.Equal(expected, HoYoMaintenancePolicy.IsExistingDirectory(@"D:\Games\bh3_cn\BH3.exe", root));
    }
}