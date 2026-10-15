using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldDistributionProviderTests
{
    [Fact]
    public async Task FormalPatchWithoutOwnVersionUsesReleaseTargetAndPreservesNativeMetadata()
    {
        // Mirrors the official 1.5.3 -> 1.6.3 schema, without publisher addresses or credentials.
        var patch = Patch(packCount: 9);
        patch["package_size"] = "9362572553";
        patch["cd_key"] = "fixture-archive-password";
        patch["v2_patch_info_url"] = "https://beyond.hycdn.cn/fixture/patch-info";
        patch["v2_patch_info_md5"] = new string('b', 32);
        patch["v2_patch_info_size"] = "256";
        using var fixture = new Fixture(Metadata("1.6.3"), Metadata("1.6.3", patch));

        var package = await fixture.Provider.GetPackageAsync(EndfieldChannel.Bilibili, "1.5.3");

        Assert.Equal("1.6.3", package.Version);
        Assert.Equal("1.6.3", package.Patch!.TargetVersion);
        Assert.Equal(9, package.Patch.Packs.Count);
        Assert.Equal(9362572553L, package.Patch.PackageSize);
        Assert.Equal("fixture-archive-password", package.Patch.CdKey);
        Assert.Equal("https://beyond.hycdn.cn/fixture/patch-info", package.Patch.V2PatchInfoUri!.AbsoluteUri);
        Assert.Equal(new string('b', 32), package.Patch.V2PatchInfoMd5);
        Assert.Equal(256L, package.Patch.V2PatchInfoSize);
        Assert.Null(package.PrePatch);
        Assert.Single(package.Files);
        Assert.Equal(["", "1.5.3"], fixture.RequestedVersions);
        Assert.Equal(["2", "2"], fixture.RequestedChannels);
        Assert.Equal(1, fixture.ManifestRequests);
    }

    [Fact]
    public async Task FormalPatchPreservesV2MetadataSizeFromNumericField()
    {
        var patch = Patch();
        patch["v2_patch_info_size"] = 512;
        using var fixture = new Fixture(Metadata("1.6.3"), Metadata("1.6.3", patch));

        var package = await fixture.Provider.GetPackageAsync(EndfieldChannel.Official, "1.5.3");

        Assert.Equal(512L, package.Patch!.V2PatchInfoSize);
    }

    [Fact]
    public async Task ExplicitMatchingFormalPatchVersionIsKept()
    {
        using var fixture = new Fixture(Metadata("1.6.3"), Metadata("1.6.3", Patch("1.6.3")));

        var package = await fixture.Provider.GetPackageAsync(EndfieldChannel.Official, "1.5.3");

        Assert.Equal("1.6.3", package.Patch!.TargetVersion);
        Assert.Equal(["1", "1"], fixture.RequestedChannels);
    }

    [Fact]
    public async Task ExplicitFormalPatchTargetCannotDisagreeWithCompleteManifest()
    {
        using var fixture = new Fixture(Metadata("1.6.3"), Metadata("1.6.3", Patch("1.7.0")));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Provider.GetPackageAsync(EndfieldChannel.Official, "1.5.3"));
    }

    [Theory]
    [InlineData("1.6.2")]
    [InlineData("1.6.4")]
    public async Task VersionChangeBetweenCompleteAndVersionedMetadataIsRejected(string versionedTarget)
    {
        using var fixture = new Fixture(Metadata("1.6.3"), Metadata(versionedTarget, Patch()));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Provider.GetPackageAsync(EndfieldChannel.Official, "1.5.3"));

        Assert.Equal(2, fixture.RequestedVersions.Count);
        Assert.Equal(1, fixture.ManifestRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrePatchWithoutIndependentTargetNeverInheritsCurrentRelease(bool versioned)
    {
        using var fixture = versioned
            ? new Fixture(Metadata("1.6.3"), Metadata("1.6.3", prePatch: Patch()))
            : new Fixture(Metadata("1.6.3", prePatch: Patch()));

        var package = await fixture.Provider.GetPackageAsync(EndfieldChannel.Official, versioned ? "1.5.3" : null);

        Assert.Null(package.PrePatch);
        Assert.Null(package.Patch);
    }

    [Fact]
    public async Task UninstalledMetadataKeepsExplicitFuturePreloadTarget()
    {
        using var fixture = new Fixture(Metadata("1.6.3", prePatch: Patch("1.7.0")));

        var package = await fixture.Provider.GetPackageAsync(EndfieldChannel.Official, null);

        Assert.Equal("1.6.3", package.Version);
        Assert.Equal("1.7.0", package.PrePatch!.TargetVersion);
        Assert.Equal([""], fixture.RequestedVersions);
    }

    [Fact]
    public async Task FormalPatchAndFuturePrePatchKeepTheirDistinctTargets()
    {
        using var fixture = new Fixture(Metadata("1.6.3"),
            Metadata("1.6.3", Patch(), Patch("1.7.0")));

        var package = await fixture.Provider.GetPackageAsync(EndfieldChannel.Official, "1.5.3");

        Assert.Equal("1.6.3", package.Patch!.TargetVersion);
        Assert.Equal("1.7.0", package.PrePatch!.TargetVersion);
    }

    [Fact]
    public async Task VersionedResponseCanOmitCompletePackageAndReplacesUnversionedPatch()
    {
        var update = Metadata("1.6.3", prePatch: Patch("1.7.0"));
        update.Remove("pkg");
        using var fixture = new Fixture(Metadata("1.6.3", Patch()), update);

        var package = await fixture.Provider.GetPackageAsync(EndfieldChannel.Official, "1.5.3");

        Assert.Single(package.Files);
        Assert.Null(package.Patch);
        Assert.Equal("1.7.0", package.PrePatch!.TargetVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task UninstalledRequestUsesOnlyCompleteMetadata(string? installedVersion)
    {
        using var fixture = new Fixture(Metadata("1.6.3"));

        var package = await fixture.Provider.GetPackageAsync(EndfieldChannel.Official, installedVersion);

        Assert.Equal("1.6.3", package.Version);
        Assert.Equal([""], fixture.RequestedVersions);
        Assert.Null(package.Patch);
    }

    [Fact]
    public async Task FormalPatchWithoutRemoteObjectsRemainsUnavailable()
    {
        using var fixture = new Fixture(Metadata("1.6.3"),
            Metadata("1.6.3", new JsonObject { ["patches"] = new JsonArray() }));

        var package = await fixture.Provider.GetPackageAsync(EndfieldChannel.Official, "1.5.3");

        Assert.Null(package.Patch);
    }

    [Fact]
    public async Task TargetFallbackDoesNotBypassPackIntegrityValidation()
    {
        var patch = Patch();
        patch["patches"]![0]!["md5"] = "invalid";
        using var fixture = new Fixture(Metadata("1.6.3"), Metadata("1.6.3", patch));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Provider.GetPackageAsync(EndfieldChannel.Official, "1.5.3"));
    }

    private static JsonObject Metadata(string version, JsonObject? patch = null, JsonObject? prePatch = null) => new()
    {
        ["version"] = version,
        ["pkg"] = new JsonObject
        {
            ["file_path"] = "https://beyond.hycdn.cn/fixture/files",
            ["game_files_md5"] = Convert.ToHexStringLower(MD5.HashData(EncryptedManifest))
        },
        ["patch"] = patch,
        ["pre_patch"] = prePatch
    };

    private static JsonObject Patch(string? version = null, int packCount = 1)
    {
        var packs = new JsonArray();
        for (var index = 1; index <= packCount; index++)
            packs.Add(new JsonObject
            {
                ["url"] = $"https://beyond.hycdn.cn/fixture/patch.zip.{index:000}",
                ["md5"] = new string('a', 32),
                ["package_size"] = "128"
            });
        var patch = new JsonObject
        {
            ["patches"] = packs,
            ["md5"] = new string('c', 32),
            ["package_size"] = "128"
        };
        if (version is not null) patch["version"] = version;
        return patch;
    }

    private static readonly byte[] EncryptedManifest = EncryptManifest();

    private static byte[] EncryptManifest()
    {
        using var aes = Aes.Create();
        aes.Key =
        [
            0xC0, 0xF3, 0x0E, 0x1C, 0xE7, 0x63, 0xBB, 0xC2, 0x1C, 0xC3, 0x55, 0xA3, 0x43, 0x03, 0xAC, 0x50,
            0x39, 0x94, 0x44, 0xBF, 0xF6, 0x8C, 0x4A, 0x22, 0xAF, 0x39, 0x8C, 0x0A, 0x16, 0x6E, 0xE1, 0x43
        ];
        aes.IV =
        [
            0x33, 0x46, 0x78, 0x61, 0x19, 0x27, 0x50, 0x64,
            0x95, 0x01, 0x93, 0x72, 0x64, 0x60, 0x84, 0x00
        ];
        var text = "{\"path\":\"Endfield.exe\",\"size\":7,\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}\n";
        var bytes = Encoding.UTF8.GetBytes(text);
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(bytes, 0, bytes.Length);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        private readonly Queue<JsonObject> _metadata;
        public EndfieldDistributionProvider Provider { get; }
        public List<string> RequestedVersions { get; } = [];
        public List<string> RequestedChannels { get; } = [];
        public int ManifestRequests { get; private set; }

        public Fixture(params JsonObject[] metadata)
        {
            _metadata = new Queue<JsonObject>(metadata);
            _http = new HttpClient(new Handler(RespondAsync));
            Provider = new EndfieldDistributionProvider(_http);
        }

        private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request)
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("https://beyond.hycdn.cn/fixture/files/game_files", request.RequestUri!.AbsoluteUri);
                ManifestRequests++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(EncryptedManifest) };
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = document.RootElement.GetProperty("proxy_reqs")[0].GetProperty("get_latest_game_req");
            RequestedVersions.Add(body.GetProperty("version").GetString()!);
            RequestedChannels.Add(body.GetProperty("channel").GetString()!);
            Assert.NotEmpty(_metadata);
            var payload = new JsonObject
            {
                ["proxy_rsps"] = new JsonArray(new JsonObject
                {
                    ["kind"] = "get_latest_game",
                    ["get_latest_game_rsp"] = _metadata.Dequeue()
                })
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);
    }
}