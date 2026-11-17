using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.HubHost;
using RemoteDeskHub.Infrastructure;
using RemoteDeskHub.Infrastructure.Services;
using RemoteDeskHub.Networking;
using Serilog;

internal static class Program
{
    private const string MutexName = @"Local\RemoteDeskHub.HubHost";

    private static async Task<int> Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: false, MutexName);
        var ownsMutex = false;
        try
        {
            ownsMutex = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            ownsMutex = true;
        }

        if (!ownsMutex)
        {
            return 0;
        }

        var dataRoot = GetOption(args, "--data");
        var paths = new AppPaths(dataRoot);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "hub-host-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddSerilog(Log.Logger, dispose: false);
            builder.Services
                .AddRemoteDeskHubCore(dataRoot)
                .AddRemoteDeskHubNetworking();

            builder.Services.AddSingleton<HubHostSignals>();
            builder.Services.AddHostedService<HubHostWorker>();
            builder.Services.AddHostedService<HubBackgroundService>();
            builder.Services.AddHostedService<SharedFolderSyncService>();

            using var host = builder.Build();

            var initializer = host.Services.GetRequiredService<IDatabaseInitializer>();
            await initializer.InitializeAsync();

            var settings = host.Services.GetRequiredService<ISettingsService>();
            await settings.SeedDefaultsAsync();

            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "RemoteDesk Hub host terminated unexpectedly.");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
            if (ownsMutex)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Already released during abnormal shutdown.
                }
            }
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
