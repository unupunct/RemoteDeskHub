using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Desktop.Services;
using RemoteDeskHub.Desktop.ViewModels;
using RemoteDeskHub.Desktop.Views;
using RemoteDeskHub.Infrastructure;
using RemoteDeskHub.Infrastructure.Services;
using RemoteDeskHub.Networking;
using RemoteDeskHub.Shared;
using Serilog;

namespace RemoteDeskHub.Desktop;

/// <summary>
/// Application composition root. Builds the dependency injection container, initialises the
/// database and certificate authority, starts the hub HTTPS server and drives the
/// first-run wizard / login / main shell lifecycle.
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private bool _shuttingDown;
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;

    public static IServiceProvider Services =>
        ((App)Current)._host?.Services
        ?? throw new InvalidOperationException("The application host is not available.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            // A second console instance would race the SQLite database and the append-only audit
            // hash chain. Enforce one hub console per interactive session.
            _instanceMutex = new Mutex(initiallyOwned: true, @"Local\RemoteDeskHub.Desktop", out var createdNew);
            _ownsInstanceMutex = createdNew;
            if (!createdNew)
            {
                MessageBox.Show(
                    "RemoteDesk Hub is already running in this Windows session.",
                    "RemoteDesk Hub", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            _host = BuildHost();

            var paths = Services.GetRequiredService<IAppPaths>();
            ConfigureLogging(paths);

            // Initialise the database (including any staged restore) before hosted services
            // start, so background monitoring never races a migration or schema repair. When the
            // headless host is already running it has completed this work; do not re-enter the
            // restore path while its database connection is open.
            var initializer = Services.GetRequiredService<IDatabaseInitializer>();
            var hubServer = Services.GetRequiredService<IHubServer>();
            if (hubServer is not HubHostControl hostControl || !hostControl.IsRunning)
            {
                await initializer.InitializeAsync();
            }
            else
            {
                Log.Information("Using the already running headless hub host on port {Port}.", hostControl.BoundPort);
            }

            var settings = Services.GetRequiredService<ISettingsService>();
            await settings.SeedDefaultsAsync();

            await _host.StartAsync();
            await StartHubAsync();

            await RunShellLoopAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "RemoteDesk Hub failed to start.");
            MessageBox.Show(
                $"RemoteDesk Hub could not start.\n\n{ex.Message}\n\n" +
                $"Logs are written to the application data folder.",
                "RemoteDesk Hub", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (!_shuttingDown)
        {
            Shutdown();
        }
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Services
            .AddRemoteDeskHubCore()
            .AddRemoteDeskHubNetworking();

        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<IRemoteToolLauncher, RemoteToolLauncher>();

        // When the headless host is deployed next to the console, it owns the listener and the
        // background scheduler. The console controls it but never starts a second listener.
        if (UseHeadlessHubHost())
        {
            builder.Services.AddSingleton<IHubServer, HubHostControl>();
        }
        else
        {
            builder.Services.AddHostedService<HubBackgroundService>();
            builder.Services.AddHostedService<SharedFolderSyncService>();
        }

        // Windows and page view models.
        builder.Services.AddTransient<LoginWindow>();
        builder.Services.AddTransient<FirstRunWizard>();
        builder.Services.AddTransient<MainWindow>();

        builder.Services.AddTransient<ShellViewModel>();
        builder.Services.AddTransient<DashboardViewModel>();
        builder.Services.AddTransient<ComputersViewModel>();
        builder.Services.AddTransient<DiscoveryViewModel>();
        builder.Services.AddTransient<MonitoringViewModel>();
        builder.Services.AddTransient<InventoryViewModel>();
        builder.Services.AddTransient<ProcessesViewModel>();
        builder.Services.AddTransient<ServicesViewModel>();
        builder.Services.AddTransient<CommandsViewModel>();
        builder.Services.AddTransient<FileTransferViewModel>();
        builder.Services.AddTransient<RemoteAssistanceViewModel>();
        builder.Services.AddTransient<AutomationViewModel>();
        builder.Services.AddTransient<ReportsViewModel>();
        builder.Services.AddTransient<AuditLogViewModel>();
        builder.Services.AddTransient<UsersViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();

        return builder.Build();
    }

    private static bool UseHeadlessHubHost()
        => File.Exists(Path.Combine(AppContext.BaseDirectory, "RemoteDeskHub.HubHost.exe"));

    private static void ConfigureLogging(IAppPaths paths)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "hub-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private async Task StartHubAsync()
    {
        var hub = Services.GetRequiredService<IHubServer>();
        try
        {
            await hub.StartAsync();
            Log.Information("Hub server listening on port {Port}.", hub.BoundPort);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "The hub server could not start.");
            MessageBox.Show(
                $"The hub listener could not start: {ex.Message}\n\n" +
                "You can still use the console, adjust the hub port in Settings and restart the hub.",
                "Hub listener", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RunShellLoopAsync()
    {
        var initializer = Services.GetRequiredService<IDatabaseInitializer>();
        var session = Services.GetRequiredService<ISessionContext>();

        while (true)
        {
            if (await initializer.IsFirstRunAsync())
            {
                var wizard = Services.GetRequiredService<FirstRunWizard>();
                if (wizard.ShowDialog() != true)
                {
                    _shuttingDown = true;
                    return;
                }
            }

            var login = Services.GetRequiredService<LoginWindow>();
            if (login.ShowDialog() != true)
            {
                _shuttingDown = true;
                return;
            }

            var main = Services.GetRequiredService<MainWindow>();
            MainWindow = main;
            main.ShowDialog();

            if (main.SignedOut)
            {
                session.SignOut();
                continue;
            }

            _shuttingDown = true;
            return;
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                var hub = _host.Services.GetService<IHubServer>();
                if (hub is HubHostControl hostControl && !hostControl.StartedByThisConsole)
                {
                    Log.Information("Leaving the independently started headless hub host running.");
                }
                else if (hub is not null)
                {
                    await hub.StopAsync();
                }

                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error while shutting down.");
        }
        finally
        {
            if (_instanceMutex is not null)
            {
                if (_ownsInstanceMutex)
                {
                    try
                    {
                        _instanceMutex.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                        // The mutex was already released during abnormal shutdown.
                    }
                }

                _instanceMutex.Dispose();
                _instanceMutex = null;
            }

            await Log.CloseAndFlushAsync();
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception.");
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}",
            "RemoteDesk Hub", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
