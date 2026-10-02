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

    public App()
    {
        InitializeComponent();
        LauncherDataPaths.MigrateLegacyBundleDataIfNeeded();
        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();
    }

    public static Window MainWindow { get; private set; } = null!;

    public IServiceProvider Services => _services;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await _services.GetRequiredService<LauncherViewModel>().InitializeAsync();
            _window = _services.GetRequiredService<MainWindow>();
            MainWindow = _window;
            _window.Activate();
            _ = RefreshWallpapersAsync();
        }
        catch (Exception exception)
        {
            _services.GetRequiredService<ILogger<App>>().LogCritical(exception, "Application startup failed");
            throw;
        }
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
        foreach (var adapter in BuiltInGameCatalog.CreateAdapters())
        {
            services.AddSingleton(typeof(IGameAdapter), adapter);
        }
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<IUigfArchiveService, UigfArchiveService>();
        services.AddSingleton<IEndfieldGachaArchiveService, EndfieldGachaArchiveService>();
        services.AddSingleton<LauncherUpdateService>();
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
        services.AddSingleton<LauncherViewModel>();
        services.AddSingleton<GameLibraryViewModel>();
        services.AddSingleton<LogViewModel>();

        services.AddSingleton<MainWindow>();
        services.AddTransient<HomePage>();
        services.AddTransient<GameLibraryPage>();
        services.AddTransient<LaunchProfilesPage>();
        services.AddTransient<ToolsPage>();
        services.AddTransient<GachaPage>();
        services.AddTransient<PlayActivityPage>();
        services.AddTransient<GameSettingsPage>();
        services.AddTransient<SettingsPage>();
        services.AddTransient<LogsPage>();
    }
}
