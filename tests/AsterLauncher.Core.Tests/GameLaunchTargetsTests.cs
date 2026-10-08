using System.Text.Json;
using System.Text.Json.Serialization;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsterLauncher.Core.Tests;

public sealed class GameLaunchTargetsTests
{
    [Theory]
    [InlineData(BuiltInGameIds.Endfield, true, false)]
    [InlineData(BuiltInGameIds.GenshinImpact, true, false)]
    [InlineData(BuiltInGameIds.HonkaiStarRail, true, false)]
    [InlineData(BuiltInGameIds.ZenlessZoneZero, true, false)]
    [InlineData(BuiltInGameIds.Arknights, false, true)]
    [InlineData(BuiltInGameIds.HonkaiImpact3rd, false, false)]
    [InlineData(BuiltInGameIds.PetitPlanet, false, false)]
    [InlineData("custom-game", false, false)]
    [InlineData(null, false, false)]
    public void CapabilitiesAndNormalizationStaySeparateFromLocalChannels(string? gameId, bool cloud, bool emulator)
    {
        Assert.Equal(cloud, GameLaunchTargets.SupportsOfficialCloud(gameId));
        Assert.Equal(emulator, GameLaunchTargets.SupportsEmulator(gameId));
        Assert.Equal(GameLaunchTarget.Local, GameLaunchTargets.Normalize(gameId, GameLaunchTarget.Local));
        Assert.Equal(cloud ? GameLaunchTarget.OfficialCloud : GameLaunchTarget.Local,
            GameLaunchTargets.Normalize(gameId, GameLaunchTarget.OfficialCloud));
        Assert.Equal(emulator ? GameLaunchTarget.Emulator : GameLaunchTarget.Local,
            GameLaunchTargets.Normalize(gameId, GameLaunchTarget.Emulator));
        Assert.Equal(GameLaunchTarget.Local, GameLaunchTargets.Normalize(gameId, (GameLaunchTarget)99));
    }

    [Theory]
    [InlineData("\"OfficialCloud\"", GameLaunchTarget.OfficialCloud)]
    [InlineData("\"officialcloud\"", GameLaunchTarget.OfficialCloud)]
    [InlineData("1", GameLaunchTarget.OfficialCloud)]
    [InlineData("\"Emulator\"", GameLaunchTarget.Emulator)]
    [InlineData("2", GameLaunchTarget.Emulator)]
    [InlineData("\"Local\"", GameLaunchTarget.Local)]
    [InlineData("0", GameLaunchTarget.Local)]
    public void KnownValuesRemainReadableWithGeneralStringEnumConverter(string json, GameLaunchTarget expected)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
        var state = JsonSerializer.Deserialize<GameUserState>("{\"launchTarget\":" + json + "}", options)!;
        Assert.Equal(expected, state.LaunchTarget);
    }
}

[Collection("Environment variables")]
public sealed class GameLaunchTargetConfigurationTests : IDisposable
{
    private static readonly string FixtureDirectory = Path.Combine(
        Environment.GetEnvironmentVariable("TEMP") ?? AppContext.BaseDirectory, "AsterLauncher.Tests", "launch-targets");
    private readonly string _root = Path.Combine(FixtureDirectory, Guid.NewGuid().ToString("N"));
    private readonly string? _originalDataHome = Environment.GetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME");
    private readonly JsonConfigurationStore _store;

    public GameLaunchTargetConfigurationTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _root);
        _store = new JsonConfigurationStore(NullLogger<JsonConfigurationStore>.Instance);
    }

    [Fact]
    public async Task CloudRoundTripKeepsLocalChannelsInstallationsProfilesAndPlayHistory()
    {
        var hoyo = new HoYoInstallation
        {
            Channel = HoYoChannel.Bilibili,
            ChannelConfirmed = true,
            InstallRoot = @"E:\Games\GenshinBili",
            ExecutablePath = @"E:\Games\GenshinBili\YuanShen.exe",
            InstalledVersion = "test-version",
            MaintenanceInProgress = true
        };
        var endfield = new EndfieldInstallation
        {
            Channel = EndfieldChannel.Bilibili,
            InstallRoot = @"E:\Games\EndfieldBili",
            ExecutablePath = @"E:\Games\EndfieldBili\Endfield.exe",
            InstalledVersion = "test-version",
            PreloadTargetVersion = "future-version"
        };
        var hoyoProfile = new LaunchProfile
        {
            GameId = BuiltInGameIds.GenshinImpact,
            HoYoInstallationId = hoyo.InstallationId,
            GameArguments = "--user-setting",
            Steps = [new LaunchStep { Name = "existing-tool", ExecutablePath = @"E:\Tools\tool.exe" }]
        };
        var endfieldProfile = new LaunchProfile
        {
            GameId = BuiltInGameIds.Endfield,
            EndfieldInstallationId = endfield.InstallationId
        };
        hoyo.SelectedLaunchProfileId = hoyoProfile.Id;
        endfield.SelectedLaunchProfileId = endfieldProfile.Id;
        var playedAt = DateTimeOffset.Parse("2026-10-08T12:00:00+08:00");
        var configuration = new LauncherConfiguration
        {
            SelectedGameId = BuiltInGameIds.GenshinImpact,
            SelectedEndfieldChannel = EndfieldChannel.Bilibili,
            SelectedProfileId = hoyoProfile.Id,
            EndfieldInstallations = [endfield],
            LaunchProfiles = [hoyoProfile, endfieldProfile],
            Games =
            [
                new GameUserState
                {
                    GameId = BuiltInGameIds.GenshinImpact,
                    ExecutablePath = hoyo.ExecutablePath,
                    SelectedHoYoChannel = HoYoChannel.Bilibili,
                    HoYoInstallations = [hoyo],
                    HoYoInstallation = hoyo,
                    LaunchTarget = GameLaunchTarget.OfficialCloud,
                    LastPlayedAt = playedAt,
                    TotalPlaySeconds = 120,
                    PlaySessions = [new GamePlaySession { StartedAt = playedAt, DurationSeconds = 120 }]
                },
                new GameUserState
                {
                    GameId = BuiltInGameIds.Endfield,
                    ExecutablePath = endfield.ExecutablePath,
                    LaunchTarget = GameLaunchTarget.OfficialCloud
                }
            ]
        };

        await _store.SaveAsync(configuration);
        var restored = await _store.LoadAsync();
        Assert.All(restored.Games, state => Assert.Equal(GameLaunchTarget.OfficialCloud, state.LaunchTarget));
        Assert.Equal(HoYoChannel.Bilibili, restored.Games[0].SelectedHoYoChannel);
        Assert.Equal(EndfieldChannel.Bilibili, restored.SelectedEndfieldChannel);
        Assert.Equal(hoyoProfile.Id, restored.SelectedProfileId);
        Assert.Equal(JsonSerializer.Serialize(hoyo), JsonSerializer.Serialize(Assert.Single(restored.Games[0].HoYoInstallations)));
        Assert.Equal(JsonSerializer.Serialize(hoyo), JsonSerializer.Serialize(restored.Games[0].HoYoInstallation));
        Assert.Equal(JsonSerializer.Serialize(endfield), JsonSerializer.Serialize(Assert.Single(restored.EndfieldInstallations)));
        Assert.Equal(JsonSerializer.Serialize(configuration.LaunchProfiles), JsonSerializer.Serialize(restored.LaunchProfiles));
        Assert.Equal(playedAt, restored.Games[0].LastPlayedAt);
        Assert.Equal(120, restored.Games[0].TotalPlaySeconds);
        Assert.Equal(120, Assert.Single(restored.Games[0].PlaySessions).DurationSeconds);

        foreach (var game in restored.Games) game.LaunchTarget = GameLaunchTarget.Local;
        await _store.SaveAsync(restored);
        var local = await _store.LoadAsync();
        Assert.All(local.Games, state => Assert.Equal(GameLaunchTarget.Local, state.LaunchTarget));
        Assert.Equal(hoyo.ExecutablePath, local.Games[0].ExecutablePath);
        Assert.Equal(endfield.ExecutablePath, local.Games[1].ExecutablePath);
        Assert.Equal(JsonSerializer.Serialize(restored.LaunchProfiles), JsonSerializer.Serialize(local.LaunchProfiles));
    }

    [Fact]
    public async Task EmulatorRoundTripKeepsItsSeparatePathEvenWhenDriveIsUnavailable()
    {
        var state = new GameUserState
        {
            GameId = BuiltInGameIds.Arknights,
            LaunchTarget = GameLaunchTarget.Emulator,
            ExecutablePath = @"E:\Existing\Arknights.exe",
            EmulatorExecutablePath = @"Z:\Emulator\Emulator.exe"
        };
        await _store.SaveAsync(new LauncherConfiguration { Games = [state] });
        var restored = Assert.Single((await _store.LoadAsync()).Games);
        Assert.Equal(GameLaunchTarget.Emulator, restored.LaunchTarget);
        Assert.Equal(state.ExecutablePath, restored.ExecutablePath);
        Assert.Equal(state.EmulatorExecutablePath, restored.EmulatorExecutablePath);
    }

    [Fact]
    public async Task LegacyConfigurationDefaultsToLocalWithoutRewritingOtherValues()
    {
        await File.WriteAllTextAsync(_store.ConfigurationPath, """
            {"selectedGameId":"genshin-impact","games":[{"gameId":"genshin-impact","executablePath":"E:\\Games\\YuanShen.exe","selectedHoYoChannel":"Bilibili","totalPlaySeconds":1234}]}
            """);
        var configuration = await _store.LoadAsync();
        var state = Assert.Single(configuration.Games);
        Assert.Equal(GameLaunchTarget.Local, state.LaunchTarget);
        Assert.Null(state.EmulatorExecutablePath);
        Assert.Equal(BuiltInGameIds.GenshinImpact, configuration.SelectedGameId);
        Assert.Equal(@"E:\Games\YuanShen.exe", state.ExecutablePath);
        Assert.Equal(HoYoChannel.Bilibili, state.SelectedHoYoChannel);
        Assert.Equal(1234, state.TotalPlaySeconds);
    }

    [Theory]
    [InlineData("\"FutureMode\"")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[\"OfficialCloud\"]")]
    [InlineData("\"Local, Emulator\"")]
    public async Task UnknownOrMalformedModeFallsBackLocallyWithoutDiscardingConfiguration(string targetJson)
    {
        await File.WriteAllTextAsync(_store.ConfigurationPath, $$"""
            {"selectedGameId":"arknights","games":[{"gameId":"arknights","launchTarget":{{targetJson}},"displayNameOverride":"kept","emulatorExecutablePath":"Z:\\Emulator\\Emulator.exe","totalPlaySeconds":4321}],"launchProfiles":[{"gameId":"arknights","name":"kept-profile"}]}
            """);
        var configuration = await _store.LoadAsync();
        var state = Assert.Single(configuration.Games);
        Assert.Equal(BuiltInGameIds.Arknights, configuration.SelectedGameId);
        Assert.Equal(GameLaunchTarget.Local, state.LaunchTarget);
        Assert.Equal("kept", state.DisplayNameOverride);
        Assert.Equal(@"Z:\Emulator\Emulator.exe", state.EmulatorExecutablePath);
        Assert.Equal(4321, state.TotalPlaySeconds);
        Assert.Equal("kept-profile", Assert.Single(configuration.LaunchProfiles).Name);
    }

    [Theory]
    [InlineData(BuiltInGameIds.Arknights, GameLaunchTarget.OfficialCloud)]
    [InlineData(BuiltInGameIds.Endfield, GameLaunchTarget.Emulator)]
    [InlineData(BuiltInGameIds.HonkaiImpact3rd, GameLaunchTarget.OfficialCloud)]
    [InlineData("custom-game", GameLaunchTarget.Emulator)]
    [InlineData(BuiltInGameIds.Endfield, (GameLaunchTarget)99)]
    public async Task UnsupportedModeIsNormalizedBeforeSavingWhilePathsRemain(string gameId, GameLaunchTarget target)
    {
        var state = new GameUserState
        {
            GameId = gameId,
            LaunchTarget = target,
            ExecutablePath = @"E:\Existing\Game.exe",
            EmulatorExecutablePath = @"Z:\Emulator\Emulator.exe"
        };
        await _store.SaveAsync(new LauncherConfiguration { Games = [state] });
        var restored = Assert.Single((await _store.LoadAsync()).Games);
        Assert.Equal(GameLaunchTarget.Local, state.LaunchTarget);
        Assert.Equal(GameLaunchTarget.Local, restored.LaunchTarget);
        Assert.Equal(state.ExecutablePath, restored.ExecutablePath);
        Assert.Equal(state.EmulatorExecutablePath, restored.EmulatorExecutablePath);
    }

    [Theory]
    [InlineData(BuiltInGameIds.Arknights, "OfficialCloud")]
    [InlineData(BuiltInGameIds.Endfield, "Emulator")]
    public async Task UnsupportedModeIsAlsoNormalizedWhenLoading(string gameId, string target)
    {
        await File.WriteAllTextAsync(_store.ConfigurationPath, $$"""
            {"games":[{"gameId":"{{gameId}}","launchTarget":"{{target}}","emulatorExecutablePath":"Z:\\Emulator\\Emulator.exe"}]}
            """);
        var restored = Assert.Single((await _store.LoadAsync()).Games);
        Assert.Equal(GameLaunchTarget.Local, restored.LaunchTarget);
        Assert.Equal(@"Z:\Emulator\Emulator.exe", restored.EmulatorExecutablePath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASTERLAUNCHER_DATA_HOME", _originalDataHome);
        var resolved = Path.GetFullPath(_root);
        if (!resolved.StartsWith(Path.GetFullPath(FixtureDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test fixture escaped its workspace directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
