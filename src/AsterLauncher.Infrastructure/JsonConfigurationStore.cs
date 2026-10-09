using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AsterLauncher.Core;
using Microsoft.Extensions.Logging;

namespace AsterLauncher.Infrastructure;

public sealed class ConfigurationReadException : InvalidOperationException
{
    public ConfigurationReadException(string configurationPath, string backupPath)
        : base(File.Exists(backupPath)
            ? "无法安全读取启动器配置，已停止保存以保护原有记录。请检查 launcher.settings.json；同目录存在一份 launcher.settings.json.bak，可检查后用于恢复，再重新打开启动器。"
            : "无法安全读取启动器配置，已停止保存以保护原有记录。请检查数据目录中的 launcher.settings.json，再重新打开启动器；未找到自动备份。")
    {
        ConfigurationPath = configurationPath;
        BackupPath = backupPath;
        BackupExists = File.Exists(backupPath);
    }

    public string ConfigurationPath { get; }
    public string BackupPath { get; }
    public bool BackupExists { get; }
}

public sealed class ConfigurationRecoveryException : InvalidOperationException
{
    internal ConfigurationRecoveryException(string message) : base(message) { }
}

public sealed class ConfigurationBackupPreview
{
    internal ConfigurationBackupPreview(string sourcePath, string backupFingerprint, string? currentFingerprint,
        DateTimeOffset lastWrittenAt, LauncherConfiguration configuration)
    {
        SourcePath = sourcePath;
        BackupFingerprint = backupFingerprint;
        CurrentFingerprint = currentFingerprint;
        LastWrittenAt = lastWrittenAt;
        GameCount = configuration.Games.Count;
        HiddenGameCount = configuration.HiddenGameIds.Count;
        TotalPlaySeconds = configuration.Games.Sum(game => Math.Max(0, game.TotalPlaySeconds));
        PlaySessionCount = configuration.Games.Sum(game => game.PlaySessions?.Count ?? 0);
        FirstRunCompleted = configuration.FirstRunCompleted;
    }

