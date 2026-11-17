using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDeskHub.Core.Abstractions;

namespace RemoteDeskHub.HubHost;

/// <summary>
/// Starts the hub HTTPS listener and keeps it alive until the desktop console signals an explicit
/// shutdown. Agent heartbeat, monitoring and automation continue when the console is closed.
/// </summary>
public sealed class HubHostWorker(
    IHubServer hub,
    HubHostSignals signals,
    IAppPaths paths,
    IHostApplicationLifetime lifetime,
    ILogger<HubHostWorker> logger) : BackgroundService
{
    private string PortFilePath => Path.Combine(paths.RootDirectory, "hub-host.port");
    private string PidFilePath => Path.Combine(paths.RootDirectory, "hub-host.pid");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await hub.StartAsync(stoppingToken);
            Directory.CreateDirectory(paths.RootDirectory);
            await File.WriteAllTextAsync(PortFilePath, hub.BoundPort.ToString(), stoppingToken);
            await File.WriteAllTextAsync(PidFilePath, Environment.ProcessId.ToString(), stoppingToken);

            logger.LogInformation("Hub host listening on port {Port} (process {Pid}).", hub.BoundPort, Environment.ProcessId);
            signals.MarkReady();

            await signals.WaitForStopAsync(stoppingToken);
            logger.LogInformation("Hub host stop requested.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Hub host failed to start or run.");
            throw;
        }
        finally
        {
            signals.MarkStopped();
            TryDelete(PortFilePath);
            TryDelete(PidFilePath);

            try
            {
                await hub.StopAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Hub listener did not stop cleanly.");
            }

            // Returning from ExecuteAsync only stops this hosted service. The host process must be
            // told to end as well so the desktop can start it again on a new port.
            lifetime.StopApplication();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup; the next host start overwrites these files.
        }
    }
}
