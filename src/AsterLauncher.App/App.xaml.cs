using AsterLauncher.App.Services;
using AsterLauncher.App.ViewModels;
using AsterLauncher.App.Views;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace AsterLauncher.App;

public partial class App : Application
{
    private readonly ServiceProvider _services;
    private Window? _window;
    private readonly DataInstanceLease _instanceLease;
    private bool _activationPending;
    private bool _windowClosed;

    public App()
    {
        InitializeComponent();
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _instanceLease = new DataInstanceLease(LauncherDataPaths.ResolveDataDirectory(),
            () => dispatcher.TryEnqueue(ActivateExistingWindow));
        if (!_instanceLease.IsPrimary)
        {
            // A secondary process must not initialize logs, configuration or any data service.
            _services = new ServiceCollection().BuildServiceProvider();
            return;
        }
        try
        {
            // Legacy migration copies into this already resolved data root; it never changes the selection.
            LauncherDataPaths.MigrateLegacyBundleDataIfNeeded();
            var services = new ServiceCollection();
            ConfigureServices(services);
            _services = services.BuildServiceProvider();
        }
        catch { _instanceLease.Dispose(); throw; }
    }

    public static Window MainWindow { get; private set; } = null!;

    public IServiceProvider Services => _services;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!_instanceLease.IsPrimary)
        {
            _instanceLease.Dispose();
            Exit();
            return;
        }
        try
        {
            await _services.GetRequiredService<LauncherViewModel>().InitializeAsync();
            await Task.Run(() => _services.GetRequiredService<ResourceUpdateService>().LoadAsync());
            ShowMainWindow(_services.GetRequiredService<MainWindow>());
            _ = RefreshWallpapersAsync();
            if (_services.GetRequiredService<LauncherViewModel>().ResourceUpdatesEnabled) _ = RefreshResourcesAsync();
        }
        catch (ConfigurationReadException exception)
        {
            ShowMainWindow(new ConfigurationRecoveryWindow(exception, (JsonConfigurationStore)_services.GetRequiredService<IConfigurationStore>()));
        }
        catch (Exception exception)
        {
            _windowClosed = true;
            _instanceLease.Dispose();
            _services.GetRequiredService<ILogger<App>>().LogCritical(exception, "Application startup failed");
            throw;
        }
    }

    private void ShowMainWindow(Window window)
    {
        _window = window;
        MainWindow = window;
        window.Closed += (_, _) =>
        {
            _windowClosed = true;
            _instanceLease.Dispose();
        };
        window.Activate();
        if (_activationPending) ActivateExistingWindow();
    }

    private void ActivateExistingWindow()
    {
        if (_windowClosed) return;
        if (_window is null) { _activationPending = true; return; }
        _activationPending = false;
        if (_window is MainWindow launcher) launcher.ShowFromSecondaryInstance();
        else
        {
            _window.AppWindow.Show();
            if (_window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                presenter.Restore();
        }
        _window.Activate();
    }

    private async Task RefreshResourcesAsync()
    {
        try { await _services.GetRequiredService<ResourceUpdateService>().RefreshAsync(); }
        catch (Exception ex) { _services.GetRequiredService<ILogger<App>>().LogWarning("Resource update ended with {ErrorType}; keeping cached/bundled resources", ex.GetType().Name); }
    }

    private async Task RefreshWallpapersAsync()
    {
        try
        {
            var updated = await _services.GetRequiredService<WallpaperUpdateService>().RefreshOnceAsync();
            if (updated.Count > 0)
            {
                _window?.DispatcherQueue.TryEnqueue(() =>
                    _services.GetRequiredService<LauncherViewModel>().RefreshArtwork(updated));
            }
        }
        catch (Exception exception)
        {
            _services.GetRequiredService<ILogger<App>>().LogWarning(
                "Wallpaper update ended with {ErrorType}", exception.GetType().Name);
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        var logStore = new LauncherLogStore();
        services.AddSingleton(logStore);
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(logStore);
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.AddSingleton<IConfigurationStore, JsonConfigurationStore>();
        services.AddSingleton<ICompanionProcessService, CompanionProcessService>();
        services.AddSingleton<DialogLaunchDecisionService>();
        services.AddSingleton<ILaunchDecisionService>(provider => provider.GetRequiredService<DialogLaunchDecisionService>());
        services.AddSingleton<GameLaunchOrchestrator>();
        services.AddSingleton<EndfieldLauncherTrust>();
        services.AddSingleton(p => new EndfieldLauncherDiscovery(trust: p.GetRequiredService<EndfieldLauncherTrust>()));
        services.AddSingleton<EndfieldLaunchBridgeService>();
        services.AddSingleton(p => new EndfieldLaunchCoordinator(
            p.GetRequiredService<ICompanionProcessService>(), p.GetRequiredService<ILaunchDecisionService>(),
            p.GetRequiredService<ILogger<GameLaunchOrchestrator>>(), p.GetRequiredService<EndfieldLauncherDiscovery>(),
            p.GetRequiredService<EndfieldLaunchBridgeService>(), LauncherDataPaths.ResolveDataDirectory(),
            Path.Combine(AppContext.BaseDirectory, "Services", "StartEndfieldBridge.ps1"), p.GetRequiredService<DialogLaunchDecisionService>()));
        foreach (var adapter in BuiltInGameCatalog.CreateAdapters())
        {
            services.AddSingleton(typeof(IGameAdapter), adapter);
        }
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<IUigfArchiveService>(p => new UigfArchiveService(p.GetRequiredService<ILogger<UigfArchiveService>>(), p.GetRequiredService<IResourceCatalogProvider>()));
        services.AddSingleton<GachaAccountSelection>();
        services.AddSingleton<IEndfieldGachaAccountService, EndfieldGachaAccountService>();
        services.AddSingleton<IEndfieldGachaArchiveService>(provider => provider.GetRequiredService<IEndfieldGachaAccountService>());
        services.AddSingleton<LauncherUpdateService>();
        services.AddSingleton(p => new ResourceUpdateService(new HttpClient(new EndfieldNodeHttpHandler(Path.Combine(AppContext.BaseDirectory, "NetworkRuntime"))) { Timeout = TimeSpan.FromSeconds(40) }, LauncherDataPaths.ResolveDataDirectory(), Path.Combine(AppContext.BaseDirectory, "ResourceFeed", "catalog.json")));
        services.AddSingleton<IResourceCatalogProvider>(p => p.GetRequiredService<ResourceUpdateService>());
        var endfieldNetworkRuntime = Path.Combine(AppContext.BaseDirectory, "NetworkRuntime");
#if DEBUG
        if (EndfieldMaintenanceUiFixture.Enabled)
        {
            services.AddSingleton<IEndfieldDistributionProvider>(new EndfieldMaintenanceUiFixture());
            services.AddSingleton(new EndfieldDownloadService(new HttpClient(new EndfieldMaintenanceUiFixture()),LauncherDataPaths.ResolveDataDirectory()));
        }
        else
#endif
        {
            services.AddSingleton<IEndfieldDistributionProvider>(provider =>
                new EndfieldDistributionProvider(new HttpClient(new EndfieldNodeHttpHandler(endfieldNetworkRuntime))
                { Timeout = TimeSpan.FromMinutes(3) }));
            services.AddSingleton(provider => new EndfieldDownloadService(
                new HttpClient(new EndfieldNodeHttpHandler(endfieldNetworkRuntime))
                { Timeout = TimeSpan.FromMinutes(30) },LauncherDataPaths.ResolveDataDirectory()));
        }        services.AddSingleton<EndfieldSharingService>();
        services.AddSingleton(provider => new EndfieldMaintenanceService(
            provider.GetRequiredService<IEndfieldDistributionProvider>(),
            provider.GetRequiredService<EndfieldDownloadService>(),
            provider.GetRequiredService<EndfieldSharingService>(),
            LauncherDataPaths.ResolveDataDirectory(),
            new EndfieldArchiveService(Path.Combine(AppContext.BaseDirectory, "MaintenanceTools", "7za.exe"))));
        services.AddSingleton<EndfieldMaintenanceViewModel>();
        services.AddSingleton(new HoYoContentCodec(endfieldNetworkRuntime));
#if DEBUG
        if (HoYoMaintenanceUiFixture.Enabled)
            services.AddSingleton<IHoYoDistributionProvider>(new HoYoMaintenanceUiFixture());
        else
#endif
        services.AddSingleton<IHoYoDistributionProvider>(provider => new HoYoDistributionProvider(
            new HttpClient(new EndfieldNodeHttpHandler(endfieldNetworkRuntime)) { Timeout = TimeSpan.FromMinutes(3) },
            provider.GetRequiredService<HoYoContentCodec>()));
        services.AddSingleton(provider => new HoYoMaintenanceService(
            provider.GetRequiredService<IHoYoDistributionProvider>(),
            new EndfieldDownloadService(new HttpClient(
#if DEBUG
                HoYoMaintenanceUiFixture.Enabled ? new HoYoMaintenanceUiFixture() :
#endif
                new EndfieldNodeHttpHandler(endfieldNetworkRuntime))
            { Timeout = TimeSpan.FromMinutes(30) }, LauncherDataPaths.ResolveDataDirectory(), "hoyo", HoYoDistributionProvider.TrustedUri),
            provider.GetRequiredService<HoYoContentCodec>(), LauncherDataPaths.ResolveDataDirectory()));
        services.AddSingleton<HoYoMaintenanceViewModel>();
        services.AddSingleton(provider => new WallpaperUpdateService(
            new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseDefaultCredentials = false
            }) { Timeout = TimeSpan.FromSeconds(20) },
            LauncherDataPaths.ResolveDataDirectory(),
            provider.GetRequiredService<ILogger<WallpaperUpdateService>>()));

#if DEBUG
        if (EndfieldMaintenanceUiFixture.Enabled) services.AddSingleton<IEndfieldDisplayStore, EndfieldDisplayUiFixture>();
        else
#endif
        services.AddSingleton<IEndfieldDisplayStore, EndfieldRegistryDisplayStore>();
        services.AddSingleton(provider => new EndfieldDisplayService(provider.GetRequiredService<IEndfieldDisplayStore>(), LauncherDataPaths.ResolveDataDirectory()));
        services.AddSingleton(_ => new PlaytimeTrackingService(LauncherDataPaths.ResolveDataDirectory(), Path.Combine(AppContext.BaseDirectory, "Services", "WatchPlaytime.ps1")));
        services.AddSingleton<LauncherViewModel>();
        services.AddSingleton<GameLibraryViewModel>();
        services.AddSingleton<EndfieldCommunityViewModel>();
        services.AddSingleton<LogViewModel>();

        services.AddSingleton<MainWindow>();
        services.AddTransient<HomePage>();
        services.AddTransient<GameLibraryPage>();
        services.AddTransient<LaunchProfilesPage>();
        services.AddTransient<ToolsPage>();
        services.AddTransient<GachaPage>();
        services.AddTransient<PlayActivityPage>();
        services.AddTransient<EndfieldCommunityPage>();
        services.AddTransient<GameSettingsPage>();
        services.AddTransient<SettingsPage>();
        services.AddTransient<LogsPage>();
    }
}
