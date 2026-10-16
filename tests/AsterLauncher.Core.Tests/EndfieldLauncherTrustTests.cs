using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldLauncherTrustTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "endfield-launcher-trust-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true, "Shanghai Hypergryph Network Technology Co., Ltd.", true)]
    [InlineData(true, "上海鹰角网络科技有限公司", true)]
    [InlineData(false, "Shanghai Hypergryph Network Technology Co., Ltd.", false)]
    [InlineData(true, "Unrelated signing company", false)]
    [InlineData(true, "Shanghai Hypergryph Network Technology Co., Ltd. Extra", false)]
    [InlineData(true, "CN=Hypergryph", false)]
    public void BridgeRequiresBothValidSignatureAndExactPublisher(bool valid, string publisher, bool expected)
    {
        var result = new EndfieldLauncherTrust(_ => new(valid, publisher, "subject", "fingerprint", valid ? 0 : -1))
            .Verify(FileAt("Games.exe"));
        Assert.Equal(valid, result.IsSignatureValid);
        Assert.Equal(expected, result.IsRecognizedPublisher);
        Assert.Equal(expected, result.CanUseAsBridgeParent);
    }

    [Fact]
    public void RecognizedPublisherDoesNotMakeLauncherExeABridgeParent()
    {
        var path = FileAt("Launcher.exe");
        var result = new EndfieldLauncherTrust(_ => new(true, "Shanghai Hypergryph Network Technology Co., Ltd.", "subject", "fingerprint", 0)).Verify(path);
        Assert.True(result.IsRecognizedPublisher);
        Assert.False(result.CanUseAsBridgeParent);
        Assert.Equal("fingerprint", result.CertificateSha256);
    }

    [Fact]
    public void MissingFileFailsClosedWithoutCallingNativeVerifier()
    {
        var calls = 0;
        var service = new EndfieldLauncherTrust(_ => { calls++; throw new InvalidOperationException(); });
        Assert.False(service.Verify(Path.Combine(_root, "Games.exe")).IsSignatureValid);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void SignatureVerificationDeniesMutationWhileReadingPublisherEvidence()
    {
        var path = FileAt("Games.exe");
        var service = new EndfieldLauncherTrust(_ =>
        {
            Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            Assert.Throws<IOException>(() => File.Move(path, path + ".replaced"));
            return new(true, "Shanghai Hypergryph Network Technology Co., Ltd.", "subject", "fingerprint", 0);
        });
        Assert.True(service.Verify(path).CanUseAsBridgeParent);
    }

    [Fact]
    public void RealWinTrustRejectsUnsignedFixtureWithoutLaunchingIt()
    {
        var result = new EndfieldLauncherTrust().Verify(FileAt("Games.exe"));
        Assert.False(result.IsSignatureValid);
        Assert.False(result.CanUseAsBridgeParent);
        Assert.NotEqual(0, result.NativeStatus);
    }

    [Theory]
    [InlineData("Games.exe")]
    [InlineData("\\\\server\\share\\Games.exe")]
    [InlineData("E:\\Games\\Games.exe:evil.exe")]
    [InlineData("E:\\Games\\Games.dll")]
    public void NonLocalOrNonExePathsAreRejected(string path) =>
        Assert.False(EndfieldLauncherTrust.TryNormalizeExecutable(path, out _));

    private string FileAt(string name)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [0x4D, 0x5A]);
        return path;
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
