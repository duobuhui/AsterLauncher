using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

public static class PlaytimeTargetCatalog
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>> NativeExecutables = BuiltInGameCatalog.CreateAdapters()
        .ToDictionary(adapter => adapter.Definition.Id, adapter => adapter.Definition.ExecutableNames.ToHashSet(StringComparer.OrdinalIgnoreCase));
    public static IReadOnlyList<PlaytimeTarget> Build(LauncherConfiguration configuration)
    {
        var targets = new List<PlaytimeTarget>();
        void Add(string gameId, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
                && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var full = Path.GetFullPath(path);
                    if (!NativeExecutables.TryGetValue(gameId, out var names) || names.Contains(Path.GetFileName(full)))
                        targets.Add(new PlaytimeTarget(gameId, full));
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        foreach (var game in configuration.Games)
        {
            // An emulator's lifetime is not the mobile game's lifetime. Cloud browsers are never process targets.
            if (game.LaunchTarget == GameLaunchTarget.Emulator) continue;
            Add(game.GameId, game.ExecutablePath);
            Add(game.GameId, game.HoYoInstallation?.ExecutablePath);
            foreach (var installation in game.HoYoInstallations) Add(game.GameId, installation.ExecutablePath);
        }
        foreach (var installation in configuration.EndfieldInstallations)
            Add(BuiltInGameIds.Endfield, installation.ExecutablePath);
        return targets.DistinctBy(target => target.GameId.ToLowerInvariant() + "\n" + target.ExecutablePath.ToLowerInvariant()).ToArray();
    }
}