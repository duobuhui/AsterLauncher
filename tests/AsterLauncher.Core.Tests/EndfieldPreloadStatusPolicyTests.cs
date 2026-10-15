using AsterLauncher.Core;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldPreloadStatusPolicyTests
{
    private static readonly EndfieldRemoteObject First = new(new Uri("https://ak-uc.hypergryph.com/a"),
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 123);
    private static readonly EndfieldRemoteObject Second = new(new Uri("https://ak-uc.hypergryph.com/b"),
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", 456);

    [Fact]
    public void CheckingTheSameOfferRetainsCompletionAndIdentity()
    {
        var installation = Completed();
        var offer = Offer();

        EndfieldPreloadStatusPolicy.ApplyCheck(installation, offer);

        Assert.Equal(EndfieldPreloadState.Completed, installation.PreloadState);
        Assert.True(EndfieldPreloadStatusPolicy.IsCompletedOffer(installation, offer));
        Assert.Equal("1.5.3", installation.PreloadSourceVersion);
        Assert.Equal("1.6.3", installation.PreloadTargetVersion);
        Assert.Equal("CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", installation.PreloadContentHash);
    }

    [Fact]
    public void WithdrawnOfferKeepsCompletionReceiptForFormalUpdate()
    {
        var installation = Completed();

        EndfieldPreloadStatusPolicy.ApplyCheck(installation, null);

        Assert.True(EndfieldPreloadStatusPolicy.HasCompletedRecord(installation));
        Assert.False(EndfieldPreloadStatusPolicy.IsCompletedOffer(installation, null));
        Assert.Equal("1.6.3", installation.PreloadTargetVersion);
    }

    [Fact]
    public void FailedCheckKeepsCompletionReceipt()
    {
        var installation = Completed();

        EndfieldPreloadStatusPolicy.ApplyCheckFailure(installation);

        Assert.True(EndfieldPreloadStatusPolicy.HasCompletedRecord(installation));
    }

    [Fact]
    public void ReplacedContentDoesNotMasqueradeAsTheCompletedOffer()
    {
        var installation = Completed();
        var replaced = Offer() with { ContentMd5 = "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD" };

        EndfieldPreloadStatusPolicy.ApplyCheck(installation, replaced);

        Assert.True(EndfieldPreloadStatusPolicy.HasCompletedRecord(installation));
        Assert.False(EndfieldPreloadStatusPolicy.IsCompletedOffer(installation, replaced));
        Assert.Equal("CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", installation.PreloadContentHash);
    }

    [Theory]
    [InlineData("1.5.2", "1.6.3")]
    [InlineData("1.5.3", "1.6.4")]
    public void AnotherSourceOrTargetIsNotTheCompletedOffer(string source, string target)
    {
        var installation = Completed();
        installation.InstalledVersion = source;

        Assert.False(EndfieldPreloadStatusPolicy.IsCompletedOffer(installation,
            Offer() with { TargetVersion = target }));
    }

    [Fact]
    public void LegacyPackFingerprintStillRecognizesTheSameOffer()
    {
        var installation = Completed();
        installation.PreloadContentHash = $"{First.Md5}-{Second.Md5}".ToLowerInvariant();

        Assert.True(EndfieldPreloadStatusPolicy.IsCompletedOffer(installation,
            Offer() with { ContentMd5 = null }));
    }

    [Theory]
    [InlineData(null, "1.6.3", "hash")]
    [InlineData("1.5.3", null, "hash")]
    [InlineData("1.5.3", "1.6.3", " ")]
    public void IncompleteReceiptCanBeRechecked(string? source, string? target, string? hash)
    {
        var installation = Completed();
        installation.PreloadSourceVersion = source;
        installation.PreloadTargetVersion = target;
        installation.PreloadContentHash = hash;

        EndfieldPreloadStatusPolicy.ApplyCheck(installation, Offer());

        Assert.False(EndfieldPreloadStatusPolicy.HasCompletedRecord(installation));
        Assert.Equal(EndfieldPreloadState.Available, installation.PreloadState);
    }

    [Theory]
    [InlineData(false, EndfieldPreloadState.NotOpen)]
    [InlineData(true, EndfieldPreloadState.Available)]
    public void ACheckWithoutCompletionTracksCurrentAvailability(bool available, EndfieldPreloadState expected)
    {
        var installation = new EndfieldInstallation();

        EndfieldPreloadStatusPolicy.ApplyCheck(installation, available ? Offer() : null);

        Assert.Equal(expected, installation.PreloadState);
    }

    [Fact]
    public void AFailedCheckWithoutCompletionIsNotOpenOrCompleted()
    {
        var installation = new EndfieldInstallation();

        EndfieldPreloadStatusPolicy.ApplyCheckFailure(installation);

        Assert.Equal(EndfieldPreloadState.CheckFailed, installation.PreloadState);
    }

    private static EndfieldPatch Offer() => new("1.6.3", [First, Second],
        "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", 579);

    private static EndfieldInstallation Completed() => new()
    {
        InstalledVersion = "1.5.3",
        PreloadState = EndfieldPreloadState.Completed,
        PreloadSourceVersion = "1.5.3",
        PreloadTargetVersion = "1.6.3",
        PreloadContentHash = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC"
    };
}