using System.Text.Json;
using System.Text.Json.Serialization;
using AsterLauncher.Core;
using Microsoft.Extensions.Logging;

namespace AsterLauncher.Infrastructure;

public sealed class JsonConfigurationStore : IConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<JsonConfigurationStore> _logger;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public JsonConfigurationStore(ILogger<JsonConfigurationStore> logger)
    {
        _logger = logger;
        ConfigurationPath = Path.Combine(LauncherDataPaths.ResolveDataDirectory(), "launcher.settings.json");
    }

    public string ConfigurationPath { get; }

    public async Task<LauncherConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConfigurationPath))
        {
            var initial = CreateInitialConfiguration();
            await SaveAsync(initial, cancellationToken).ConfigureAwait(false);
            return initial;
        }

        try
        {
            await using var stream = File.OpenRead(ConfigurationPath);
            var configuration = await JsonSerializer.DeserializeAsync<LauncherConfiguration>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            return configuration ?? CreateInitialConfiguration();
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "Configuration JSON is invalid at {ConfigurationPath}", ConfigurationPath);
            return CreateInitialConfiguration();
        }
        catch (IOException exception)
        {
            _logger.LogError(exception, "Could not read configuration at {ConfigurationPath}", ConfigurationPath);
            return CreateInitialConfiguration();
        }
    }

    public async Task SaveAsync(LauncherConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(ConfigurationPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $"launcher.settings.{Guid.NewGuid():N}.tmp");

            try
            {
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(stream, configuration, SerializerOptions, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, ConfigurationPath, overwrite: true);
                _logger.LogInformation("Configuration saved to {ConfigurationPath}", ConfigurationPath);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }
    private static LauncherConfiguration CreateInitialConfiguration()
    {
        var profile = new LaunchProfile
        {
            GameId = BuiltInGameIds.Endfield,
            Name = "默认启动",
            IsDefault = true
        };

        return new LauncherConfiguration
        {
            FirstRunCompleted = false,
            SelectedGameId = BuiltInGameIds.Endfield,
            SelectedProfileId = profile.Id,
            Games =
            [
                new GameUserState
                {
                    GameId = BuiltInGameIds.Endfield
                }
            ],
            LaunchProfiles = [profile],
            CompanionTools = [.. CompanionToolPresetFactory.CreateDefaults()]
        };
    }
}
