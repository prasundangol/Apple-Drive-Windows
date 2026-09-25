using AppleDrive.Application.Interfaces;
using AppleDrive.Application.Services;
using AppleDrive.Application.Settings;
using AppleDrive.App.Services;
using AppleDrive.Infrastructure;
using AppleDrive.Infrastructure.Database;
using AppleDrive.Infrastructure.FileSystem;
using AppleDrive.Presentation.Services;
using AppleDrive.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Serilog;

namespace AppleDrive.App;

/// <summary>Composition root: configures logging and dependency injection, then opens the main window.</summary>
public partial class App : Microsoft.UI.Xaml.Application
{
    private MainWindow? _window;
    private ServiceProvider? _services;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    /// <summary>The application's service provider. Available after launch.</summary>
    public static IServiceProvider Services =>
        ((App)Current)._services ?? throw new InvalidOperationException("Services are not initialized yet.");

    public static MainWindow MainWindow =>
        ((App)Current)._window ?? throw new InvalidOperationException("The main window is not created yet.");

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var paths = new AppPaths();
        var logLevel = new SerilogLogLevelController();
        Log.Logger = SerilogConfiguration.Create(paths, logLevel);
        Log.Information("Apple Drive {Version} starting on {OS}", typeof(App).Assembly.GetName().Version, Environment.OSVersion);

        _services = ConfigureServices(paths, logLevel);
        logLevel.SetLevel(_services.GetRequiredService<ISettingsService>().Current.LogLevel);
        MigrateDatabase(_services.GetRequiredService<DatabaseMigrator>());

        _window = new MainWindow();
        _window.Closed += (_, _) => Shutdown();
        _window.Activate();
    }

    private static ServiceProvider ConfigureServices(IAppPaths paths, ILogLevelController logLevel)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddSerilog(dispose: false));
        services.AddAppleDriveCore(paths);

        services.AddSingleton(logLevel);
        services.AddSingleton<IUiDispatcher>(new UiDispatcher(DispatcherQueue.GetForCurrentThread()));
        services.AddSingleton<IShellServices, ShellServices>();
        services.AddSingleton<ImportSession>();

        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<DeviceStatusViewModel>();
        services.AddSingleton<PhoneScanViewModel>();
        services.AddSingleton<DestinationViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>
    /// Brings the media index schema up to date before any page uses it. This is fast (it only
    /// does real work after an app update), so it runs before the window opens.
    /// </summary>
    private static void MigrateDatabase(DatabaseMigrator migrator)
    {
        try
        {
            Task.Run(() => migrator.MigrateAsync(CancellationToken.None)).GetAwaiter().GetResult();
        }
        catch (DatabaseException exception)
        {
            // The app still opens; index-dependent actions then report a database error.
            Log.Error(exception, "Media index could not be prepared");
        }
    }

    private void Shutdown()
    {
        Log.Information("Apple Drive shutting down");
        try
        {
            // Closes the device connection and stops the device watcher.
            _services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled exception");
        Log.CloseAndFlush();
    }
}
