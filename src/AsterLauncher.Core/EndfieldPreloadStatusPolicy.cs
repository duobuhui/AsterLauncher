namespace AsterLauncher.Core;

public static class EndfieldPreloadStatusPolicy
{
    // This is a saved completion receipt, not proof that the cache still exists.
    // The maintenance service must validate every object again before use.
    public static bool HasCompletedRecord(EndfieldInstallation installation) =>
        installation.PreloadState == EndfieldPreloadState.Completed
        && !string.IsNullOrWhiteSpace(installation.PreloadSourceVersion)
        && !string.IsNullOrWhiteSpace(installation.PreloadTargetVersion)
        && !string.IsNullOrWhiteSpace(installation.PreloadContentHash);

    public static bool IsCompletedOffer(EndfieldInstallation installation, EndfieldPatch? offer) =>
        offer is not null && HasCompletedRecord(installation)
        && string.Equals(installation.InstalledVersion, installation.PreloadSourceVersion,
            StringComparison.Ordinal)
        && string.Equals(offer.TargetVersion, installation.PreloadTargetVersion,
            StringComparison.Ordinal)
        && string.Equals(ContentIdentity(offer), installation.PreloadContentHash,
            StringComparison.OrdinalIgnoreCase);

    public static void ApplyCheck(EndfieldInstallation installation, EndfieldPatch? offer)
    {
        // Closing or replacing an offer does not remove downloaded cache objects.
        // Keep its identity until a new preload starts or an update consumes it.
        if (!HasCompletedRecord(installation))
            installation.PreloadState = offer is null
                ? EndfieldPreloadState.NotOpen : EndfieldPreloadState.Available;
    }

    public static void ApplyCheckFailure(EndfieldInstallation installation)
    {
        if (!HasCompletedRecord(installation))
            installation.PreloadState = EndfieldPreloadState.CheckFailed;
    }

    private static string ContentIdentity(EndfieldPatch patch) => patch.ContentMd5
        ?? string.Join("-", patch.Packs.Select(pack => pack.Md5));
}