using AsterLauncher.Core;
using Microsoft.Win32;
using System.Security;
using System.Text.RegularExpressions;

namespace AsterLauncher.Infrastructure;

public sealed record EndfieldLauncherRegistration(string Source, string? DisplayName, string? InstallRoot,
    string? ExecutablePath, EndfieldChannel Channel = EndfieldChannel.Unknown);

public interface IEndfieldLauncherEvidenceSource
{
    IReadOnlyList<EndfieldLauncherRegistration> GetRegistrations();
}

public sealed record EndfieldLauncherCandidate(string ExecutablePath, EndfieldChannel Channel, string Source,
    EndfieldLauncherTrustResult Trust, bool IsManualSelection, string? BridgeParentPath = null)
{
    public bool CanUseAsBridgeParent => Trust.CanUseAsBridgeParent;
}

/// <summary>Bounded launcher discovery. Neither directory names nor a publisher imply an installation channel.</summary>
public sealed class EndfieldLauncherDiscovery
{
    private readonly IEndfieldLauncherEvidenceSource _evidence;
    private readonly EndfieldLauncherTrust _trust;
    public EndfieldLauncherDiscovery(IEndfieldLauncherEvidenceSource? evidence = null, EndfieldLauncherTrust? trust = null)
    {
        _evidence = evidence ?? new WindowsEndfieldLauncherEvidenceSource();
        _trust = trust ?? new EndfieldLauncherTrust();
    }

    /// <param name="manuallySelectedExecutable">A path explicitly associated by the user with requestedChannel. Arbitrary local EXEs remain handoff-only unless verified as a bridge parent.</param>
    public Task<IReadOnlyList<EndfieldLauncherCandidate>> DiscoverAsync(EndfieldChannel requestedChannel,
        string? manuallySelectedExecutable = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var result = new List<EndfieldLauncherCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (EndfieldLauncherTrust.TryNormalizeExecutable(manuallySelectedExecutable, out var manual) && File.Exists(manual))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trust = _trust.Verify(manual);
            var parent = FindBridgeParent(manual, cancellationToken);
            result.Add(new(manual, IsKnownChannel(requestedChannel) ? requestedChannel : EndfieldChannel.Unknown,
                "手动选择（用户确认渠道）", trust, true, parent?.ExecutablePath));
            seen.Add(manual);
        }
        foreach (var registration in _evidence.GetRegistrations().Take(256))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Opposite-channel evidence must not be silently adopted for the selected installation.
            if (IsKnownChannel(requestedChannel) && IsKnownChannel(registration.Channel) && requestedChannel != registration.Channel) continue;
            var paths = EnumerateExecutables(registration.InstallRoot, registration.ExecutablePath).ToArray();
            var checkedPaths = paths.Select(path => (Path: path, Trust: _trust.Verify(path))).ToArray();
            var parentPath = checkedPaths.FirstOrDefault(item => item.Trust.CanUseAsBridgeParent).Path;
            foreach (var item in checkedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(item.Path)) continue;
                result.Add(new(item.Path, IsKnownChannel(registration.Channel) ? registration.Channel : EndfieldChannel.Unknown,
                    registration.Source, item.Trust, false, parentPath));
            }
        }
        return (IReadOnlyList<EndfieldLauncherCandidate>)result;
    }, cancellationToken);

    /// <summary>Checks only the selected launcher directory and numeric version subdirectories, without recursion.</summary>
    public Task<EndfieldLauncherCandidate?> ResolveBridgeParentAsync(string rootLauncherPath,
        CancellationToken cancellationToken = default) => Task.Run(() => FindBridgeParent(rootLauncherPath, cancellationToken), cancellationToken);

    private EndfieldLauncherCandidate? FindBridgeParent(string rootLauncherPath, CancellationToken cancellationToken)
    {
        if (!EndfieldLauncherTrust.TryNormalizeExecutable(rootLauncherPath, out var path) || !File.Exists(path)) return null;
        foreach (var candidate in EnumerateExecutables(Path.GetDirectoryName(path), path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(Path.GetFileName(candidate), "Games.exe", StringComparison.OrdinalIgnoreCase)) continue;
            var trust = _trust.Verify(candidate);
            if (trust.CanUseAsBridgeParent) return new(candidate, EndfieldChannel.Unknown, "所选启动器的已验证签名组件", trust, false, candidate);
        }
        return null;
    }

    internal static bool IsKnownChannel(EndfieldChannel channel) => channel is EndfieldChannel.Official or EndfieldChannel.Bilibili;

    internal static IEnumerable<string> EnumerateExecutables(string? root, string? executable)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value)
        {
            if (EndfieldLauncherTrust.TryNormalizeExecutable(value, out var path) && File.Exists(path)
                && IsLauncherName(Path.GetFileName(path)) && seen.Add(path)) result.Add(path);
        }
        Add(executable);
        if (string.IsNullOrWhiteSpace(root) && EndfieldLauncherTrust.TryNormalizeExecutable(executable, out var supplied))
            root = Path.GetDirectoryName(supplied);
        if (!TryNormalizeDirectory(root, out var directory)) return result;
        Add(Path.Combine(directory, "Launcher.exe"));
        Add(Path.Combine(directory, "Games.exe"));
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return result;
            var versions = Directory.EnumerateDirectories(directory).Take(128)
                .Where(child => Version.TryParse(Path.GetFileName(child), out _))
                .Select(child => (Path: child, Version: Version.Parse(Path.GetFileName(child))))
                .OrderByDescending(child => child.Version);
            foreach (var version in versions)
            {
                if ((File.GetAttributes(version.Path) & FileAttributes.ReparsePoint) != 0) continue;
                Add(Path.Combine(version.Path, "Games.exe"));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException) { }
        return result;
    }

    private static bool TryNormalizeDirectory(string? value, out string directory)
    {
        directory = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(value)) return false;
            var path = Path.GetFullPath(value);
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length < 3 || path[1] != ':' || path[2..].Contains(':')) return false;
            directory = path;
            return Directory.Exists(directory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    internal static bool IsLauncherName(string? name) => string.Equals(name, "Launcher.exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Games.exe", StringComparison.OrdinalIgnoreCase);
}

