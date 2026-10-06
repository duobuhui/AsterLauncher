namespace AsterLauncher.Core;
public static class LauncherAppearance
{
    public static void Normalize(LauncherConfiguration config)
    {
        config.AccentPreference ??= config.ThemePreference switch
        {
            LauncherThemePreference.TyphonPurple => LauncherAccentPreference.Purple,
            LauncherThemePreference.ElysiaPink => LauncherAccentPreference.Pink,
            LauncherThemePreference.PaimonWhite => LauncherAccentPreference.Cream,
            _ => LauncherAccentPreference.Default
        };
        if (!Enum.IsDefined(config.AccentPreference.Value)) config.AccentPreference = LauncherAccentPreference.Default;
        if (config.ThemePreference is LauncherThemePreference.TyphonPurple or LauncherThemePreference.ElysiaPink or LauncherThemePreference.PaimonWhite)
            config.ThemePreference = LauncherThemePreference.Dark;
        if (!Enum.IsDefined(config.ThemePreference)) config.ThemePreference = LauncherThemePreference.System;
    }
}
public static class GameLibraryFilter
{
    public static bool Matches(string name, string publisher, bool installed, bool running,
        string search, string selectedPublisher, bool installedOnly, bool runningOnly) =>
        (!installedOnly || installed) && (!runningOnly || running)
        && (selectedPublisher == "全部发行商" || publisher == selectedPublisher)
        && (string.IsNullOrWhiteSpace(search) || name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
            || publisher.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
}
