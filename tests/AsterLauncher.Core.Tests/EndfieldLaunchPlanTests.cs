using System.Text.Json;
using System.Text.Json.Serialization;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldLaunchPlanTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void LegacyInstallationDefaultsToOfficialTakeoverWithoutChangingGamePathOrHistory()
    {
        var configuration = JsonSerializer.Deserialize<LauncherConfiguration>("""
            {"endfieldInstallations":[{"channel":"Bilibili","executablePath":"E:\\Games\\Endfield.exe","installedVersion":"1.6.3"}],"games":[{"gameId":"endfield","totalPlaySeconds":12345}],"hiddenGameIds":["arknights"]}
            """, Json)!;
        var installation = Assert.Single(configuration.EndfieldInstallations);
        Assert.Equal(EndfieldLaunchMode.OfficialLauncher, installation.LaunchMode);
        Assert.Null(installation.LauncherExecutablePath);
        Assert.False(installation.LauncherAssociationConfirmed);
        Assert.Equal(180, installation.LauncherDetectionTimeoutSeconds);
        Assert.Equal(@"E:\Games\Endfield.exe", installation.ExecutablePath);
        Assert.Equal("1.6.3", installation.InstalledVersion);
        Assert.Equal(12345, Assert.Single(configuration.Games).TotalPlaySeconds);
        Assert.Equal("arknights", Assert.Single(configuration.HiddenGameIds));
    }

    [Theory]
    [InlineData("\"ExperimentalBridge\"", EndfieldLaunchMode.ExperimentalBridge)]
    [InlineData("\"experimentalbridge\"", EndfieldLaunchMode.ExperimentalBridge)]
    [InlineData("1", EndfieldLaunchMode.ExperimentalBridge)]
    [InlineData("\"OfficialLauncher\"", EndfieldLaunchMode.OfficialLauncher)]
    [InlineData("\"officiallauncher\"", EndfieldLaunchMode.OfficialLauncher)]
    [InlineData("0", EndfieldLaunchMode.OfficialLauncher)]
    public void RecognizedModesReadThroughPropertyConverter(string value, EndfieldLaunchMode expected)
    {
        var installation = JsonSerializer.Deserialize<EndfieldInstallation>("{\"launchMode\":" + value + "}", Json)!;
        Assert.Equal(expected, installation.LaunchMode);
    }

    [Theory]
    [InlineData("\"FutureMode\"")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("{\"mode\":\"ExperimentalBridge\",\"nested\":[1,2]}")]
    [InlineData("[\"ExperimentalBridge\"]")]
    [InlineData("\"OfficialLauncher, ExperimentalBridge\"")]
    [InlineData("\"1\"")]
    public void UnknownOrMalformedModeFallsBackWithoutDiscardingRemainingConfiguration(string value)
    {
        var configuration = JsonSerializer.Deserialize<LauncherConfiguration>(
            "{\"endfieldInstallations\":[{\"launchMode\":" + value + ",\"installedVersion\":\"kept\",\"launcherExecutablePath\":\"E:\\\\Official\\\\Games.exe\"}],"
            + "\"games\":[{\"gameId\":\"endfield\",\"totalPlaySeconds\":456}],\"hiddenGameIds\":[\"genshin-impact\"]}", Json)!;
        var installation = Assert.Single(configuration.EndfieldInstallations);
        Assert.Equal(EndfieldLaunchMode.OfficialLauncher, installation.LaunchMode);
        Assert.Equal("kept", installation.InstalledVersion);
        Assert.Equal(@"E:\Official\Games.exe", installation.LauncherExecutablePath);
        Assert.Equal(456, Assert.Single(configuration.Games).TotalPlaySeconds);
        Assert.Equal("genshin-impact", Assert.Single(configuration.HiddenGameIds));
    }

    [Fact]
    public void NewOptionsRoundTripIndependentlyForEachChannel()
    {
        var official = new EndfieldInstallation
        {
            Channel = EndfieldChannel.Official,
            ExecutablePath = @"E:\Games\Official\Endfield.exe",
            LaunchMode = EndfieldLaunchMode.OfficialLauncher,
            LauncherExecutablePath = @"E:\Launchers\Official\Games.exe",
            LauncherAssociationConfirmed = true,
            LauncherDetectionTimeoutSeconds = 60
        };
        var bilibili = new EndfieldInstallation
        {
            Channel = EndfieldChannel.Bilibili,
            ExecutablePath = @"E:\Games\Bilibili\Endfield.exe",
            LaunchMode = EndfieldLaunchMode.ExperimentalBridge,
            LauncherExecutablePath = @"E:\Launchers\Bilibili\Games.exe",
            LauncherAssociationConfirmed = true,
            LauncherDetectionTimeoutSeconds = 300
        };
        var configuration = new LauncherConfiguration { EndfieldInstallations = [official, bilibili] };
        var restored = JsonSerializer.Deserialize<LauncherConfiguration>(JsonSerializer.Serialize(configuration, Json), Json)!;
        Assert.Equal(JsonSerializer.Serialize(official, Json), JsonSerializer.Serialize(restored.EndfieldInstallations[0], Json));
        Assert.Equal(JsonSerializer.Serialize(bilibili, Json), JsonSerializer.Serialize(restored.EndfieldInstallations[1], Json));
    }

    [Fact]
    public void UnknownRuntimeModeSerializesAsSafeOfficialDefault()
    {
        var installation = new EndfieldInstallation { LaunchMode = (EndfieldLaunchMode)123 };
        var json = JsonSerializer.Serialize(installation, Json);
        Assert.Contains("\"launchMode\":\"OfficialLauncher\"", json);
        Assert.Equal(EndfieldLaunchMode.OfficialLauncher, JsonSerializer.Deserialize<EndfieldInstallation>(json, Json)!.LaunchMode);
    }

    [Fact]
    public void OfficialPlanDeepClonesProfileAndAddsOnlyTemporaryTakeover()
    {
        var profile = SampleProfile();
        var installation = new EndfieldInstallation
        {
            ExecutablePath = @"E:\Game\Endfield.exe",
            LauncherDetectionTimeoutSeconds = 180,
            LaunchMode = EndfieldLaunchMode.OfficialLauncher
        };
        var before = JsonSerializer.Serialize(profile, Json);
        var result = EndfieldLaunchPlan.Create(profile, installation, @"E:\Game\Endfield.exe", @"E:\Launcher\Games.exe");

        Assert.NotSame(profile, result.Profile);
        Assert.NotSame(profile.Steps, result.Profile.Steps);
        Assert.False(result.UsesCustomTakeover);
        Assert.NotNull(result.ManagedStepId);
        Assert.Equal(180, result.Profile.GameDetectionTimeoutSeconds);
        Assert.Equal(profile.Steps.Count + 1, result.Profile.Steps.Count);
        for (var index = 0; index < profile.Steps.Count; index++)
        {
            Assert.NotSame(profile.Steps[index], result.Profile.Steps[index]);
            Assert.Equal(JsonSerializer.Serialize(profile.Steps[index], Json), JsonSerializer.Serialize(result.Profile.Steps[index], Json));
        }
        Assert.Equal(profile.Id, result.Profile.Id);
        Assert.Equal(profile.GameId, result.Profile.GameId);
        Assert.Equal(profile.EndfieldInstallationId, result.Profile.EndfieldInstallationId);
        Assert.Equal(profile.HoYoInstallationId, result.Profile.HoYoInstallationId);
        Assert.Equal(profile.Name, result.Profile.Name);
        Assert.Equal(profile.IsDefault, result.Profile.IsDefault);
        Assert.Equal(profile.GameArguments, result.Profile.GameArguments);
        Assert.Equal(profile.GameWorkingDirectory, result.Profile.GameWorkingDirectory);

        var managed = Assert.Single(result.Profile.Steps, step => step.Id == result.ManagedStepId);
        Assert.Equal("官方启动器接管", managed.Name);
        Assert.Equal(@"E:\Launcher\Games.exe", managed.ExecutablePath);
        Assert.Equal(@"E:\Launcher", managed.WorkingDirectory);
        Assert.Equal(string.Empty, managed.Arguments);
        Assert.Equal(LaunchPhase.Game, managed.Phase);
        Assert.Equal(15, managed.TimeoutSeconds);
        Assert.Equal(LaunchFailurePolicy.Abort, managed.FailurePolicy);
        Assert.True(managed.IsEnabled);
        Assert.True(managed.TakesOverGameLaunch);
        Assert.False(managed.RunAsAdministrator);
        Assert.False(managed.SkipIfAlreadyRunning);
        Assert.False(managed.CloseOnGameExit);
        Assert.Equal(0, managed.DelayMilliseconds);
        Assert.Equal(before, JsonSerializer.Serialize(profile, Json));
        Assert.Equal(@"E:\Game\Endfield.exe", installation.ExecutablePath);

        result.Profile.Name = "temporary";
        result.Profile.GameArguments = "temporary arguments";
        result.Profile.Steps[0].Arguments = "temporary tool arguments";
        result.Profile.Steps.Clear();
        Assert.Equal(before, JsonSerializer.Serialize(profile, Json));
    }

    [Fact]
    public void ExperimentalPlanUsesRealGamePlaceholderWithoutForwardingArgumentsToParent()
    {
        var profile = SampleProfile();
        var installation = new EndfieldInstallation { LaunchMode = EndfieldLaunchMode.ExperimentalBridge };
        var result = EndfieldLaunchPlan.Create(profile, installation, @"E:\Game\Endfield.exe", @"E:\Launcher\Games.exe");
        var managed = Assert.Single(result.Profile.Steps, step => step.Id == result.ManagedStepId);
        Assert.Equal("实验性启动桥", managed.Name);
        Assert.Equal(@"E:\Game\Endfield.exe", managed.ExecutablePath);
        Assert.Equal(profile.GameWorkingDirectory, managed.WorkingDirectory);
        Assert.Equal(string.Empty, managed.Arguments);
        Assert.Equal(profile.GameArguments, result.Profile.GameArguments);
        Assert.Equal(LaunchPhase.Game, managed.Phase);
        Assert.True(managed.TakesOverGameLaunch);
        Assert.False(managed.SkipIfAlreadyRunning);
        Assert.False(managed.CloseOnGameExit);
        Assert.False(managed.RunAsAdministrator);
        Assert.Equal(15, managed.TimeoutSeconds);
        Assert.False(result.UsesCustomTakeover);
    }

    [Theory]
    [InlineData(LaunchPhase.BeforeGame)]
    [InlineData(LaunchPhase.Game)]
    [InlineData(LaunchPhase.AfterGame)]
    [InlineData(LaunchPhase.OnGameExit)]
    public void EnabledCustomTakeoverRemainsAuthoritativeInEveryExistingPhase(LaunchPhase phase)
    {
        var profile = SampleProfile();
        profile.Steps[0].Phase = phase;
        profile.Steps[0].TakesOverGameLaunch = true;
        var before = JsonSerializer.Serialize(profile, Json);
        var result = EndfieldLaunchPlan.Create(profile, new EndfieldInstallation(), string.Empty, string.Empty);
        Assert.True(result.UsesCustomTakeover);
        Assert.Null(result.ManagedStepId);
        Assert.Equal(profile.Steps.Count, result.Profile.Steps.Count);
        Assert.NotSame(profile.Steps[0], result.Profile.Steps[0]);
        Assert.Equal(before, JsonSerializer.Serialize(profile, Json));
        Assert.Equal(180, result.Profile.GameDetectionTimeoutSeconds);
    }

    [Fact]
    public void DisabledTakeoverDoesNotSuppressManagedStepOrLoseDisabledConfiguration()
    {
        var profile = SampleProfile();
        profile.Steps[0].TakesOverGameLaunch = true;
        profile.Steps[0].IsEnabled = false;
        var result = EndfieldLaunchPlan.Create(profile, new EndfieldInstallation(), @"E:\Game\Endfield.exe", @"E:\Launcher\Games.exe");
        Assert.False(result.UsesCustomTakeover);
        Assert.NotNull(result.ManagedStepId);
        Assert.False(result.Profile.Steps[0].IsEnabled);
        Assert.True(result.Profile.Steps[0].TakesOverGameLaunch);
        Assert.Equal(profile.Steps.Count + 1, result.Profile.Steps.Count);
    }

    [Theory]
    [InlineData(-1, 60)]
    [InlineData(0, 60)]
    [InlineData(60, 60)]
    [InlineData(180, 180)]
    [InlineData(300, 300)]
    [InlineData(301, 300)]
    [InlineData(int.MaxValue, 300)]
    public void DetectionTimeoutIsBoundedWithoutChangingSavedValues(int configured, int expected)
    {
        var profile = SampleProfile();
        var installation = new EndfieldInstallation { LauncherDetectionTimeoutSeconds = configured };
        var result = EndfieldLaunchPlan.Create(profile, installation, @"E:\Game\Endfield.exe", @"E:\Launcher\Games.exe");
        Assert.Equal(expected, result.Profile.GameDetectionTimeoutSeconds);
        Assert.Equal(configured, installation.LauncherDetectionTimeoutSeconds);
        Assert.Equal(45, profile.GameDetectionTimeoutSeconds);
    }

    [Fact]
    public void UnknownRuntimeModeBuildsOfficialTakeoverAndRepeatedPlansDoNotAccumulateSteps()
    {
        var profile = SampleProfile();
        var installation = new EndfieldInstallation { LaunchMode = (EndfieldLaunchMode)99 };
        var first = EndfieldLaunchPlan.Create(profile, installation, @"E:\Game\Endfield.exe", @"E:\Launcher\Games.exe");
        var second = EndfieldLaunchPlan.Create(profile, installation, @"E:\Game\Endfield.exe", @"E:\Launcher\Games.exe");
        var firstManaged = Assert.Single(first.Profile.Steps, step => step.Id == first.ManagedStepId);
        Assert.Equal(@"E:\Launcher\Games.exe", firstManaged.ExecutablePath);
        Assert.Equal(first.Profile.Steps.Count, second.Profile.Steps.Count);
        Assert.NotEqual(first.ManagedStepId, second.ManagedStepId);
        Assert.Equal(2, profile.Steps.Count);
    }

    private static LaunchProfile SampleProfile() => new()
    {
        GameId = BuiltInGameIds.Endfield,
        EndfieldInstallationId = Guid.NewGuid(),
        HoYoInstallationId = Guid.NewGuid(),
        Name = "我的终末地方案",
        IsDefault = false,
        GameArguments = "--language zh-cn --custom \"kept value\"",
        GameWorkingDirectory = @"E:\Game\WorkingDirectory",
        GameDetectionTimeoutSeconds = 45,
        Steps =
        [
            new LaunchStep
            {
                Name = "启动前伴随工具", ExecutablePath = @"E:\Tools\Before.exe", Arguments = "--before",
                WorkingDirectory = @"E:\Tools", Phase = LaunchPhase.BeforeGame, DelayMilliseconds = 200,
                RunAsAdministrator = true, SkipIfAlreadyRunning = false, TimeoutSeconds = 25,
                FailurePolicy = LaunchFailurePolicy.Ask, CloseOnGameExit = true
            },
            new LaunchStep
            {
                Name = "退出后任务", ExecutablePath = @"E:\Tools\After.exe", Arguments = "--after",
                Phase = LaunchPhase.OnGameExit, IsEnabled = false
            }
        ]
    };
}