namespace AsterLauncher.Infrastructure;

/// <summary>Removes only empty directory entries inside an explicit owner boundary.</summary>
public static class EmptyDirectoryCleanup
{
    public static int Prune(string root, Func<string, bool>? preserve = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root)) return 0;
        root = Path.GetFullPath(root);
        var pending = new Stack<(string Path, bool Visited)>();
        pending.Push((root, false));
        var deleted = 0;
        while (pending.TryPop(out var entry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (entry.Path.Equals(root, StringComparison.OrdinalIgnoreCase))
                    SafeGamePath.Resolve(root, "empty-directory-check");
                else
                    SafeGamePath.Resolve(root, Path.GetRelativePath(root, entry.Path));
                if (!Directory.Exists(entry.Path)) continue;
                if (!entry.Visited)
                {
                    pending.Push((entry.Path, true));
                    foreach (var child in Directory.EnumerateDirectories(entry.Path))
                        pending.Push((child, false));
                    continue;
                }
                if (preserve?.Invoke(entry.Path) == true || Directory.EnumerateFileSystemEntries(entry.Path).Any())
                    continue;
                // Non-recursive removal fails if another writer creates an entry.
                Directory.Delete(entry.Path, recursive: false);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        return deleted;
    }
}