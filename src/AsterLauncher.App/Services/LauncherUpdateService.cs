using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.App.Services;

public sealed class LauncherUpdateService
{
    public const string RepositoryUrl = "https://github.com/duobuhui/AsterLauncher";
    private const string ReleasesApi = "https://api.github.com/repos/duobuhui/AsterLauncher/releases?per_page=30";
    private static readonly HttpClient Client = CreateClient();

    public string CurrentVersion => typeof(App).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0] ?? "0.1.1-beta";

    public async Task<LauncherUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(ReleasesApi, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var releases = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!TryVersion(CurrentVersion, out var installed))
        {
            throw new InvalidDataException("当前应用版本格式无效。");
        }

        LauncherUpdate? newest = null;
        VersionKey newestKey = installed;
        foreach (var release in releases.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()
                || !TryVersion(release.GetProperty("tag_name").GetString(), out var releaseKey)
                || releaseKey.CompareTo(newestKey) <= 0)
            {
                continue;
            }

            var manifestAsset = FindAsset(release, "aster-update.json");
            if (manifestAsset is null) continue;

            using var manifestResponse = await Client.GetAsync(manifestAsset, cancellationToken);
            manifestResponse.EnsureSuccessStatusCode();
            using var manifest = JsonDocument.Parse(await manifestResponse.Content.ReadAsStreamAsync(cancellationToken));
            var root = manifest.RootElement;
            if (!TryVersion(root.GetProperty("version").GetString(), out var manifestKey)
                || manifestKey.CompareTo(releaseKey) != 0)
            {
                throw new InvalidDataException("GitHub Release 与更新清单的版本不一致。");
            }

            var full = ReadPackage(release, root.GetProperty("full"));
            UpdatePackage chosen = full;
            if (root.TryGetProperty("patch", out var patch)
                && patch.ValueKind == JsonValueKind.Object
                && string.Equals(patch.GetProperty("from").GetString(), CurrentVersion, StringComparison.OrdinalIgnoreCase))
            {
                chosen = ReadPackage(release, patch);
            }

            newestKey = releaseKey;
            newest = new LauncherUpdate(root.GetProperty("version").GetString()!, chosen,
                chosen == full ? "完整包" : "差量更新");
        }

        return newest;
    }

    public async Task DownloadAndInstallAsync(LauncherUpdate update, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetFileName(Environment.ProcessPath), "AsterLauncher.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("请从正式发布包运行启动器后再安装更新。");
        }
        var dataRoot = LauncherDataPaths.ResolveDataDirectory();
        var stage = Path.Combine(dataRoot, "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var packagePath = Path.Combine(stage, "package.zip");
        using (var response = await Client.GetAsync(update.Package.Url,
                   HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = File.Create(packagePath);
            await source.CopyToAsync(destination, cancellationToken);
        }

        await using (var stream = File.OpenRead(packagePath))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!actual.Equals(update.Package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(packagePath);
                throw new InvalidDataException("更新包校验失败，未安装。 ");
            }
        }

        var scriptPath = Path.Combine(stage, "apply-update.ps1");
        await using (var resource = typeof(LauncherUpdateService).Assembly
                         .GetManifestResourceStream("AsterLauncher.ApplyUpdate.ps1")
                     ?? throw new InvalidOperationException("更新脚本缺失。"))
        await using (var destination = File.Create(scriptPath))
        {
            await resource.CopyToAsync(destination, cancellationToken);
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法定位当前启动器。 ");
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = stage
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                     "-Package", packagePath, "-TargetExe", executable,
                     "-Mode", update.Package.IsPatch ? "Patch" : "Full",
                     "-ProcessId", Environment.ProcessId.ToString(),
                     "-ExpectedHash", update.Package.TargetSha256
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("无法启动更新程序。 ");
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    private static UpdatePackage ReadPackage(JsonElement release, JsonElement package)
    {
        var name = package.GetProperty("asset").GetString();
        var sha = package.GetProperty("sha256").GetString();
        var target = package.GetProperty("targetSha256").GetString();
        if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(sha ?? "", "^[0-9a-fA-F]{64}$")
            || !Regex.IsMatch(target ?? "", "^[0-9a-fA-F]{64}$"))
        {
            throw new InvalidDataException("更新清单缺少有效的文件校验值。");
        }
        var asset = FindAsset(release, name)
            ?? throw new InvalidDataException($"Release 缺少更新文件 {name}。");
        return new UpdatePackage(asset, sha!, target!, package.TryGetProperty("from", out _));
    }

    private static Uri? FindAsset(JsonElement release, string name)
    {
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com"
                && uri.AbsolutePath.StartsWith("/duobuhui/AsterLauncher/releases/download/", StringComparison.Ordinal))
            {
                return uri;
            }
        }
        return null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AsterLauncher-Updater/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static bool TryVersion(string? value, out VersionKey version)
    {
        version = default;
        var match = Regex.Match(value ?? "", @"^v?(\d+)\.(\d+)\.(\d+)(?:-(beta)(?:\.(\d+))?)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major)
            || !int.TryParse(match.Groups[2].Value, out var minor)
            || !int.TryParse(match.Groups[3].Value, out var patch)) return false;
        var number = match.Groups[5].Success && int.TryParse(match.Groups[5].Value, out var parsed) ? parsed : 0;
        version = new VersionKey(major, minor, patch, match.Groups[4].Success ? 0 : 1, number);
        return true;
    }

    private readonly record struct VersionKey(int Major, int Minor, int Patch, int Stability, int Prerelease)
        : IComparable<VersionKey>
    {
        public int CompareTo(VersionKey other)
        {
            var a = (Major, Minor, Patch, Stability, Prerelease);
            var b = (other.Major, other.Minor, other.Patch, other.Stability, other.Prerelease);
            return a.CompareTo(b);
        }
    }
}

public sealed record LauncherUpdate(string Version, UpdatePackage Package, string Method);
public sealed record UpdatePackage(Uri Url, string Sha256, string TargetSha256, bool IsPatch);