    internal string SourcePath { get; }
    internal string BackupFingerprint { get; }
    internal string? CurrentFingerprint { get; }
    public DateTimeOffset LastWrittenAt { get; }
    public int GameCount { get; }
    public int HiddenGameCount { get; }
    public double TotalPlaySeconds { get; }
    public int PlaySessionCount { get; }
    public bool? FirstRunCompleted { get; }
    public bool HasCurrentConfiguration => CurrentFingerprint is not null;
}
public sealed class JsonConfigurationStore : IConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<JsonConfigurationStore> _logger;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _writesBlocked;
    private bool _hasReadConfiguration;

    public JsonConfigurationStore(ILogger<JsonConfigurationStore> logger)
    {
        _logger = logger;
        ConfigurationPath = Path.Combine(LauncherDataPaths.ResolveDataDirectory(), "launcher.settings.json");
    }

    public string ConfigurationPath { get; }
    public string BackupPath => ConfigurationPath + ".bak";
    public string RecoveryOriginalPath => Path.Combine(Path.GetDirectoryName(ConfigurationPath)!, "launcher.settings.recovery-original.json");

    public async Task<ConfigurationBackupPreview?> ReadBackupAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RejectRecoveryLink(BackupPath);
            await using var stream = new FileStream(BackupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = await ReadBytesAsync(stream, cancellationToken).ConfigureAwait(false);
            var configuration = DeserializeBackup(bytes);
            var currentFingerprint = await ReadCurrentFingerprintAsync(cancellationToken).ConfigureAwait(false);
            return new ConfigurationBackupPreview(BackupPath, Fingerprint(bytes), currentFingerprint,
                new DateTimeOffset(File.GetLastWriteTimeUtc(BackupPath), TimeSpan.Zero), configuration);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            LogRecoveryFailure(exception);
            throw new ConfigurationRecoveryException("无法读取有效的配置备份，现有文件未被更改。请保留数据目录，稍后重试或联系项目支持。");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task<LauncherConfiguration> RestoreBackupAsync(
        ConfigurationBackupPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            if (!string.Equals(preview.SourcePath, BackupPath, StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationRecoveryException("备份所属的数据目录已变化，请重新检查备份后再确认恢复。");
            RejectRecoveryLink(BackupPath);
            RejectRecoveryLink(ConfigurationPath);
            RejectRecoveryLink(RecoveryOriginalPath);
            // Keep the validated backup open without sharing writes or deletion through commit.
            await using var stream = new FileStream(BackupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = await ReadBytesAsync(stream, cancellationToken).ConfigureAwait(false);
            var configuration = DeserializeBackup(bytes);
            if (!string.Equals(preview.BackupFingerprint, Fingerprint(bytes), StringComparison.Ordinal))
                throw new ConfigurationRecoveryException("备份内容已变化，请重新检查备份后再确认恢复。");

            temporaryPath = Path.Combine(Path.GetDirectoryName(ConfigurationPath)!,
                $"launcher.settings.recovery.{Guid.NewGuid():N}.tmp");
            await using (var temporary = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await temporary.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await temporary.FlushAsync(cancellationToken).ConfigureAwait(false);
                temporary.Flush(flushToDisk: true);
            }

            var currentFingerprint = await ReadCurrentFingerprintAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(preview.CurrentFingerprint, currentFingerprint, StringComparison.Ordinal))
                throw new ConfigurationRecoveryException("当前配置在检查备份后发生了变化，已停止恢复；请重新检查后再确认。");

            cancellationToken.ThrowIfCancellationRequested();
            if (currentFingerprint is null)
                File.Move(temporaryPath, ConfigurationPath, overwrite: false);
            else
                File.Replace(temporaryPath, ConfigurationPath, RecoveryOriginalPath);
            // A rejected or cancelled attempt leaves the previous write permission intact.
            // Block normal saves only once restoration has actually committed.
            _writesBlocked = true;
            // The broken main file is deliberately kept out of the last valid .bak.
            // The caller must restart or explicitly load successfully before normal saves resume.
            _hasReadConfiguration = true;
            LauncherAppearance.Normalize(configuration);
            NormalizeLaunchTargets(configuration);
            _logger.LogInformation("Configuration backup restored after explicit confirmation.");
            return configuration;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            LogRecoveryFailure(exception);
            throw new ConfigurationRecoveryException("未能恢复配置备份。请保留现有文件，关闭其他启动器实例后重试。");
        }
        finally
        {
            try
            {
                if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Configuration recovery temporary file cleanup failed. Failure kind: {FailureKind}",
                    exception.GetType().Name);
            }
            finally
            {
                _saveGate.Release();
            }
        }
    }

    private static LauncherConfiguration DeserializeBackup(byte[] bytes)
    {
        var configuration = JsonSerializer.Deserialize<LauncherConfiguration>(bytes, SerializerOptions);
        if (configuration is null) throw new JsonException("Configuration root must be an object.");
        ValidateCollections(configuration);
        return configuration;
    }

    private static async Task<byte[]> ReadBytesAsync(FileStream stream, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return memory.ToArray();
    }

    private static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private async Task<string?> ReadCurrentFingerprintAsync(CancellationToken cancellationToken)
    {
        try
        {
            RejectRecoveryLink(ConfigurationPath);
            await using var stream = new FileStream(ConfigurationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void RejectRecoveryLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new ConfigurationRecoveryException("配置恢复不支持链接文件，请检查数据目录中的配置与备份位置。");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private void LogRecoveryFailure(Exception exception) =>
        _logger.LogError("Configuration recovery could not proceed. Failure kind: {FailureKind}",
            exception.GetType().Name);
    public async Task<LauncherConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (configuration is null)
            {
                EnsureInitialCreationAllowed();
                configuration = CreateInitialConfiguration();
                LauncherAppearance.Normalize(configuration);
                await SaveCoreAsync(configuration, cancellationToken).ConfigureAwait(false);
            }

            LauncherAppearance.Normalize(configuration);
            NormalizeLaunchTargets(configuration);
            // Recovery is explicit: retry LoadAsync after repairing/restoring the file.
            // SaveAsync alone must never unlock a store whose read has failed.
            _writesBlocked = false;
            _hasReadConfiguration = true;
            return configuration;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task SaveAsync(LauncherConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writesBlocked) throw CreateReadException();
            await SaveCoreAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task SaveCoreAsync(LauncherConfiguration configuration, CancellationToken cancellationToken)
    {
        // Revalidate before replacement, including saves made before the first load.
        // A damaged file must neither be overwritten nor replace the last valid backup.
        var previous = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (previous is null) EnsureInitialCreationAllowed();
        NormalizeLaunchTargets(configuration);
        ValidateCollections(configuration);

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
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (previous is null)
                File.Move(temporaryPath, ConfigurationPath, overwrite: false);
            else
                File.Replace(temporaryPath, ConfigurationPath, BackupPath);

            _hasReadConfiguration = true;
            _logger.LogInformation("Configuration saved to {ConfigurationPath}", ConfigurationPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private async Task<LauncherConfiguration?> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(ConfigurationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var configuration = await JsonSerializer.DeserializeAsync<LauncherConfiguration>(
                stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            if (configuration is null) throw new JsonException("Configuration root must be an object.");
            ValidateCollections(configuration);
            return configuration;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _writesBlocked = true;
            // JSON exception messages can contain values; never log the original exception.
            _logger.LogError("Configuration could not be read; writes are blocked. Failure kind: {FailureKind}",
                exception.GetType().Name);
            throw CreateReadException();
        }
    }

    private void EnsureInitialCreationAllowed()
    {
        if (!_writesBlocked && !_hasReadConfiguration && !File.Exists(BackupPath)) return;
        _writesBlocked = true;
        throw CreateReadException();
    }

    private ConfigurationReadException CreateReadException() => new(ConfigurationPath, BackupPath);

    private static void ValidateCollections(LauncherConfiguration configuration)
    {
        if (configuration.Games is null || configuration.LaunchProfiles is null
            || configuration.CompanionTools is null || configuration.EndfieldInstallations is null
            || configuration.HiddenGameIds is null || configuration.GameOrder is null
            || configuration.Games.Any(game => game is null)
            || configuration.LaunchProfiles.Any(profile => profile is null)
            || configuration.CompanionTools.Any(tool => tool is null)
            || configuration.EndfieldInstallations.Any(installation => installation is null))
            throw new JsonException("Configuration collections must not be null.");
    }

    private static void NormalizeLaunchTargets(LauncherConfiguration configuration)
    {
        foreach (var game in configuration.Games ?? [])
        {
            if (game is not null)
                game.LaunchTarget = GameLaunchTargets.Normalize(game.GameId, game.LaunchTarget);
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