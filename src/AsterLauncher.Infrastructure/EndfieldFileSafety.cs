using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Buffers;
using System.Diagnostics;
using AsterLauncher.Core;

namespace AsterLauncher.Infrastructure;

public static class SafeGamePath
{
    public static void ValidateRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Contains(':') || relative.Contains('\0'))
            throw new InvalidDataException("Unsafe game file path.");
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".."
            || part.EndsWith('.') || part.EndsWith(' ')
            || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || IsDeviceName(part)))
            throw new InvalidDataException("Unsafe game file path.");
    }

    public static string Resolve(string root, string relative)
    {
        ValidateRelative(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Game file escaped its install root.");
        RejectReparsePoints(fullRoot, full);
        return full;
    }

    private static void RejectReparsePoints(string root, string full)
    {
        var current = Path.GetPathRoot(root)!;
        var rootRelative = Path.GetRelativePath(current, root.TrimEnd(Path.DirectorySeparatorChar));
        foreach (var part in rootRelative.Split(Path.DirectorySeparatorChar))
        {
            if (part == ".") continue;
            current = Path.Combine(current, part);
            if (HasReparsePoint(current)) throw new InvalidDataException("Install root crosses a reparse point.");
        }
        var relative = Path.GetRelativePath(current, full);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (HasReparsePoint(current)) throw new InvalidDataException("Game path crosses a reparse point.");
        }
    }

    private static bool HasReparsePoint(string path) =>
        (File.Exists(path) || Directory.Exists(path))
        && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsDeviceName(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT"))
                && stem[3] is >= '1' and <= '9');
    }
}

