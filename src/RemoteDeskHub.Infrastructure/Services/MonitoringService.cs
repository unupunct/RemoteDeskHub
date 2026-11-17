using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class MonitoringService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    IAgentTransport transport,
    ISettingsService settings,
    ISessionContext session,
    IAppEvents events,
    IAuditService audit,
    IClock clock) : IMonitoringService
{
    private readonly SemaphoreSlim _ruleLock = new(1, 1);
    private IReadOnlyList<AlertRule> _ruleCache = [];
    private DateTime _ruleCacheUtc = DateTime.MinValue;

    public async Task IngestMetricsAsync(Computer computer, AgentMetricsDto metrics, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Computers.Include(c => c.Agent).FirstOrDefaultAsync(c => c.Id == computer.Id, cancellationToken);
        if (entity is null)
        {
            return;
        }

        var now = clock.UtcNow;
        var uptime = Math.Max(0, metrics.UptimeSeconds);
        var temperature = metrics.CpuTemperatureC is > -50 and < 200 ? metrics.CpuTemperatureC : null;

        db.PerformanceMetrics.Add(new PerformanceMetric
        {
            ComputerId = entity.Id,
            TimestampUtc = now,
            CpuPercent = Clamp(metrics.CpuPercent),
            MemoryPercent = Clamp(metrics.MemoryPercent),
            DiskPercent = Clamp(metrics.DiskPercent),
            NetworkInBps = Math.Max(0, metrics.NetworkInBps),
            NetworkOutBps = Math.Max(0, metrics.NetworkOutBps),
            CpuTemperatureC = temperature,
            UptimeSeconds = uptime,
            ProcessCount = metrics.ProcessCount
        });

        entity.CpuPercent = Clamp(metrics.CpuPercent);
        entity.MemoryPercent = Clamp(metrics.MemoryPercent);
        entity.DiskPercent = Clamp(metrics.DiskPercent);
        entity.CpuTemperatureC = temperature;
        entity.MemoryTotalBytes = metrics.MemoryTotalBytes;
        entity.UptimeSeconds = uptime;
        entity.LastBootUtc = now.AddSeconds(-uptime);
        entity.TotalDiskBytes = metrics.DiskTotalBytes;
        entity.FreeDiskBytes = metrics.DiskFreeBytes;
        entity.LastSeenUtc = now;
        entity.UpdatedUtc = now;
        if (entity.Agent is { State: AgentState.Active })
        {
            entity.Agent.LastSeenUtc = now;
            entity.Agent.HealthStatus = "Healthy";
        }

        var alerts = await db.Alerts.Where(a => a.ComputerId == entity.Id && a.IsActive).ToListAsync(cancellationToken);
        foreach (var offline in alerts.Where(a => a.MetricType is AlertMetricType.ComputerOffline or AlertMetricType.AgentUnavailable))
        {
            offline.IsActive = false;
            offline.ResolvedUtc = now;
        }

        var suppressed = new HashSet<AlertMetricType>();
        if (entity.IsManaged && entity.Agent is { State: AgentState.Active })
        {
            var rules = await GetRulesCachedAsync(cancellationToken);
            var applicable = rules.Where(r => r.Enabled && RuleMatches(r, entity)).ToList();
            var newlyRaised = new List<Alert>();

            // Cooldown prevents an alert from flapping back immediately after it resolves. Load
            // only the resolved alerts that could still be inside the largest cooldown window.
            var maxCooldownMinutes = applicable.Count == 0 ? 0 : applicable.Max(r => r.CooldownMinutes);
            var recentResolved = maxCooldownMinutes > 0
                ? await db.Alerts.AsNoTracking()
                    .Where(a => a.ComputerId == entity.Id && !a.IsActive && a.ResolvedUtc != null &&
                                a.ResolvedUtc > now.AddMinutes(-maxCooldownMinutes))
                    .ToListAsync(cancellationToken)
                : [];

            foreach (var group in applicable.GroupBy(r => r.MetricType))
            {
                var metricType = group.Key;
                if (metricType is AlertMetricType.ComputerOffline or AlertMetricType.AgentUnavailable or AlertMetricType.ServiceStopped or AlertMetricType.UnexpectedReboot)
                {
                    continue;
                }

                var freePercent = 100 - entity.DiskPercent;
                var triggered = group
                    .Select(rule => (Rule: rule, Value: metricType switch
                    {
                        AlertMetricType.CpuUsage => entity.CpuPercent,
                        AlertMetricType.MemoryUsage => entity.MemoryPercent,
                        AlertMetricType.DiskFree => freePercent,
                        AlertMetricType.Temperature => temperature ?? double.NaN,
                        AlertMetricType.NetworkTraffic => Math.Max(metrics.NetworkInBps, metrics.NetworkOutBps),
                        _ => double.NaN
                    }))
                    .Where(x => !double.IsNaN(x.Value) && (
                        metricType == AlertMetricType.DiskFree ? x.Value < x.Rule.Threshold : x.Value > x.Rule.Threshold))
                    .OrderByDescending(x => x.Rule.Severity)
                    .ToList();

                if (triggered.Count == 0)
                {
                    continue;
                }

                var winner = triggered[0];
                var existing = alerts.FirstOrDefault(a => a.MetricType == metricType && a.IsActive);
                if (existing is null && winner.Rule.CooldownMinutes > 0)
                {
                    var lastResolved = recentResolved
                        .Where(a => a.MetricType == metricType)
                        .MaxBy(a => a.ResolvedUtc);
                    if (lastResolved?.ResolvedUtc is { } resolvedUtc &&
                        now - resolvedUtc < TimeSpan.FromMinutes(winner.Rule.CooldownMinutes))
                    {
                        continue;
                    }
                }

                suppressed.Add(metricType);
                var title = metricType switch
                {
                    AlertMetricType.CpuUsage => "High CPU utilisation",
                    AlertMetricType.MemoryUsage => "High memory utilisation",
                    AlertMetricType.DiskFree => "Low free disk space",
                    AlertMetricType.Temperature => "High CPU temperature",
                    AlertMetricType.NetworkTraffic => "High network traffic",
                    _ => $"{metricType} threshold exceeded"
                };
                var message = metricType switch
                {
                    AlertMetricType.DiskFree => $"{entity.Name}: only {freePercent:F1}% disk space remains (threshold {winner.Rule.Threshold:F0}%).",
                    AlertMetricType.Temperature => $"{entity.Name}: CPU temperature is {temperature:F1} °C (threshold {winner.Rule.Threshold:F0} °C).",
                    AlertMetricType.CpuUsage => $"{entity.Name}: CPU usage is {entity.CpuPercent:F1}% (threshold {winner.Rule.Threshold:F0}%).",
                    AlertMetricType.MemoryUsage => $"{entity.Name}: memory usage is {entity.MemoryPercent:F1}% (threshold {winner.Rule.Threshold:F0}%).",
                    AlertMetricType.NetworkTraffic => $"{entity.Name}: network traffic is {Math.Max(metrics.NetworkInBps, metrics.NetworkOutBps):N0} B/s (threshold {winner.Rule.Threshold:N0} B/s).",
                    _ => $"{entity.Name}: threshold exceeded."
                };

                if (existing is null)
                {
                    existing = new Alert
                    {
                        ComputerId = entity.Id,
                        ComputerName = entity.Name,
                        MetricType = metricType,
                        Title = title,
                        Message = message,
                        DedupeKey = $"{entity.Id}:{metricType}",
                        Severity = winner.Rule.Severity,
                        IsActive = true,
                        CreatedUtc = now
                    };
                    db.Alerts.Add(existing);
                    alerts.Add(existing);
                    newlyRaised.Add(existing);
                }
                else if (winner.Rule.Severity > existing.Severity)
                {
                    existing.Severity = winner.Rule.Severity;
                    existing.Title = title;
                    existing.Message = message;
                    existing.CreatedUtc = now;
                    newlyRaised.Add(existing);
                }
            }

            foreach (var active in alerts.Where(a => a.IsActive &&
                         a.MetricType is AlertMetricType.CpuUsage or AlertMetricType.MemoryUsage or AlertMetricType.DiskFree or AlertMetricType.Temperature or AlertMetricType.NetworkTraffic))
            {
                if (!suppressed.Contains(active.MetricType))
                {
                    active.IsActive = false;
                    active.ResolvedUtc = now;
                }
            }

            foreach (var alert in newlyRaised)
            {
                events.RaiseNotification(new AppNotification(alert.Title, alert.Message, alert.Severity, entity.Id, now));
            }
        }

        entity.Status = alerts.Any(a => a.IsActive && a.Severity == AlertSeverity.Critical)
            ? ComputerStatus.Critical
            : alerts.Any(a => a.IsActive && a.Severity == AlertSeverity.Warning)
                ? ComputerStatus.Warning
                : entity.IsManaged ? ComputerStatus.Online : entity.Status;

        await db.SaveChangesAsync(cancellationToken);
        events.RaiseComputerChanged(entity.Id);
    }

    public async Task RecordOfflineStatusesAsync(CancellationToken cancellationToken = default)
    {
        var heartbeat = await settings.GetAsync(SettingsKeys.HeartbeatSeconds, HubConstants.DefaultHeartbeatSeconds, cancellationToken);
        var threshold = TimeSpan.FromSeconds(Math.Max(30, heartbeat * 3));
        var now = clock.UtcNow;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computers = await db.Computers.Include(c => c.Agent)
            .Where(c => c.IsManaged && c.Agent != null && c.Agent.State == AgentState.Active)
            .ToListAsync(cancellationToken);
        var activeAlerts = await db.Alerts.Where(a => a.IsActive && a.ComputerId != null).ToListAsync(cancellationToken);
        var rules = await GetRulesCachedAsync(cancellationToken);
        var changed = new List<Guid>();
        var notifications = new List<AppNotification>();

        foreach (var computer in computers)
        {
            var lastSeen = computer.LastSeenUtc ?? computer.Agent?.LastSeenUtc;
            var stale = lastSeen is null || now - lastSeen.Value > threshold;
            var activeOffline = activeAlerts.FirstOrDefault(a => a.ComputerId == computer.Id &&
                a.MetricType is AlertMetricType.ComputerOffline or AlertMetricType.AgentUnavailable);

            if (stale)
            {
                if (computer.Status != ComputerStatus.Offline)
                {
                    computer.Status = ComputerStatus.Offline;
                    computer.UpdatedUtc = now;
                    changed.Add(computer.Id);
                }

                if (computer.Agent is not null)
                {
                    computer.Agent.HealthStatus = "Agent not communicating";
                }

                if (activeOffline is null)
                {
                    var rule = rules.FirstOrDefault(r => r.Enabled && r.MetricType == AlertMetricType.ComputerOffline && RuleMatches(r, computer));
                    var alert = new Alert
                    {
                        ComputerId = computer.Id,
                        ComputerName = computer.Name,
                        MetricType = AlertMetricType.ComputerOffline,
                        Severity = rule?.Severity ?? AlertSeverity.Warning,
                        Title = "Computer offline",
                        Message = $"{computer.Name} has not communicated since {lastSeen?.ToLocalTime():g}.",
                        DedupeKey = $"{computer.Id}:{AlertMetricType.ComputerOffline}",
                        IsActive = true,
                        CreatedUtc = now
                    };
                    db.Alerts.Add(alert);
                    notifications.Add(new AppNotification(alert.Title, alert.Message, alert.Severity, computer.Id, now));
                }
            }
            else if (computer.Status == ComputerStatus.Offline)
            {
                computer.Status = computer.Agent?.State == AgentState.PendingApproval ? ComputerStatus.PendingApproval : ComputerStatus.Online;
                computer.UpdatedUtc = now;
                changed.Add(computer.Id);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var id in changed)
        {
            events.RaiseComputerChanged(id);
        }

        foreach (var notification in notifications)
        {
            events.RaiseNotification(notification);
        }
    }

    public async Task<IReadOnlyList<MonitoringSeries>> GetSeriesAsync(IReadOnlyCollection<Guid> computerIds, DateTime fromUtc, DateTime toUtc, int maxPointsPerComputer, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewMonitoring);
        if (computerIds.Count == 0)
        {
            return [];
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var names = await db.Computers.AsNoTracking().Where(c => computerIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var raw = await db.PerformanceMetrics.AsNoTracking()
            .Where(m => computerIds.Contains(m.ComputerId) && m.TimestampUtc >= fromUtc && m.TimestampUtc <= toUtc)
            .OrderBy(m => m.TimestampUtc)
            .ToListAsync(cancellationToken);

        maxPointsPerComputer = Math.Clamp(maxPointsPerComputer, 2, 5000);
        var result = new List<MonitoringSeries>();
        foreach (var group in raw.GroupBy(m => m.ComputerId))
        {
            var samples = group.ToList();
            var bucketSize = Math.Max(1, (int)Math.Ceiling(samples.Count / (double)maxPointsPerComputer));
            var series = new MonitoringSeries
            {
                ComputerId = group.Key,
                ComputerName = names.GetValueOrDefault(group.Key, "Unknown computer")
            };

            for (var i = 0; i < samples.Count; i += bucketSize)
            {
                var bucket = samples.Skip(i).Take(bucketSize).ToList();
                series.Samples.Add(new MetricSample
                {
                    TimestampUtc = bucket[^1].TimestampUtc,
                    CpuPercent = bucket.Average(m => m.CpuPercent),
                    MemoryPercent = bucket.Average(m => m.MemoryPercent),
                    DiskPercent = bucket.Average(m => m.DiskPercent),
                    NetworkInBps = bucket.Average(m => m.NetworkInBps),
                    NetworkOutBps = bucket.Average(m => m.NetworkOutBps),
                    CpuTemperatureC = bucket.Any(m => m.CpuTemperatureC.HasValue)
                        ? bucket.Where(m => m.CpuTemperatureC.HasValue).Average(m => m.CpuTemperatureC!.Value)
                        : null,
                    UptimeSeconds = bucket[^1].UptimeSeconds
                });
            }

            result.Add(series);
        }

        return result;
    }

    public async Task<AgentMetricsDto?> GetLiveMetricsAsync(Guid computerId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewMonitoring);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computer = await db.Computers.AsNoTracking().Include(c => c.Agent)
            .FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken);
        if (computer is null)
        {
            return null;
        }

        try
        {
            return await transport.GetMetricsAsync(computer, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<AlertView>> GetAlertsAsync(bool activeOnly, int limit, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewMonitoring);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Alerts.AsNoTracking().AsQueryable();
        if (activeOnly)
        {
            query = query.Where(a => a.IsActive);
        }

        return await query.OrderByDescending(a => a.CreatedUtc).Take(Math.Clamp(limit, 1, 5000))
            .Select(a => new AlertView
            {
                Id = a.Id,
                ComputerId = a.ComputerId,
                ComputerName = a.ComputerName,
                Severity = a.Severity,
                MetricType = a.MetricType,
                Title = a.Title,
                Message = a.Message,
                IsActive = a.IsActive,
                CreatedUtc = a.CreatedUtc,
                AcknowledgedUtc = a.AcknowledgedUtc,
                AcknowledgedBy = a.AcknowledgedBy
            }).ToListAsync(cancellationToken);
    }

    public async Task<OperationResult> AcknowledgeAlertAsync(string actor, Guid alertId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageAlerts, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == alertId, cancellationToken);
        if (alert is null)
        {
            return OperationResult.Fail("Alert not found.");
        }

        alert.AcknowledgedUtc = clock.UtcNow;
        alert.AcknowledgedBy = actor;
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, AuditOperations.AlertAcknowledged, alert.ComputerName, alert.ComputerId,
            details: alert.Title, cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok("Alert acknowledged.");
    }

    public async Task<OperationResult> ResolveAlertAsync(string actor, Guid alertId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageAlerts, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == alertId, cancellationToken);
        if (alert is null)
        {
            return OperationResult.Fail("Alert not found.");
        }

        alert.IsActive = false;
        alert.ResolvedUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, "Alert.Resolved", alert.ComputerName, alert.ComputerId, details: alert.Title, cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok("Alert resolved.");
    }

    public async Task<IReadOnlyList<AlertRuleView>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewMonitoring);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rules = await db.AlertRules.AsNoTracking().OrderBy(r => r.MetricType).ToListAsync(cancellationToken);
        var groups = await db.ComputerGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        var computers = await db.Computers.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        return rules.Select(r => new AlertRuleView
        {
            Id = r.Id,
            Name = r.Name,
            MetricType = r.MetricType,
            Threshold = r.Threshold,
            Severity = r.Severity,
            Enabled = r.Enabled,
            ScopeComputerId = r.ScopeComputerId,
            ScopeGroupId = r.ScopeGroupId,
            ScopeName = r.ScopeComputerId.HasValue
                ? computers.GetValueOrDefault(r.ScopeComputerId.Value)
                : r.ScopeGroupId.HasValue ? groups.GetValueOrDefault(r.ScopeGroupId.Value) : "All computers",
            CooldownMinutes = r.CooldownMinutes
        }).ToList();
    }

    public async Task<OperationResult> SaveRuleAsync(string actor, AlertRuleView rule, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageAlerts, actor);
        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            return OperationResult.Fail("Rule name is required.");
        }

        var limitError = rule.MetricType switch
        {
            AlertMetricType.CpuUsage or AlertMetricType.MemoryUsage or AlertMetricType.DiskFree => rule.Threshold is < 0 or > 100 ? "Threshold must be between 0 and 100 percent." : null,
            AlertMetricType.Temperature => rule.Threshold is < -20 or > 150 ? "Temperature threshold must be between -20 and 150 °C." : null,
            AlertMetricType.NetworkTraffic => rule.Threshold < 0 ? "Network threshold cannot be negative." : null,
            _ => null
        };
        if (limitError is not null)
        {
            return OperationResult.Fail(limitError);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        AlertRule entity;
        if (rule.Id == Guid.Empty)
        {
            entity = new AlertRule { CreatedUtc = clock.UtcNow };
            db.AlertRules.Add(entity);
        }
        else
        {
            var found = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == rule.Id, cancellationToken);
            if (found is null)
            {
                return OperationResult.Fail("Alert rule not found.");
            }

            entity = found;
        }

        entity.Name = rule.Name.Trim();
        entity.MetricType = rule.MetricType;
        entity.Threshold = rule.Threshold;
        entity.Severity = rule.Severity;
        entity.Enabled = rule.Enabled;
        entity.ScopeComputerId = rule.ScopeComputerId;
        entity.ScopeGroupId = rule.ScopeGroupId;
        entity.CooldownMinutes = Math.Clamp(rule.CooldownMinutes, 0, 24 * 60);
        await db.SaveChangesAsync(cancellationToken);
        InvalidateRuleCache();
        await audit.LogAsync(actor, "AlertRule.Saved", details: $"Rule '{entity.Name}' saved.", cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok("Alert rule saved.");
    }

    /// <summary>
    /// Applies the setup/settings defaults to the global alert rules. Called by the first-run
    /// wizard before a signed-in session exists and by the Settings page after validation.
    /// </summary>
    public async Task<OperationResult> ApplyDefaultThresholdsAsync(string actor, double cpu, double memory,
        double disk, double temperature, int cooldownMinutes, CancellationToken cancellationToken = default)
    {
        if (cpu is <= 0 or > 100 || memory is <= 0 or > 100 || disk is <= 0 or > 100)
        {
            return OperationResult.Fail("CPU, memory and disk thresholds must be between 1 and 100.");
        }

        if (temperature is < -20 or > 150)
        {
            return OperationResult.Fail("Temperature threshold must be between -20 and 150 degrees Celsius.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rules = await db.AlertRules
            .Where(r => r.ScopeComputerId == null && r.ScopeGroupId == null)
            .ToListAsync(cancellationToken);

        foreach (var rule in rules)
        {
            rule.Threshold = rule.MetricType switch
            {
                AlertMetricType.CpuUsage => cpu,
                AlertMetricType.MemoryUsage => memory,
                AlertMetricType.DiskFree => disk,
                AlertMetricType.Temperature => temperature,
                _ => rule.Threshold
            };
            rule.CooldownMinutes = Math.Clamp(cooldownMinutes, 0, 24 * 60);
        }

        await db.SaveChangesAsync(cancellationToken);
        InvalidateRuleCache();
        await audit.LogAsync(actor, "AlertRule.DefaultsApplied",
            details: $"Default thresholds applied: CPU {cpu}, memory {memory}, disk {disk}, temperature {temperature}, cooldown {cooldownMinutes} min.",
            cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok(rules.Count == 0
            ? "No global alert rules were found to update."
            : $"Default thresholds applied to {rules.Count} global rule(s).");
    }

    public async Task<OperationResult> DeleteRuleAsync(string actor, Guid ruleId, CancellationToken cancellationToken = default)
    {        AccessGuard.Require(session, Permission.ManageAlerts, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rule = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == ruleId, cancellationToken);
        if (rule is null)
        {
            return OperationResult.Fail("Alert rule not found.");
        }

        db.AlertRules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
        InvalidateRuleCache();
        await audit.LogAsync(actor, "AlertRule.Deleted", details: $"Rule '{rule.Name}' deleted.", cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok("Alert rule deleted.");
    }

    public async Task<int> PurgeOldMetricsAsync(string? actor = null, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageSettings, actor);
        var days = await settings.GetAsync(SettingsKeys.MetricsRetentionDays, 30, cancellationToken);
        var cutoff = clock.UtcNow.AddDays(-Math.Clamp(days, 1, 3650));
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.PerformanceMetrics.Where(m => m.TimestampUtc < cutoff).ExecuteDeleteAsync(cancellationToken);
        if (rows > 0)
        {
            await audit.LogAsync(actor ?? session.CurrentUser?.UserName ?? HubConstants.AuditActorSystem,
                "Monitoring.MetricsPurged", result: "Success",
                details: $"Deleted {rows} metric samples older than {cutoff:O} (retention {days} days).",
                cancellationToken: cancellationToken);
        }

        return rows;
    }

    public async Task<ReportFile> ExportHistoryAsync(IReadOnlyCollection<Guid> computerIds, DateTime fromUtc, DateTime toUtc, string format, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewReports);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.PerformanceMetrics.AsNoTracking().Where(m => m.TimestampUtc >= fromUtc && m.TimestampUtc <= toUtc);
        if (computerIds.Count > 0)
        {
            query = query.Where(m => computerIds.Contains(m.ComputerId));
        }

        var names = await db.Computers.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var rows = await query.OrderBy(m => m.TimestampUtc).Take(200_000).ToListAsync(cancellationToken);
        var data = rows.Select(m => new[]
        {
            names.GetValueOrDefault(m.ComputerId, "Unknown"),
            m.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            m.CpuPercent.ToString("F1"),
            m.MemoryPercent.ToString("F1"),
            m.DiskPercent.ToString("F1"),
            m.NetworkInBps.ToString("F0"),
            m.NetworkOutBps.ToString("F0"),
            m.CpuTemperatureC?.ToString("F1") ?? string.Empty,
            TimeSpan.FromSeconds(m.UptimeSeconds).ToString(@"d\.hh\:mm\:ss")
        }).ToList();

        return ReportWriter.Create("performance-history", format, "Performance History",
            ["Computer", "Timestamp", "CPU %", "Memory %", "Disk %", "Network in (B/s)", "Network out (B/s)", "Temperature (°C)", "Uptime"], data);
    }

    public Task EvaluateRulesForAsync(Computer computer, AgentMetricsDto metrics, CancellationToken cancellationToken = default)
        => IngestMetricsAsync(computer, metrics, cancellationToken);

    private async Task<IReadOnlyList<AlertRule>> GetRulesCachedAsync(CancellationToken cancellationToken)
    {
        if (clock.UtcNow - _ruleCacheUtc < TimeSpan.FromSeconds(30) && _ruleCache.Count > 0)
        {
            return _ruleCache;
        }

        await _ruleLock.WaitAsync(cancellationToken);
        try
        {
            if (clock.UtcNow - _ruleCacheUtc < TimeSpan.FromSeconds(30) && _ruleCache.Count > 0)
            {
                return _ruleCache;
            }

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            _ruleCache = await db.AlertRules.AsNoTracking().Where(r => r.Enabled).ToListAsync(cancellationToken);
            _ruleCacheUtc = clock.UtcNow;
            return _ruleCache;
        }
        finally
        {
            _ruleLock.Release();
        }
    }

    private void InvalidateRuleCache()
    {
        _ruleCacheUtc = DateTime.MinValue;
    }

    private static bool RuleMatches(AlertRule rule, Computer computer)
    {
        if (rule.ScopeComputerId.HasValue && rule.ScopeComputerId != computer.Id)
        {
            return false;
        }

        if (rule.ScopeGroupId.HasValue && rule.ScopeGroupId != computer.GroupId)
        {
            return false;
        }

        return true;
    }

    private static double Clamp(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 100);
}
