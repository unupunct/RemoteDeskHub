using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Infrastructure.Services;

/// <summary>
/// Drives hub-side background work that does not belong to a page: offline detection, alert
/// evaluation for missing heartbeats, and due automation tasks.
/// </summary>
public sealed class HubBackgroundService(
    IMonitoringService monitoring,
    IAutomationService automation,
    ILogger<HubBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    private DateTime _lastMetricsPurgeUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await monitoring.RecordOfflineStatusesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Offline status evaluation failed.");
            }

            try
            {
                await automation.TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Automation scheduler tick failed.");
            }

            if (DateTime.UtcNow - _lastMetricsPurgeUtc >= TimeSpan.FromHours(24))
            {
                try
                {
                    var purged = await monitoring.PurgeOldMetricsAsync(HubConstants.AuditActorSystem, stoppingToken);
                    _lastMetricsPurgeUtc = DateTime.UtcNow;
                    if (purged > 0)
                    {
                        logger.LogInformation("Retention purge removed {Count} metric samples.", purged);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Metric retention purge failed.");
                }
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
