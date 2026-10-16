using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
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
                    if (ReadExecutablePath(process.Id) is { } actualPath
                        && PlaytimeTrackingService.SameExecutablePath(normalizedExpectedPath, actualPath))
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

    private static string? ReadExecutablePath(int processId)
    {
        // Module enumeration asks for VM_READ, which a normal launcher cannot obtain for an
        // elevated game. Query only the image path while keeping full-path identity checks.
        using var handle = OpenProcess(0x1000, false, processId);
        if (handle.IsInvalid) return null;
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        return QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);

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
