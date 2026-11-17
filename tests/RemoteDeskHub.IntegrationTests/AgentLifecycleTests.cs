using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.IntegrationTests;

public sealed class AgentLifecycleTests
{
    private static HeartbeatRequestDto Heartbeat(Computer computer, Agent agent, AgentMetricsDto? metrics = null) =>
        new()
        {
            AgentId = agent.Id,
            ComputerId = computer.Id,
            MachineName = computer.Name,
            AgentVersion = "1.2.3",
            ListeningPort = 7444,
            Nonce = Guid.NewGuid().ToString("N"),
            Metrics = metrics
        };

    [Fact]
    public async Task Heartbeat_MarksComputerOnline_AndReturnsRuntimeConfig()
    {
        await using var hub = await TestHub.CreateAsync();
        var (computer, agent) = await hub.CreateApprovedComputerAsync(certificateThumbprint: "A" + new string('B', 39));

        var response = await hub.Get<IAgentLifecycleService>()
            .ProcessHeartbeatAsync(Heartbeat(computer, agent), agent.CertificateThumbprint, "192.168.1.10");

        Assert.True(response.Approved);
        Assert.False(response.Revoked);
        Assert.True(response.Config.HeartbeatSeconds >= 3);
        Assert.NotEmpty(response.Config.AllowedFileRoots);

        await using var db = await hub.Get<IDbContextFactory<RemoteDeskDbContext>>().CreateDbContextAsync();
        var stored = await db.Computers.Include(c => c.Agent).SingleAsync(c => c.Id == computer.Id);
        Assert.Equal("1.2.3", stored.AgentVersion);
        Assert.Equal("Healthy", stored.Agent!.HealthStatus);
        Assert.NotNull(stored.LastSeenUtc);
        Assert.Equal("192.168.1.10", stored.IpAddress);
    }