public sealed class VerifiedFileCommit
{
    public static async Task<bool> MatchesAsync(string path, EndfieldManifestFile file,
        CancellationToken cancellationToken = default, IProgress<long>? byteProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (byteProgress is null)
        {
            var hash = await MD5.HashDataAsync(stream, cancellationToken);
            return hash.AsSpan().SequenceEqual(Convert.FromHexString(file.Md5));
        }
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long processed = 0, lastReport = 0;
        byte[] actual;
        try
        {
            byteProgress.Report(0);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(0, 128 * 1024), cancellationToken);
                if (read == 0) break;
                digest.AppendData(buffer, 0, read);
                processed += read;
                var now = Stopwatch.GetTimestamp();
                if (lastReport == 0 || now - lastReport >= Stopwatch.Frequency / 10)
                {
                    byteProgress.Report(processed);
                    lastReport = now;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            actual = digest.GetHashAndReset();
            byteProgress.Report(processed);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return actual.AsSpan().SequenceEqual(Convert.FromHexString(file.Md5));
    }

    private sealed class CommitByteProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    public static async Task CommitAsync(string verifiedSource, string destination,
        EndfieldManifestFile file, CancellationToken cancellationToken = default, IProgress<long>? byteProgress = null)
    {
        IProgress<long>? Phase(int phase) => byteProgress is null ? null
            : new CommitByteProgress(value => byteProgress.Report(file.Size / 3 * phase + value / 3));
        if (!await MatchesAsync(verifiedSource, file, cancellationToken, Phase(0)))
            throw new InvalidDataException("Staged game file failed integrity verification.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".aster-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var source = new FileStream(verifiedSource, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (byteProgress is null) await source.CopyToAsync(target, cancellationToken);
                else
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                    var copyProgress = Phase(1)!;
                    long copied = 0, lastReport = 0;
                    try
                    {
                        int read;
                        while ((read = await source.ReadAsync(buffer.AsMemory(0, 128 * 1024), cancellationToken)) > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            copied += read;
                            var now = Stopwatch.GetTimestamp();
                            if (lastReport == 0 || now - lastReport >= Stopwatch.Frequency / 10)
                            {
                                copyProgress.Report(copied);
                                lastReport = now;
                            }
                        }
                        copyProgress.Report(copied);
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                }
                await target.FlushAsync(cancellationToken);
                target.Flush(true);
            }
            if (!await MatchesAsync(temporary, file, cancellationToken, Phase(2)))
                throw new InvalidDataException("Temporary game file failed integrity verification.");
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public readonly record struct WindowsFileIdentity(uint VolumeSerial, uint IndexHigh, uint IndexLow, uint LinkCount)
{
    public bool SameFile(WindowsFileIdentity other) =>
        VolumeSerial == other.VolumeSerial && IndexHigh == other.IndexHigh && IndexLow == other.IndexLow;
}

public static class WindowsHardLink
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string linkName, string existingFileName, nint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetCompressedFileSize(string path, out uint high);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileStandardInformation information, uint size);
    public static long EstimatedAllocatedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (GetFileInformationByHandleEx(stream.SafeFileHandle, 1, out var standard,
                (uint)Marshal.SizeOf<FileStandardInformation>())) return standard.AllocationSize;
        var low = GetCompressedFileSize(path, out var high);
        if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0)
            return new FileInfo(path).Length;
        return ((long)high << 32) | low;
    }
    public static WindowsFileIdentity Identity(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            throw new IOException("Could not read Windows file identity.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        return new WindowsFileIdentity(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow, info.NumberOfLinks);
    }

    [DllImport("kernel32.dll", EntryPoint="CreateFileW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeFileHandle OpenForDelete(string path,uint access,uint share,nint security,uint disposition,uint flags,nint template);
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle,int informationClass,ref FileDispositionInformation info,uint size);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation { public byte DeleteFile; }
    public static bool DeleteVerifiedEntry(string path,WindowsFileIdentity expected,long size,DateTime lastWriteUtc)
    {
        // DELETE + GENERIC_READ, share READ only: deny writes and renames until unlink is committed.
        using var handle=OpenForDelete(path,0x80010000,1,0,3,0x00200000,0);
        if(handle.IsInvalid)throw new IOException("File is busy or cannot be deleted.",Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        if(!GetFileInformationByHandle(handle,out var info))throw new IOException("Cannot verify cleanup file identity.");
        if((info.FileAttributes&(uint)FileAttributes.ReparsePoint)!=0)return false;
        var identity=new WindowsFileIdentity(info.VolumeSerialNumber,info.FileIndexHigh,info.FileIndexLow,info.NumberOfLinks);
        var length=((long)info.FileSizeHigh<<32)|info.FileSizeLow;
        var timestamp=DateTime.FromFileTimeUtc(((long)(uint)info.LastWriteTime.dwHighDateTime<<32)|(uint)info.LastWriteTime.dwLowDateTime);
        if(!identity.SameFile(expected)||length!=size||timestamp!=lastWriteUtc)return false;
        var disposition=new FileDispositionInformation{DeleteFile=1};
        if(!SetFileInformationByHandle(handle,4,ref disposition,(uint)Marshal.SizeOf<FileDispositionInformation>()))
            throw new IOException("Cannot remove selected directory entry.",Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        return true;
    }
    public static bool TryCreate(string linkName, string existingFileName)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return CreateHardLink(linkName, existingFileName, 0);
    }

    public static bool CanShareVolume(string first, string second)
    {
        var firstRoot = Path.GetPathRoot(Path.GetFullPath(first));
        var secondRoot = Path.GetPathRoot(Path.GetFullPath(second));
        if (!string.Equals(firstRoot, secondRoot, StringComparison.OrdinalIgnoreCase)) return false;
        try { return string.Equals(new DriveInfo(firstRoot!).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}

public class ResourceSharingService
{
    private readonly Action _commitGuard;
    private readonly Func<string, bool> _isShareable;
    public ResourceSharingService(Func<string, bool> isShareable, Action commitGuard) { _isShareable = isShareable; _commitGuard = commitGuard; }
    public async Task<EndfieldSharingSummary> OptimizeAsync(
        string sourceRoot, string targetRoot,
        IReadOnlyList<EndfieldManifestFile> sourceManifest,
        IReadOnlyList<EndfieldManifestFile> targetManifest,
        CancellationToken cancellationToken = default, IProgress<long>? byteProgress = null)
    {
        if (Path.GetFullPath(sourceRoot).Equals(Path.GetFullPath(targetRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Channels require independent install directories.");
        if (!WindowsHardLink.CanShareVolume(sourceRoot, targetRoot))
            return new EndfieldSharingSummary(0, 0, 0);

        var sourceMap = sourceManifest.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var count = 0;
        long bytes = 0;
        long saved = 0;
        foreach (var target in targetManifest.Where(item => _isShareable(item.Path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sourceMap.TryGetValue(target.Path, out var source)
                || source.Size != target.Size || !source.Md5.Equals(target.Md5, StringComparison.OrdinalIgnoreCase))
                continue;
            var sourcePath = SafeGamePath.Resolve(sourceRoot, source.Path);
            var targetPath = SafeGamePath.Resolve(targetRoot, target.Path);
            // Report logical progress through the four verification passes for the current file.
            IProgress<long>? Phase(int phase) => byteProgress is null ? null
                : new SharingByteProgress(value => byteProgress.Report(target.Size / 4 * phase + value / 4));
            if (!await VerifiedFileCommit.MatchesAsync(sourcePath, source, cancellationToken, Phase(0))) continue;
            if (File.Exists(targetPath) && !await VerifiedFileCommit.MatchesAsync(targetPath, target, cancellationToken, Phase(1))) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var temporary = targetPath + ".aster-link-" + Guid.NewGuid().ToString("N");
            try
            {
                // Keep the source open without write sharing until the new directory entry is committed.
                using var held = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!await VerifiedFileCommit.MatchesAsync(sourcePath, source, cancellationToken, Phase(2))) continue;
                var sourceId = WindowsHardLink.Identity(sourcePath);
                if (File.Exists(targetPath) && sourceId.SameFile(WindowsHardLink.Identity(targetPath)))
                {
                    count++;
                    bytes += target.Size;
                    saved += WindowsHardLink.EstimatedAllocatedBytes(targetPath);
                    continue;
                }
                if (!WindowsHardLink.TryCreate(temporary, sourcePath)) continue;
                if (!sourceId.SameFile(WindowsHardLink.Identity(temporary)))
                    throw new IOException("Hard-link identity check failed.");
                _commitGuard();
                File.Move(temporary, targetPath, overwrite: true);
                if (!sourceId.SameFile(WindowsHardLink.Identity(targetPath))
                    || !await VerifiedFileCommit.MatchesAsync(targetPath, target, cancellationToken, Phase(3)))
                    throw new IOException("Committed hard-link verification failed.");
                count++;
                bytes += target.Size;
                saved += WindowsHardLink.EstimatedAllocatedBytes(targetPath);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        return new EndfieldSharingSummary(count, bytes, saved);
    }

    private sealed class SharingByteProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    public async Task<EndfieldSharingSummary> ScanAsync(
        string firstRoot, string secondRoot, IReadOnlyList<EndfieldManifestFile> candidates,
        CancellationToken cancellationToken = default)
    {
        var count = 0;
        long bytes = 0;
        long saved = 0;
        foreach (var file in candidates.Where(item => _isShareable(item.Path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = SafeGamePath.Resolve(firstRoot, file.Path);
            var second = SafeGamePath.Resolve(secondRoot, file.Path);
            if (!File.Exists(first) || !File.Exists(second)) continue;
            if (!await VerifiedFileCommit.MatchesAsync(first, file, cancellationToken)) continue;
            if (!WindowsHardLink.Identity(first).SameFile(WindowsHardLink.Identity(second))) continue;
            count++;
            bytes += file.Size;
            saved += WindowsHardLink.EstimatedAllocatedBytes(first);
        }
        return new EndfieldSharingSummary(count, bytes, saved);
    }

    public EndfieldSharingSummary ScanLocal(
        string firstRoot, string secondRoot, CancellationToken cancellationToken = default)
    {
        var count = 0;
        long bytes = 0;
        long saved = 0;
        if (!Directory.Exists(firstRoot) || !Directory.Exists(secondRoot))
            return new EndfieldSharingSummary(0, 0, 0);
        foreach (var first in EnumerateGameFiles(firstRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(firstRoot, first).Replace('\\', '/');
            if (!_isShareable(relative)) continue;
            var second = SafeGamePath.Resolve(secondRoot, relative);
            if (!File.Exists(second)) continue;
            var firstId = WindowsHardLink.Identity(first);
            if (!firstId.SameFile(WindowsHardLink.Identity(second))) continue;
            count++;
            bytes += new FileInfo(first).Length;
            saved += WindowsHardLink.EstimatedAllocatedBytes(first);
        }
        return new EndfieldSharingSummary(count, bytes, saved);
    }

    public async Task<int> UnshareAsync(string root, IReadOnlyList<EndfieldManifestFile> manifest,
        CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var path in EnumerateGameFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WindowsHardLink.Identity(path).LinkCount <= 1) continue;
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var size = new FileInfo(path).Length;
            string hash;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                hash = Convert.ToHexString(await MD5.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            var current = new EndfieldManifestFile(relative, size, hash);
            _commitGuard();
            await VerifiedFileCommit.CommitAsync(path, path, current, cancellationToken);
            count++;
        }
        return count;
    }

    private static IEnumerable<string> EnumerateGameFiles(string root)
    {
        SafeGamePath.Resolve(root, "identity-check");
        if (!Directory.Exists(root)) yield break;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            var relative = Path.GetRelativePath(root, path);
            yield return SafeGamePath.Resolve(root, relative);
        }
    }
}

public sealed class EndfieldSharingService : ResourceSharingService
{
    public EndfieldSharingService(Action? commitGuard = null) : base(IsShareable, commitGuard ?? EndfieldMaintenanceService.EnsureNotRunning) { }
    // The inspected Endfield VFS stores large resources under hex-named .chk paths.
    // Index files, .blc metadata, executables, configuration and user state stay independent.
    public static bool IsShareable(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        return parts.Length == 5
            && parts[0].Equals("Endfield_Data", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("StreamingAssets", StringComparison.OrdinalIgnoreCase)
            && parts[2].Equals("VFS", StringComparison.OrdinalIgnoreCase)
            && parts[3].Length == 8 && parts[3].All(Uri.IsHexDigit)
            && parts[4].EndsWith(".chk", StringComparison.OrdinalIgnoreCase)
            && parts[4].Length == 36
            && parts[4][..32].All(Uri.IsHexDigit);
    }
}
