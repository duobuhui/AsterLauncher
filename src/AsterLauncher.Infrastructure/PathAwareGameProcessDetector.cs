using System.Diagnostics;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

public sealed class PathAwareGameProcessDetector : IGameProcessDetector
{
    public Task<bool> IsRunningAsync(
        GameDefinition game,
        string? expectedExecutablePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var matches = FindMatchingProcesses(game, expectedExecutablePath);
        var isRunning = matches.Count > 0;
        foreach (var process in matches)
        {
            process.Dispose();
        }

        return Task.FromResult(isRunning);
    }

    public async Task<bool> WaitForStartAsync(
        GameDefinition game,
        string? expectedExecutablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsRunningAsync(game, expectedExecutablePath, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public async Task WaitForExitAsync(
        GameDefinition game,
        string? expectedExecutablePath,
        CancellationToken cancellationToken = default)
    {
        while (await IsRunningAsync(game, expectedExecutablePath, cancellationToken).ConfigureAwait(false))
        {
            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<Process> FindMatchingProcesses(GameDefinition game, string? expectedExecutablePath)
    {
        var matches = new List<Process>();
        var normalizedExpectedPath = NormalizePath(expectedExecutablePath);
        var candidateNames = game.ExecutableNames
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(expectedExecutablePath))
        {
            candidateNames.Add(Path.GetFileNameWithoutExtension(expectedExecutablePath));
        }

        foreach (var processName in candidateNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                if (normalizedExpectedPath is null)
                {
                    matches.Add(process);
                    continue;
                }

                try
                {
                    if (string.Equals(NormalizePath(process.MainModule?.FileName), normalizedExpectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(process);
                    }
                    else
                    {
                        process.Dispose();
                    }
                }
                catch
                {
                    process.Dispose();
                }
            }
        }

        return matches;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }
}
