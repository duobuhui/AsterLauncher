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
        }
        catch (Exception exception)
        {
            _services.GetRequiredService<ILogger<App>>().LogCritical(exception, "Application startup failed");
            throw;
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

        services.AddSingleton<LauncherViewModel>();
        services.AddSingleton<GameLibraryViewModel>();
        services.AddSingleton<LogViewModel>();

        services.AddSingleton<MainWindow>();
        services.AddTransient<HomePage>();
        services.AddTransient<GameLibraryPage>();
        services.AddTransient<LaunchProfilesPage>();
        services.AddTransient<ToolsPage>();
        services.AddTransient<GachaPage>();
        services.AddTransient<GameSettingsPage>();
        services.AddTransient<SettingsPage>();
        services.AddTransient<LogsPage>();
    }
}