    [Fact]
    public async Task Heartbeat_WithoutCertificate_IsRejected()
    {
        await using var hub = await TestHub.CreateAsync();
        var (computer, agent) = await hub.CreateApprovedComputerAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            hub.Get<IAgentLifecycleService>().ProcessHeartbeatAsync(Heartbeat(computer, agent), null, "192.168.1.10"));
    }

    [Fact]
    public async Task Heartbeat_WithUnknownCertificate_IsRejected()
    {
        await using var hub = await TestHub.CreateAsync();
        var (computer, agent) = await hub.CreateApprovedComputerAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            hub.Get<IAgentLifecycleService>().ProcessHeartbeatAsync(Heartbeat(computer, agent), "CD" + new string('E', 38), "192.168.1.10"));
    }

    [Fact]
    public async Task Heartbeat_WithMismatchedIdentity_IsRejected()
    {
        await using var hub = await TestHub.CreateAsync();
        var (computer, agent) = await hub.CreateApprovedComputerAsync(certificateThumbprint: "AB" + new string('C', 38));
        var (otherComputer, otherAgent) = await hub.CreateApprovedComputerAsync("OTHER-PC", "192.168.1.11", "DD" + new string('E', 38));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            hub.Get<IAgentLifecycleService>().ProcessHeartbeatAsync(
                Heartbeat(otherComputer, otherAgent), agent.CertificateThumbprint, "192.168.1.10"));
    }

    [Fact]
    public async Task Telemetry_RequiresApprovedAgent()
    {
        await using var hub = await TestHub.CreateAsync();
        var (computer, agent) = await hub.CreateApprovedComputerAsync(certificateThumbprint: "AC" + new string('D', 38));
        var lifecycle = hub.Get<IAgentLifecycleService>();

        var accepted = await lifecycle.RecordTelemetryAsync(new TelemetryBatchDto
        {
            AgentId = agent.Id,
            Nonce = Guid.NewGuid().ToString("N"),
            Samples = [new AgentMetricsDto { CpuPercent = 20 }]
        }, agent.CertificateThumbprint);

        var rejected = await lifecycle.RecordTelemetryAsync(new TelemetryBatchDto
        {
            AgentId = Guid.NewGuid(),
            Nonce = Guid.NewGuid().ToString("N"),
            Samples = [new AgentMetricsDto { CpuPercent = 20 }]
        }, agent.CertificateThumbprint);

        Assert.True(accepted.Success, accepted.Error);
        Assert.False(rejected.Success);

        await using var db = await hub.Get<IDbContextFactory<RemoteDeskDbContext>>().CreateDbContextAsync();
        Assert.True(await db.PerformanceMetrics.AnyAsync(m => m.ComputerId == computer.Id));
    }

    [Fact]
    public async Task MetricsIngestion_StoresSampleAndUpdatesComputer()
    {
        await using var hub = await TestHub.CreateAsync();
        var (computer, _) = await hub.CreateApprovedComputerAsync();

        await hub.Get<IMonitoringService>().IngestMetricsAsync(computer, new AgentMetricsDto
        {
            CpuPercent = 41.5,
            MemoryPercent = 55,
            DiskPercent = 72,
            MemoryTotalBytes = 32L * 1024 * 1024 * 1024,
            DiskTotalBytes = 512L * 1024 * 1024 * 1024,
            DiskFreeBytes = 100L * 1024 * 1024 * 1024,
            UptimeSeconds = 86400,
            ProcessCount = 123,
            CpuTemperatureC = 52
        });

        await using var db = await hub.Get<IDbContextFactory<RemoteDeskDbContext>>().CreateDbContextAsync();
        var stored = await db.Computers.SingleAsync(c => c.Id == computer.Id);
        Assert.Equal(41.5, stored.CpuPercent, 3);
        Assert.Equal(55, stored.MemoryPercent, 3);
        Assert.Equal(52, stored.CpuTemperatureC);
        Assert.Equal(1, await db.PerformanceMetrics.CountAsync(m => m.ComputerId == computer.Id));
        Assert.NotNull(stored.LastBootUtc);
    }

    [Fact]
    public async Task HighCpu_RaisesAlert_Once_AndResolvesOnRecovery()
    {
        await using var hub = await TestHub.CreateAsync();
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        var monitoring = hub.Get<IMonitoringService>();

        await monitoring.IngestMetricsAsync(computer, new AgentMetricsDto { CpuPercent = 99, MemoryPercent = 10, DiskPercent = 10 });
        await monitoring.IngestMetricsAsync(computer, new AgentMetricsDto { CpuPercent = 98, MemoryPercent = 10, DiskPercent = 10 });

        var active = await monitoring.GetAlertsAsync(activeOnly: true, 50);
        var cpuAlerts = active.Where(a => a.MetricType == AlertMetricType.CpuUsage).ToList();
        Assert.Single(cpuAlerts);
        Assert.Equal("TEST-PC-01", cpuAlerts[0].ComputerName);

        await monitoring.IngestMetricsAsync(computer, new AgentMetricsDto { CpuPercent = 12, MemoryPercent = 10, DiskPercent = 10 });

        var after = await monitoring.GetAlertsAsync(activeOnly: true, 50);
        Assert.DoesNotContain(after, a => a.MetricType == AlertMetricType.CpuUsage);

        var history = await monitoring.GetAlertsAsync(activeOnly: false, 50);
        Assert.Contains(history, a => a.MetricType == AlertMetricType.CpuUsage);
    }

    [Fact]
    public async Task Alert_Cooldown_SuppressesImmediateRetrigger()
    {
        await using var hub = await TestHub.CreateAsync();
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        var monitoring = hub.Get<IMonitoringService>();

        var rule = (await monitoring.GetRulesAsync()).Single(r =>
            r.MetricType == AlertMetricType.CpuUsage && r.ScopeComputerId is null && r.ScopeGroupId is null);
        var updated = new AlertRuleView
        {
            Id = rule.Id,
            Name = rule.Name,
            MetricType = rule.MetricType,
            Threshold = rule.Threshold,
            Severity = rule.Severity,
            Enabled = rule.Enabled,
            ScopeComputerId = rule.ScopeComputerId,
            ScopeGroupId = rule.ScopeGroupId,
            CooldownMinutes = 60
        };
        Assert.True((await monitoring.SaveRuleAsync(TestHub.AdminUserName, updated)).Success);

        await monitoring.IngestMetricsAsync(computer, new AgentMetricsDto { CpuPercent = 99, DiskPercent = 50 });
        await monitoring.IngestMetricsAsync(computer, new AgentMetricsDto { CpuPercent = 10, DiskPercent = 50 });
        Assert.DoesNotContain(await monitoring.GetAlertsAsync(true, 50),
            a => a.MetricType == AlertMetricType.CpuUsage);

        hub.Clock.Advance(TimeSpan.FromMinutes(1));
        await monitoring.IngestMetricsAsync(computer, new AgentMetricsDto { CpuPercent = 99, DiskPercent = 50 });
        Assert.DoesNotContain(await monitoring.GetAlertsAsync(true, 50),
            a => a.MetricType == AlertMetricType.CpuUsage);

        hub.Clock.Advance(TimeSpan.FromMinutes(61));
        await monitoring.IngestMetricsAsync(computer, new AgentMetricsDto { CpuPercent = 99, DiskPercent = 50 });
        Assert.Contains(await monitoring.GetAlertsAsync(true, 50),
            a => a.MetricType == AlertMetricType.CpuUsage);
    }

    [Fact]
    public async Task Alert_AcknowledgeAndResolve()
    {
        await using var hub = await TestHub.CreateAsync();
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        var monitoring = hub.Get<IMonitoringService>();
        await monitoring.IngestMetricsAsync(computer, new AgentMetricsDto { CpuPercent = 99, DiskPercent = 5 });

        var alert = (await monitoring.GetAlertsAsync(true, 50)).First();

        Assert.True((await monitoring.AcknowledgeAlertAsync(TestHub.AdminUserName, alert.Id)).Success);
        Assert.True((await monitoring.ResolveAlertAsync(TestHub.AdminUserName, alert.Id)).Success);

        Assert.DoesNotContain(await monitoring.GetAlertsAsync(true, 50), a => a.Id == alert.Id);
    }

    [Fact]
    public async Task StaleHeartbeat_MarksComputerOfflineAndRaisesAlert()
    {
        await using var hub = await TestHub.CreateAsync();
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        var monitoring = hub.Get<IMonitoringService>();

        await monitoring.IngestMetricsAsync(computer, new AgentMetricsDto { CpuPercent = 5, DiskPercent = 50 });
        hub.Clock.Advance(TimeSpan.FromMinutes(10));

        await monitoring.RecordOfflineStatusesAsync();

        await using var db = await hub.Get<IDbContextFactory<RemoteDeskDbContext>>().CreateDbContextAsync();
        var stored = await db.Computers.SingleAsync(c => c.Id == computer.Id);
        Assert.Equal(ComputerStatus.Offline, stored.Status);
        Assert.Contains(await monitoring.GetAlertsAsync(true, 50),
            a => a.MetricType == AlertMetricType.ComputerOffline && a.ComputerId == computer.Id);
    }

    [Fact]
    public async Task Series_ReturnsBucketedHistory()
    {
        await using var hub = await TestHub.CreateAsync();
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        var monitoring = hub.Get<IMonitoringService>();

        for (var i = 0; i < 10; i++)
        {
            hub.Clock.Advance(TimeSpan.FromSeconds(30));
            await monitoring.IngestMetricsAsync(computer, new AgentMetricsDto { CpuPercent = 10 + i, DiskPercent = 50 });
        }

        var series = await monitoring.GetSeriesAsync([computer.Id],
            hub.Clock.UtcNow.AddHours(-1), hub.Clock.UtcNow.AddMinutes(1), 5);

        Assert.Single(series);
        Assert.True(series[0].Samples.Count <= 5);
        Assert.NotEmpty(series[0].Samples);
    }

    [Fact]
    public async Task UnmanagedComputers_AreNotEvaluatedForAlerts()
    {
        await using var hub = await TestHub.CreateAsync();
        var (managed, _) = await hub.CreateApprovedComputerAsync();
        var monitoring = hub.Get<IMonitoringService>();

        Computer unmanaged;
        await using (var db = await hub.Get<IDbContextFactory<RemoteDeskDbContext>>().CreateDbContextAsync())
        {
            unmanaged = new Computer
            {
                Name = "UNMANAGED-PC",
                IpAddress = "192.168.1.77",
                IsManaged = false,
                Status = ComputerStatus.Online
            };
            db.Computers.Add(unmanaged);
            await db.SaveChangesAsync();
        }

        await monitoring.IngestMetricsAsync(unmanaged, new AgentMetricsDto { CpuPercent = 99, DiskPercent = 10 });

        var alerts = await monitoring.GetAlertsAsync(true, 50);
        Assert.DoesNotContain(alerts, a => a.ComputerId == unmanaged.Id);
        Assert.NotNull(managed);
    }

    [Fact]
    public async Task DefaultThresholds_UpdateGlobalRules()
    {
        await using var hub = await TestHub.CreateAsync();
        var monitoring = hub.Get<IMonitoringService>();

        var result = await monitoring.ApplyDefaultThresholdsAsync("admin", 70, 75, 20, 70, 45);

        Assert.True(result.Success, result.Error);
        var rules = await monitoring.GetRulesAsync();
        var cpu = Assert.Single(rules, r =>
            r.MetricType == AlertMetricType.CpuUsage && r.ScopeComputerId is null && r.ScopeGroupId is null);
        Assert.Equal(70, cpu.Threshold);
        Assert.Equal(45, cpu.CooldownMinutes);
    }

    [Fact]
    public async Task Heartbeat_WithReplayedNonce_IsRejected()
    {
        await using var hub = await TestHub.CreateAsync();
        var (computer, agent) = await hub.CreateApprovedComputerAsync(certificateThumbprint: "AF" + new string('1', 38));
        var lifecycle = hub.Get<IAgentLifecycleService>();
        var heartbeat = Heartbeat(computer, agent);

        await lifecycle.ProcessHeartbeatAsync(heartbeat, agent.CertificateThumbprint, "192.168.1.10");

        await Assert.ThrowsAsync<RemoteDeskHub.Core.Security.ReplayDetectedException>(() =>
            lifecycle.ProcessHeartbeatAsync(heartbeat, agent.CertificateThumbprint, "192.168.1.10"));
    }

    [Fact]
    public async Task Heartbeat_WithStaleTimestamp_IsRejected()
    {
        await using var hub = await TestHub.CreateAsync();
        var (computer, agent) = await hub.CreateApprovedComputerAsync(certificateThumbprint: "AF" + new string('2', 38));
        var heartbeat = Heartbeat(computer, agent) with
        {
            TimestampUtc = new DateTimeOffset(hub.Clock.UtcNow.AddMinutes(-10), TimeSpan.Zero)
        };

        await Assert.ThrowsAsync<RemoteDeskHub.Core.Security.ReplayDetectedException>(() =>
            hub.Get<IAgentLifecycleService>()
                .ProcessHeartbeatAsync(heartbeat, agent.CertificateThumbprint, "192.168.1.10"));
    }

    [Fact]
    public async Task Telemetry_WithReplayedNonce_IsRejected()
    {
        await using var hub = await TestHub.CreateAsync();
        var (_, agent) = await hub.CreateApprovedComputerAsync(certificateThumbprint: "AF" + new string('3', 38));
        var lifecycle = hub.Get<IAgentLifecycleService>();
        var batch = new TelemetryBatchDto
        {
            AgentId = agent.Id,
            Nonce = Guid.NewGuid().ToString("N"),
            Samples = [new AgentMetricsDto { CpuPercent = 20 }]
        };

        Assert.True((await lifecycle.RecordTelemetryAsync(batch, agent.CertificateThumbprint)).Success);
        Assert.False((await lifecycle.RecordTelemetryAsync(batch, agent.CertificateThumbprint)).Success);
    }
}
