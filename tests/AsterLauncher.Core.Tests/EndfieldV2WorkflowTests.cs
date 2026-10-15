using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldV2WorkflowTests
{
    [Fact]
    public async Task CachedPreloadAppliesRootFullVfsBlcAndNativeDeltaWithoutFullDownloadsOrPeerMutation()
    {
        using var fixture = await Fixture.CreateAsync(Failure.None);
        await fixture.PreloadAndOfferFormalAsync();

        await fixture.Service.SyncAsync(fixture.Installation, null, false);

        Assert.Equal(1, fixture.ArchiveRequests);
        Assert.Equal(1, fixture.MetadataRequests);
        Assert.Equal(0, fixture.FullResourceRequests);
        Assert.Equal("1.6.3", fixture.Installation.InstalledVersion);
        Assert.False(File.Exists(fixture.Journal));
        Assert.Equal(fixture.Executable, await File.ReadAllBytesAsync(fixture.GamePath("Endfield.exe")));
        Assert.Equal(fixture.FullChunk, await File.ReadAllBytesAsync(fixture.GamePath(Fixture.FullPath)));
        Assert.Equal(fixture.Blc, await File.ReadAllBytesAsync(fixture.GamePath(Fixture.BlcPath)));
        Assert.Equal(fixture.NewNative, await File.ReadAllBytesAsync(fixture.GamePath(Fixture.NativePath)));
        Assert.Equal(fixture.OriginalNative, await File.ReadAllBytesAsync(fixture.Peer));
        Assert.False(WindowsHardLink.Identity(fixture.Peer).SameFile(
            WindowsHardLink.Identity(fixture.GamePath(Fixture.NativePath))));
        Assert.Equal("personal note", await File.ReadAllTextAsync(fixture.GamePath("personal-note.txt")));
    }

    [Fact]
    public async Task CompleteTargetCacheBypassesUnavailableNativeSourceAndAllPatchTraffic()
    {
        using var fixture = await Fixture.CreateAsync(Failure.None);
        fixture.OfferFormalWithoutPreload();
        await fixture.CacheAllCompleteTargetsAsync();
        File.Delete(fixture.GamePath(Fixture.NativePath));

        await fixture.Service.SyncAsync(fixture.Installation, null, false);

        Assert.Equal(0, fixture.ArchiveRequests);
        Assert.Equal(0, fixture.MetadataRequests);
        Assert.Equal(0, fixture.FullResourceRequests);
        Assert.Equal("1.6.3", fixture.Installation.InstalledVersion);
        Assert.Equal(fixture.NewNative, await File.ReadAllBytesAsync(fixture.GamePath(Fixture.NativePath)));
        Assert.Equal(fixture.Executable, await File.ReadAllBytesAsync(fixture.GamePath("Endfield.exe")));
        Assert.Equal(fixture.OriginalNative, await File.ReadAllBytesAsync(fixture.Peer));
        Assert.False(File.Exists(fixture.Journal));
    }

    [Fact]
    public async Task OneSmallDefectCanBeExtractedFromZipWithUnselectedEntryLargerThanSixtyFourMiB()
    {
        using var fixture = await Fixture.CreateAsync(Failure.None, includeLargeUnusedEntry: true);
        await fixture.InstallCompleteTargetsExceptAsync("Endfield.exe");
        await fixture.PreloadAndOfferFormalAsync();
        Assert.InRange(fixture.Zip.Length, 1, 256 * 1024); // 65 MiB zero entry remains compressed in memory/cache.

        await fixture.Service.SyncAsync(fixture.Installation, null, false);

        Assert.Equal("1.6.3", fixture.Installation.InstalledVersion);
        Assert.Equal(1, fixture.ArchiveRequests);
        Assert.Equal(1, fixture.MetadataRequests);
        Assert.Equal(0, fixture.FullResourceRequests);
        Assert.Equal(fixture.Executable, await File.ReadAllBytesAsync(fixture.GamePath("Endfield.exe")));
        Assert.Equal(fixture.OriginalNative, await File.ReadAllBytesAsync(fixture.Peer));
        Assert.False(Directory.Exists(fixture.GamePath("unrelated")));
        Assert.False(File.Exists(fixture.Journal));
    }

    [Fact]
    public async Task WithdrawnFormalPatchAfterCompletedPreloadCannotSilentlyDownloadCompleteResources()
    {
        using var fixture = await Fixture.CreateAsync(Failure.None);
        await fixture.PreloadAndOfferFormalAsync();
        fixture.WithdrawFormalPatch();

        await Assert.ThrowsAsync<EndfieldIncrementalUpdateException>(() =>
            fixture.Service.SyncAsync(fixture.Installation, null, false));

        Assert.Equal(1, fixture.ArchiveRequests);
        Assert.Equal(0, fixture.MetadataRequests);
        Assert.Equal(0, fixture.FullResourceRequests);
        Assert.Equal("1.5.3", fixture.Installation.InstalledVersion);
        Assert.Equal(EndfieldPreloadState.Completed, fixture.Installation.PreloadState);
        Assert.Equal(fixture.OldExecutable, await File.ReadAllBytesAsync(fixture.GamePath("Endfield.exe")));
        Assert.Equal(fixture.OriginalNative, await File.ReadAllBytesAsync(fixture.GamePath(Fixture.NativePath)));
        Assert.Equal(fixture.Zip, await File.ReadAllBytesAsync(fixture.CachedArchive));
        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.Journal));
        Assert.False(journal.RootElement.GetProperty("Applying").GetBoolean());
        Assert.Contains(DownloadTaskCache.ContentKey(Md5(fixture.Zip), fixture.Zip.Length),
            await new DownloadTaskCache(fixture.Data).ReferencedAsync());
    }

    [Fact]
    public async Task CompleteLegacyZipStillAppliesSuccessfullyWithoutV2Metadata()
    {
        using var fixture = await Fixture.CreateAsync(Failure.None, legacyLayout: true);
        await fixture.PreloadAndOfferFormalAsync();
        fixture.OfferLegacyFormal();

        await fixture.Service.SyncAsync(fixture.Installation, null, false);

        Assert.Equal("1.6.3", fixture.Installation.InstalledVersion);
        Assert.Equal(1, fixture.ArchiveRequests);
        Assert.Equal(0, fixture.MetadataRequests);
        Assert.Equal(0, fixture.FullResourceRequests);
        Assert.Equal(fixture.NewNative, await File.ReadAllBytesAsync(fixture.GamePath(Fixture.NativePath)));
        Assert.Equal(fixture.OriginalNative, await File.ReadAllBytesAsync(fixture.Peer));
        Assert.False(File.Exists(fixture.Journal));
    }

    [Theory]
    [InlineData(Failure.InvalidArchive)]
    [InlineData(Failure.MissingNativeEntry)]
    [InlineData(Failure.NativeTargetHash)]
    public async Task AttemptedLegacyZipFailureNeverAuthorizesCompleteFileFallback(Failure failure)
    {
        using var fixture = await Fixture.CreateAsync(failure, legacyLayout: true);
        await fixture.PreloadAndOfferFormalAsync();
        fixture.OfferLegacyFormal();

        await Assert.ThrowsAsync<EndfieldIncrementalUpdateException>(() =>
            fixture.Service.SyncAsync(fixture.Installation, null, false));

        Assert.Equal(1, fixture.ArchiveRequests);
        Assert.Equal(0, fixture.MetadataRequests);
        Assert.Equal(0, fixture.FullResourceRequests);
        Assert.Equal("1.5.3", fixture.Installation.InstalledVersion);
        Assert.Equal(EndfieldPreloadState.Completed, fixture.Installation.PreloadState);
        Assert.Equal(fixture.OldExecutable, await File.ReadAllBytesAsync(fixture.GamePath("Endfield.exe")));
        Assert.Equal(fixture.SourceOnDisk, await File.ReadAllBytesAsync(fixture.Peer));
        Assert.Equal(fixture.Zip, await File.ReadAllBytesAsync(fixture.CachedArchive));
        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.Journal));
        Assert.False(journal.RootElement.GetProperty("Applying").GetBoolean());
    }

    [Fact]
    public async Task UnattemptedLargerLegacyZipCanUseDirectCompleteFileSynchronization()
    {
        using var fixture = await Fixture.CreateAsync(Failure.None, legacyLayout: true);
        fixture.OfferLegacyWithoutPreloadForExecutableOnly();
        Assert.True(fixture.Zip.Length > fixture.Executable.Length);

        await fixture.Service.SyncAsync(fixture.Installation, null, false);

        Assert.Equal("1.6.3", fixture.Installation.InstalledVersion);
        Assert.Equal(0, fixture.ArchiveRequests);
        Assert.Equal(0, fixture.MetadataRequests);
        Assert.Equal(1, fixture.FullResourceRequests);
        Assert.Equal(fixture.Executable, await File.ReadAllBytesAsync(fixture.GamePath("Endfield.exe")));
        Assert.False(File.Exists(fixture.Journal));
    }

    [Theory]
    [InlineData(Failure.SourceHash)]
    [InlineData(Failure.SourceSize)]
    [InlineData(Failure.NativeTargetHash)]
    [InlineData(Failure.UnsupportedCompression)]
    [InlineData(Failure.MissingNativeEntry)]
    [InlineData(Failure.MetadataHash)]
    [InlineData(Failure.MetadataTooLarge)]
    public async Task UnsafeNativeUpdateStopsBeforeCommitAndNeverFallsBackToFullResources(Failure failure)
    {
        using var fixture = await Fixture.CreateAsync(failure);
        await fixture.PreloadAndOfferFormalAsync();

        await Assert.ThrowsAsync<EndfieldIncrementalUpdateException>(() =>
            fixture.Service.SyncAsync(fixture.Installation, null, false));

        Assert.Equal(1, fixture.ArchiveRequests);
        Assert.Equal(0, fixture.FullResourceRequests);
        Assert.Equal("1.5.3", fixture.Installation.InstalledVersion);
        Assert.Equal(EndfieldPreloadState.Completed, fixture.Installation.PreloadState);
        Assert.Equal("1.6.3", fixture.Installation.PreloadTargetVersion);
        Assert.Equal(fixture.OldExecutable, await File.ReadAllBytesAsync(fixture.GamePath("Endfield.exe")));
        Assert.Equal(fixture.SourceOnDisk, await File.ReadAllBytesAsync(fixture.GamePath(Fixture.NativePath)));
        Assert.Equal(fixture.SourceOnDisk, await File.ReadAllBytesAsync(fixture.Peer));
        Assert.True(WindowsHardLink.Identity(fixture.Peer).SameFile(
            WindowsHardLink.Identity(fixture.GamePath(Fixture.NativePath))));
        Assert.False(File.Exists(fixture.GamePath(Fixture.FullPath)));
        Assert.False(File.Exists(fixture.GamePath(Fixture.BlcPath)));
        Assert.Equal("personal note", await File.ReadAllTextAsync(fixture.GamePath("personal-note.txt")));
        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.Journal));
        Assert.False(journal.RootElement.GetProperty("Applying").GetBoolean());
        Assert.Equal("1.5.3", journal.RootElement.GetProperty("SourceVersion").GetString());
        Assert.True(File.Exists(fixture.CachedArchive));
        Assert.Equal(fixture.Zip, await File.ReadAllBytesAsync(fixture.CachedArchive));
        Assert.Contains(DownloadTaskCache.ContentKey(Md5(fixture.Zip), fixture.Zip.Length),
            await new DownloadTaskCache(fixture.Data).ReferencedAsync());
        Assert.Empty(Directory.EnumerateDirectories(fixture.Game, ".aster-patch-*"));
        if (failure == Failure.MetadataTooLarge) Assert.Equal(0, fixture.MetadataRequests);
        else Assert.True(fixture.MetadataRequests > 0);
    }

    public enum Failure
    {
        None, SourceHash, SourceSize, NativeTargetHash, UnsupportedCompression,
        MissingNativeEntry, MetadataHash, MetadataTooLarge, InvalidArchive
    }

    private sealed class Fixture : IDisposable
    {
        public const string Vfs = "Endfield_Data/StreamingAssets/VFS";
        public const string NativePath = Vfs + "/AA/native.chk";
        public const string FullPath = Vfs + "/BB/full.chk";
        public const string BlcPath = Vfs + "/BB/BB.blc";
        private const string DeltaEntry = "vfs_files/vfs_patch/diff_1.6.3_1.5.3/AA_native.chk_patch";
        private readonly string _boundary;
        private readonly string _root;
        private readonly HttpClient _http;
        private readonly Provider _provider;
        private readonly EndfieldPatch _formal;
        private readonly EndfieldPatch _preload;
        private readonly byte[] _metadata;
        private bool _allowCompleteResource;
        public byte[] OriginalNative { get; } = Encoding.ASCII.GetBytes("base");
        public byte[] NewNative { get; } = Encoding.ASCII.GetBytes("base+new");
        public byte[] SourceOnDisk { get; }
        public byte[] Executable { get; } = Encoding.ASCII.GetBytes("new executable");
        public byte[] OldExecutable { get; } = Encoding.ASCII.GetBytes("old executable");
        public byte[] FullChunk { get; } = Encoding.ASCII.GetBytes("full resource");
        public byte[] Blc { get; } = Encoding.ASCII.GetBytes("VFS index");
        public byte[] Zip { get; }
        public string Game => Path.Combine(_root, "game");
        public string Data => Path.Combine(_root, "data");
        public string Peer => Path.Combine(_root, "peer", "native.chk");
        public string Journal => GamePath(".aster-maintenance.json");
        public string CachedArchive => Path.Combine(Data, "endfield", "objects",
            DownloadTaskCache.ContentKey(Md5(Zip), Zip.Length));
        public int ArchiveRequests { get; private set; }
        public int MetadataRequests { get; private set; }
        public int FullResourceRequests { get; private set; }
        public EndfieldInstallation Installation { get; }
        public EndfieldMaintenanceService Service { get; }

        private Fixture(Failure failure, bool includeLargeUnusedEntry, bool legacyLayout)
        {
            var project = Path.GetDirectoryName(Environment.GetEnvironmentVariable("DOTNET_CLI_HOME"))
                ?? throw new InvalidOperationException("Run with the repository environment.");
            _boundary = Path.GetFullPath(Path.Combine(project, ".artifacts", "test-data", "endfield-v2"));
            _root = Path.Combine(_boundary, Guid.NewGuid().ToString("N"));
            SourceOnDisk = failure switch
            {
                Failure.SourceHash => Encoding.ASCII.GetBytes("BASE"),
                Failure.SourceSize => Encoding.ASCII.GetBytes("bad"),
                _ => OriginalNative
            };
            var patchBytes = NativeAppend(OriginalNative, Encoding.ASCII.GetBytes("+new"),
                failure == Failure.UnsupportedCompression);
            var targetNativeHash = failure == Failure.NativeTargetHash ? new string('d', 32) : Md5(NewNative);
            var sources = new JsonArray(new JsonObject
            {
                ["base_file"] = "AA/native.chk", ["base_size"] = OriginalNative.Length,
                ["base_md5"] = Md5(OriginalNative),
                ["patch"] = "diff_1.6.3_1.5.3/AA_native.chk_patch", ["patch_size"] = patchBytes.Length
            });
            _metadata = Encoding.UTF8.GetBytes(new JsonObject
            {
                ["version"] = "1.6.3", ["vfs_base_path"] = Vfs,
                ["files"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "AA/native.chk", ["size"] = NewNative.Length,
                        ["md5"] = targetNativeHash, ["diffType"] = 1, ["patch"] = sources
                    },
                    FullRow("BB/full.chk", FullChunk, 1),
                    FullRow("BB/BB.blc", Blc, 2))
            }.ToJsonString());
            var entries = new Dictionary<string, byte[]>
            {
                ["Endfield.exe"] = Executable,
                ["vfs_files/files/" + FullPath] = FullChunk,
                ["vfs_files/files/" + BlcPath] = Blc
            };
            if (legacyLayout)
            {
                entries = new Dictionary<string, byte[]>
                {
                    ["Endfield.exe"] = Executable, [FullPath] = FullChunk, [BlcPath] = Blc
                };
                if (failure != Failure.MissingNativeEntry) entries.Add(NativePath, NewNative);
            }
            else if (failure != Failure.MissingNativeEntry) entries.Add(DeltaEntry, patchBytes);
            Zip = failure == Failure.InvalidArchive ? Encoding.ASCII.GetBytes("not-a-zip-fixture")
                : ZipBytes(entries, includeLargeUnusedEntry);
            var archive = new EndfieldRemoteObject(new Uri("https://beyond.hycdn.cn/fixture/preload.zip"), Md5(Zip), Zip.Length);
            _preload = new EndfieldPatch("1.6.3", [archive], Md5(Zip), Zip.Length);
            _formal = _preload with
            {
                Packs = [archive with { Uri = new Uri("https://beyond.hycdn.cn/fixture/release.zip") }],
                V2PatchInfoUri = new Uri("https://beyond.hycdn.cn/fixture/v2-info"),
                V2PatchInfoMd5 = failure == Failure.MetadataHash ? new string('e', 32) : Md5(_metadata),
                V2PatchInfoSize = failure == Failure.MetadataTooLarge ? 8 * 1024 * 1024 + 1 : _metadata.Length
            };
            var files = new EndfieldManifestFile[]
            {
                Entry("Endfield.exe", Executable), Entry(FullPath, FullChunk), Entry(BlcPath, Blc),
                new(NativePath, NewNative.Length, targetNativeHash)
            };
            _provider = new Provider(new EndfieldPackage(EndfieldChannel.Bilibili, "1.6.3",
                new Uri("https://beyond.hycdn.cn/fixture/full"), new string('f', 32), files, null, _preload));
            _http = new HttpClient(new Handler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/fixture/preload.zip") { ArchiveRequests++; return Ok(Zip); }
                if (path == "/fixture/v2-info") { MetadataRequests++; return Ok(_metadata); }
                if (path == "/fixture/release.zip") { ArchiveRequests++; return Ok(Zip); }
                FullResourceRequests++;
                if (_allowCompleteResource && path == "/fixture/full/Endfield.exe") return Ok(Executable);
                throw new InvalidOperationException("Unexpected complete-resource network request.");
            }));
            var tool = Path.Combine(project, ".artifacts", "vendor", "7zip-26.03", "extra", "x64", "7za.exe");
            Service = new EndfieldMaintenanceService(_provider, new EndfieldDownloadService(_http, Data),
                new EndfieldSharingService(static () => { }), Data, new EndfieldArchiveService(tool), static () => { });
            Installation = new EndfieldInstallation
            {
                Channel = EndfieldChannel.Bilibili, InstallRoot = Game, InstalledVersion = "1.5.3"
            };
        }

        public static async Task<Fixture> CreateAsync(Failure failure, bool includeLargeUnusedEntry = false, bool legacyLayout = false)
        {
            var fixture = new Fixture(failure, includeLargeUnusedEntry, legacyLayout);
            try
            {
                Directory.CreateDirectory(fixture.Game);
                Directory.CreateDirectory(Path.GetDirectoryName(fixture.Peer)!);
                var source = fixture.GamePath(NativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                await File.WriteAllBytesAsync(source, fixture.SourceOnDisk);
                Assert.True(WindowsHardLink.TryCreate(fixture.Peer, source));
                await File.WriteAllBytesAsync(fixture.GamePath("Endfield.exe"), fixture.OldExecutable);
                await File.WriteAllTextAsync(fixture.GamePath("personal-note.txt"), "personal note");
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        public async Task PreloadAndOfferFormalAsync()
        {
            await Service.PreloadAsync(Installation, _preload);
            Assert.Equal(1, ArchiveRequests);
            Assert.Equal(0, MetadataRequests);
            Assert.Equal(EndfieldPreloadState.Completed, Installation.PreloadState);
            Assert.True(File.Exists(CachedArchive));
            _provider.Package = _provider.Package with { Patch = _formal, PrePatch = null };
        }

        public void OfferFormalWithoutPreload() => _provider.Package = _provider.Package with { Patch = _formal, PrePatch = null };
        public void OfferLegacyFormal() => _provider.Package = _provider.Package with
        {
            Patch = _formal with { V2PatchInfoUri = null, V2PatchInfoMd5 = null, V2PatchInfoSize = null }, PrePatch = null
        };
        public void OfferLegacyWithoutPreloadForExecutableOnly()
        {
            OfferLegacyFormal();
            _provider.Package = _provider.Package with { Files = [Entry("Endfield.exe", Executable)] };
            _allowCompleteResource = true;
        }
        public void WithdrawFormalPatch() => _provider.Package = _provider.Package with { Patch = null, PrePatch = null };

        private IReadOnlyDictionary<string, byte[]> CompleteTargets() => new Dictionary<string, byte[]>
        {
            ["Endfield.exe"] = Executable, [FullPath] = FullChunk, [BlcPath] = Blc, [NativePath] = NewNative
        };

        public async Task CacheAllCompleteTargetsAsync()
        {
            var objects = SafeGamePath.Resolve(Data, "endfield/objects");
            Directory.CreateDirectory(objects);
            foreach (var bytes in CompleteTargets().Values)
                await File.WriteAllBytesAsync(SafeGamePath.Resolve(objects,
                    DownloadTaskCache.ContentKey(Md5(bytes), bytes.Length)), bytes);
        }

        public async Task InstallCompleteTargetsExceptAsync(string excluded)
        {
            foreach (var pair in CompleteTargets().Where(pair => pair.Key != excluded))
            {
                var destination = GamePath(pair.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                // Fixture preparation also replaces directory entries rather than writing through peer hard links.
                var temporary = GamePath(pair.Key + ".fixture-" + Guid.NewGuid().ToString("N"));
                await File.WriteAllBytesAsync(temporary, pair.Value);
                File.Move(temporary, destination, true);
            }
        }

        public string GamePath(string relative) => SafeGamePath.Resolve(Game, relative);

        public void Dispose()
        {
            _http.Dispose();
            var path = Path.GetFullPath(_root);
            if (!path.StartsWith(_boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Length != 32)
                throw new InvalidOperationException("Refusing cleanup outside the isolated fixture.");
            if (Directory.Exists(path))
            {
                SafeGamePath.Resolve(path, "cleanup-boundary-check");
                Directory.Delete(path, true);
            }
        }

        private static JsonObject FullRow(string name, byte[] bytes, int type) => new()
        {
            ["name"] = name, ["size"] = bytes.Length, ["md5"] = Md5(bytes), ["diffType"] = type,
            ["local_path"] = "vfs_files/files/" + Vfs + "/" + name
        };
    }

    private static byte[] NativeAppend(byte[] old, byte[] suffix, bool unsupportedCompression)
    {
        var covers = Join(Number(0), Number(0), Number(old.Length), Number(0), Number(suffix.Length), Number(0));
        var rle = Number(old.Length);
        var data = Join(Number(covers.Length), Number(rle.Length), covers, rle, suffix);
        return Join(Encoding.ASCII.GetBytes(unsupportedCompression ? "HDIFFSF20&zstd\0" : "HDIFFSF20&\0"),
            Number(old.Length + suffix.Length), Number(old.Length), Number(2),
            Number(covers.Length + rle.Length), Number(data.Length), Number(0), data);
    }

    private static byte[] Number(long value)
    {
        var bytes = new List<byte> { (byte)(value & 0x7f) };
        while ((value >>= 7) > 0) bytes.Insert(0, (byte)(0x80 | (value & 0x7f)));
        return bytes.ToArray();
    }

    private static byte[] Join(params byte[][] chunks) => chunks.SelectMany(chunk => chunk).ToArray();
    private static EndfieldManifestFile Entry(string path, byte[] bytes) => new(path, bytes.Length, Md5(bytes));
    private static string Md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));
    private static HttpResponseMessage Ok(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static byte[] ZipBytes(IReadOnlyDictionary<string, byte[]> files, bool includeLargeUnusedEntry)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var pair in files)
            {
                using var stream = zip.CreateEntry(pair.Key, CompressionLevel.NoCompression).Open();
                stream.Write(pair.Value);
            }
            if (includeLargeUnusedEntry)
            {
                using var stream = zip.CreateEntry("unrelated/unused.bin", CompressionLevel.SmallestSize).Open();
                var zeros = new byte[64 * 1024];
                for (var block = 0; block < 65 * 1024 / 64; block++) stream.Write(zeros);
            }
        }
        return memory.ToArray();
    }

    private sealed class Provider(EndfieldPackage package) : IEndfieldDistributionProvider
    {
        public EndfieldPackage Package { get; set; } = package;
        public Task<string> GetLatestVersionAsync(EndfieldChannel channel, string? installedVersion,
            CancellationToken cancellationToken = default) => Task.FromResult(Package.Version);
        public Task<EndfieldPackage> GetPackageAsync(EndfieldChannel channel, string? installedVersion,
            CancellationToken cancellationToken = default) => Task.FromResult(Package);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}