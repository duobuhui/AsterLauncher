namespace AsterLauncher.Infrastructure;

/// <summary>Explicit consent for one elevated bridge attempt; implementations must default to decline.</summary>
public interface IEndfieldBridgeElevationConsent
{
    Task<bool> ConfirmAsync(CancellationToken cancellationToken = default);
}

internal static class EndfieldBridgeElevationRetry
{
    public static async Task<EndfieldBridgeLaunchResult> RunAsync(
        Func<Task<EndfieldBridgeLaunchResult>> ordinary,
        Func<Task<EndfieldBridgeLaunchResult>> elevated,
        IEndfieldBridgeElevationConsent? consent,
        CancellationToken cancellationToken)
    {
        try { return await ordinary(); }
        catch (EndfieldBridgeLaunchException failure) when (failure.RequiresElevation)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (consent is null || !await consent.ConfirmAsync(cancellationToken))
                throw new EndfieldBridgeLaunchException("elevation_declined", "已取消本次管理员授权，可切回官方启动器接管。");
            cancellationToken.ThrowIfCancellationRequested();
            // Only a definitively failed parent creation can reach this explicit, single retry.
            return await elevated();
        }
    }
}
