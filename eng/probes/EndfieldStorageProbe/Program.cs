using System.Security.Cryptography;
using System.Text.Json;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

try {
var root = Path.GetFullPath(args.Single());
if (!root.StartsWith(@"E:\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("E isolation required");
Directory.CreateDirectory(root);
var a = Path.Combine(root, "official");
var b = Path.Combine(root, "bilibili");
const string relative = "Endfield_Data/StreamingAssets/VFS/0123ABCD/0123456789ABCDEF0123456789ABCDEF.chk";
var first = SafeGamePath.Resolve(a, relative);
var second = SafeGamePath.Resolve(b, relative);
Directory.CreateDirectory(Path.GetDirectoryName(first)!);
Directory.CreateDirectory(Path.GetDirectoryName(second)!);
var bytes = new byte[1024 * 1024 + 1];
RandomNumberGenerator.Fill(bytes);
string Hash(byte[] value) => Convert.ToHexString(MD5.HashData(value)).ToLowerInvariant();
var original = Hash(bytes);
await File.WriteAllBytesAsync(first, bytes);
await File.WriteAllBytesAsync(second, bytes);
await File.WriteAllTextAsync(Path.Combine(a, "config.ini"), "independent channel state");
await File.WriteAllTextAsync(Path.Combine(b, "config.ini"), "independent channel state");
var beforeA = WindowsHardLink.Identity(first);
var beforeB = WindowsHardLink.Identity(second);
var allocatedBefore = WindowsHardLink.EstimatedAllocatedBytes(first) + WindowsHardLink.EstimatedAllocatedBytes(second);
var files = new[] { new EndfieldManifestFile(relative, bytes.Length, original),
    new EndfieldManifestFile("config.ini", new FileInfo(Path.Combine(a,"config.ini")).Length,
        Hash(await File.ReadAllBytesAsync(Path.Combine(a,"config.ini")))) };
var sharing = new EndfieldSharingService();
var summary = await sharing.OptimizeAsync(a, b, files, files);
var linkedA = WindowsHardLink.Identity(first);
var linkedB = WindowsHardLink.Identity(second);
var allocatedShared = WindowsHardLink.EstimatedAllocatedBytes(first);
if (beforeA.SameFile(beforeB) || !linkedA.SameFile(linkedB) || linkedA.LinkCount != 2 || summary.FileCount != 1)
    throw new InvalidDataException("Actual NTFS link identity mismatch");
if (WindowsHardLink.Identity(Path.Combine(a,"config.ini")).SameFile(WindowsHardLink.Identity(Path.Combine(b,"config.ini"))))
    throw new InvalidDataException("Mutable state was shared");
bytes[0] ^= 0xff;
var independent = Path.Combine(root, "replacement");
await File.WriteAllBytesAsync(independent, bytes);
await VerifiedFileCommit.CommitAsync(independent, first, new(relative, bytes.Length, Hash(bytes)));
var otherPreserved = Hash(await File.ReadAllBytesAsync(second)) == original;
var detached = !WindowsHardLink.Identity(first).SameFile(WindowsHardLink.Identity(second));
File.Delete(first);
var otherReadableAfterDelete = Hash(await File.ReadAllBytesAsync(second)) == original;
if (!otherPreserved || !detached || !otherReadableAfterDelete) throw new InvalidDataException("Channel isolation failed");
var evidence = new {
    volume = new DriveInfo(@"E:\").DriveFormat,
    beforeA, beforeB, linkedA, linkedB,
    summary.FileCount, summary.SharedBytes, summary.EstimatedSavedBytes,
    allocatedBefore, allocatedShared, measuredSavedBytes = allocatedBefore - allocatedShared,
    mutableFilesIndependent = true, otherPreserved, detached, otherReadableAfterDelete,
    scope = "isolated generated resource; not a full game or runtime immutability test"
};
var path = Path.Combine(root, "hardlink-evidence.json");
await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(await File.ReadAllTextAsync(path));

} catch (Exception error) { Console.Error.WriteLine(error.Message); Environment.ExitCode = 1; }
