using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldBridgeElevationRetryTests
{
    private static EndfieldBridgeLaunchException Required() => new("official_process_creation_failed", "fixed", 740, "create_process");

    [Fact]
    public async Task OrdinarySuccessNeverRequestsConsentOrElevation()
    {
        var consent = new Consent(true);
        var elevated = 0;
        var result = await EndfieldBridgeElevationRetry.RunAsync(() => Task.FromResult(new EndfieldBridgeLaunchResult(1, 2, false)),
            () => { elevated++; return Task.FromResult(new EndfieldBridgeLaunchResult(3, 4, false)); }, consent, default);
        Assert.Equal(1, result.GamePid);
        Assert.Equal(0, consent.Calls);
        Assert.Equal(0, elevated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentElevationFailureRequiresOneExplicitConsent(bool approve)
    {
        var consent = new Consent(approve);
        var ordinary = 0;
        var elevated = 0;
        Task<EndfieldBridgeLaunchResult> First() { ordinary++; return Task.FromException<EndfieldBridgeLaunchResult>(Required()); }
        Task<EndfieldBridgeLaunchResult> Retry() { elevated++; return Task.FromResult(new EndfieldBridgeLaunchResult(3, 4, false)); }
        if (approve)
            Assert.Equal(3, (await EndfieldBridgeElevationRetry.RunAsync(First, Retry, consent, default)).GamePid);
        else
            Assert.Equal("elevation_declined", (await Assert.ThrowsAsync<EndfieldBridgeLaunchException>(
                () => EndfieldBridgeElevationRetry.RunAsync(First, Retry, consent, default))).Code);
        Assert.Equal(1, ordinary);
        Assert.Equal(1, consent.Calls);
        Assert.Equal(approve ? 1 : 0, elevated);
    }

    [Theory]
    [InlineData("game_process_creation_failed", 740, "create_process")]
    [InlineData("official_process_creation_failed", 5, "create_process")]
    [InlineData("official_process_creation_failed", 740, "update_attribute_list")]
    [InlineData("official_process_creation_failed", 740, null)]
    [InlineData("bridge_unconfirmed", 740, "create_process")]
    public async Task AmbiguousOrOtherFailuresNeverElevate(string code, int native, string? stage)
    {
        var consent = new Consent(true);
        var elevated = 0;
        await Assert.ThrowsAsync<EndfieldBridgeLaunchException>(() => EndfieldBridgeElevationRetry.RunAsync(
            () => Task.FromException<EndfieldBridgeLaunchResult>(new EndfieldBridgeLaunchException(code, "fixed", native, stage)),
            () => { elevated++; return Task.FromResult(new EndfieldBridgeLaunchResult(3, 4, false)); }, consent, default));
        Assert.Equal(0, consent.Calls);
        Assert.Equal(0, elevated);
    }

    [Fact]
    public async Task ElevatedFailureNeverLoopsOrRequestsAgain()
    {
        var consent = new Consent(true);
        var elevated = 0;
        await Assert.ThrowsAsync<EndfieldBridgeLaunchException>(() => EndfieldBridgeElevationRetry.RunAsync(
            () => Task.FromException<EndfieldBridgeLaunchResult>(Required()),
            () => { elevated++; return Task.FromException<EndfieldBridgeLaunchResult>(Required()); }, consent, default));
        Assert.Equal(1, elevated);
        Assert.Equal(1, consent.Calls);
    }

    [Fact]
    public async Task MissingConsentImplementationDefaultsToDecline()
    {
        var elevated = 0;
        await Assert.ThrowsAsync<EndfieldBridgeLaunchException>(() => EndfieldBridgeElevationRetry.RunAsync(
            () => Task.FromException<EndfieldBridgeLaunchResult>(Required()),
            () => { elevated++; return Task.FromResult(new EndfieldBridgeLaunchResult(3, 4, false)); }, null, default));
        Assert.Equal(0, elevated);
    }

    [Fact]
    public async Task CancellationDuringConsentPreventsElevatedCreation()
    {
        using var cancellation = new CancellationTokenSource();
        var consent = new Consent(true, cancellation.Cancel);
        var elevated = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EndfieldBridgeElevationRetry.RunAsync(
            () => Task.FromException<EndfieldBridgeLaunchResult>(Required()),
            () => { elevated++; return Task.FromResult(new EndfieldBridgeLaunchResult(3, 4, false)); }, consent, cancellation.Token));
        Assert.Equal(0, elevated);
    }

    private sealed class Consent(bool answer, Action? action = null) : IEndfieldBridgeElevationConsent
    {
        public int Calls { get; private set; }
        public Task<bool> ConfirmAsync(CancellationToken cancellationToken = default)
        { Calls++; action?.Invoke(); return Task.FromResult(answer); }
    }
}
