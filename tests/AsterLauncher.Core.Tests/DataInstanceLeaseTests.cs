using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class DataInstanceLeaseTests
{
    private static string Root() => Path.GetFullPath(Path.Combine(
        Environment.GetEnvironmentVariable("ASTERLAUNCHER_TEST_TEMP_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "instance-fixtures"), Guid.NewGuid().ToString("N")));

    [Fact]
    public void SameDataSecondarySignalsPrimaryWithoutAcquiringTheWritableLease()
    {
        using var activated = new ManualResetEventSlim();
        var root = Root();
        using var first = new DataInstanceLease(root, activated.Set);
        using var second = new DataInstanceLease(root.ToUpperInvariant() + Path.DirectorySeparatorChar, () => { });
        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void DifferentDataRootsCanRunIndependently()
    {
        using var first = new DataInstanceLease(Root(), () => { });
        using var second = new DataInstanceLease(Root(), () => { });
        Assert.True(first.IsPrimary);
        Assert.True(second.IsPrimary);
    }

    [Fact]
    public void ClosingPrimaryReleasesItsDataRootForTheNextLaunch()
    {
        var root = Root();
        using (var first = new DataInstanceLease(root, () => { })) Assert.True(first.IsPrimary);
        using var reopened = new DataInstanceLease(root, () => { });
        Assert.True(reopened.IsPrimary);
    }

    [Fact]
    public void RelativeDataDirectoryIsRejectedWithoutCreatingState()
        => Assert.Throws<ArgumentException>(() => new DataInstanceLease("relative-data", () => { }));
}