public sealed class WindowsEndfieldLauncherEvidenceSource : IEndfieldLauncherEvidenceSource
{
    public IReadOnlyList<EndfieldLauncherRegistration> GetRegistrations()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var result = new List<EndfieldLauncherRegistration>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                ReadUninstall(baseKey, hive, view, result);
                ReadProtocols(baseKey, hive, view, result);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException) { }
        }
        return result;
    }

    private static void ReadUninstall(RegistryKey baseKey, RegistryHive hive, RegistryView view, List<EndfieldLauncherRegistration> result)
    {
        using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        if (uninstall is null) return;
        foreach (var keyName in uninstall.GetSubKeyNames().Take(4096))
        {
            try
            {
                using var key = uninstall.OpenSubKey(keyName);
                var name = key?.GetValue("DisplayName") as string;
                if (!IsRelatedName(name)) continue;
                var root = Expand(key?.GetValue("InstallLocation") as string)?.Trim().Trim('"');
                var executable = ReadCommandExecutable(key?.GetValue("DisplayIcon") as string);
                // Registry labels identify a channel only when they explicitly say so. Generic GRYPHLINK is unknown.
                result.Add(new($"{name} 卸载信息 ({hive}/{view})", name, root, executable, ReadExplicitChannel(name)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException) { }
            if (result.Count >= 256) break;
        }
    }

    private static void ReadProtocols(RegistryKey baseKey, RegistryHive hive, RegistryView view, List<EndfieldLauncherRegistration> result)
    {
        using var classes = baseKey.OpenSubKey(@"SOFTWARE\Classes");
        if (classes is null) return;
        foreach (var name in classes.GetSubKeyNames().Take(16384))
        {
            if (!name.StartsWith("hypergryphlauncher-", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("gryphlink", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("endfield", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var protocol = classes.OpenSubKey(name);
                if (protocol?.GetValue("URL Protocol") is not string) continue;
                using var command = protocol.OpenSubKey(@"shell\open\command");
                var path = ReadCommandExecutable(command?.GetValue(null) as string);
                if (path is null) continue;
                result.Add(new($"{name} 注册协议 ({hive}/{view})", name, Path.GetDirectoryName(path), path, ReadExplicitChannel(name)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException) { }
            if (result.Count >= 256) break;
        }
    }

    internal static bool IsRelatedName(string? name) => name is not null
        && (name.Equals("鹰角启动器", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GRYPHLINK", StringComparison.OrdinalIgnoreCase)
            || name.Contains("终末地", StringComparison.Ordinal)
            || name.Contains("Endfield", StringComparison.OrdinalIgnoreCase));

    internal static EndfieldChannel ReadExplicitChannel(string? name)
    {
        if (name is null || !(name.Contains("终末地", StringComparison.Ordinal)
            || name.Contains("Endfield", StringComparison.OrdinalIgnoreCase))) return EndfieldChannel.Unknown;
        if (name.Contains("bilibili", StringComparison.OrdinalIgnoreCase) || name.Contains("哔哩哔哩", StringComparison.Ordinal)
            || name.Contains("B服", StringComparison.OrdinalIgnoreCase)) return EndfieldChannel.Bilibili;
        if (name.Contains("官服", StringComparison.Ordinal)) return EndfieldChannel.Official;
        return EndfieldChannel.Unknown;
    }

    internal static string? ReadCommandExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 4096 || command.Any(char.IsControl)) return null;
        var text = Expand(command)?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        string path;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) return null;
            path = text[1..end];
        }
        else
        {
            var match = Regex.Match(text, @"^(?<path>[A-Za-z]:\\[^""\r\n]*?\.exe)(?:\s|,|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return null;
            path = match.Groups["path"].Value;
        }
        return EndfieldLauncherTrust.TryNormalizeExecutable(path, out var normalized) && EndfieldLauncherDiscovery.IsLauncherName(Path.GetFileName(normalized))
            ? normalized : null;
    }

    private static string? Expand(string? value) => string.IsNullOrWhiteSpace(value) ? null
        : Environment.ExpandEnvironmentVariables(value);
}
