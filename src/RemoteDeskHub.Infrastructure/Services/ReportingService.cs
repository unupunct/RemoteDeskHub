using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class ReportingService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    IMonitoringService monitoring,
    ISessionContext session,
    IAuditService audit,
    IClock clock) : IReportService
{
    public async Task<ReportFile> GenerateAsync(ExportRequest request, CancellationToken cancellationToken = default)
    {
        return (request.ReportName ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "network" or "network-inventory" => await GenerateNetworkInventoryAsync(request.Format, cancellationToken),
            "hardware" => await GenerateHardwareInventoryAsync(request.Format, cancellationToken),
            "software" => await GenerateSoftwareInventoryAsync(request.Format, cancellationToken),
            "alerts" or "alerts-events" => await GenerateAlertReportAsync(request.Format, cancellationToken),
            "agent-health" or "agents" => await GenerateAgentHealthReportAsync(request.Format, cancellationToken),
            "audit" or "audit-log" => await GenerateAuditReportAsync(request.Format, cancellationToken),
            "performance" or "performance-history" => await monitoring.ExportHistoryAsync(
                request.ComputerIds ?? [], request.FromUtc ?? clock.UtcNow.AddDays(-1), request.ToUtc ?? clock.UtcNow, request.Format, cancellationToken),
            _ => throw new ArgumentException($"Unknown report '{request.ReportName}'.")
        };
    }

    public async Task<ReportFile> GenerateNetworkInventoryAsync(string format, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewReports);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computers = await db.Computers.AsNoTracking().Include(c => c.Group).Include(c => c.Agent)
            .OrderBy(c => c.Name).ToListAsync(cancellationToken);
        var rows = computers.Select(c => new[]
        {
            c.Name,
            c.FriendlyName ?? string.Empty,
            c.IpAddress ?? string.Empty,
            c.MacAddress ?? string.Empty,
            c.OperatingSystem ?? string.Empty,
            c.OsVersion ?? string.Empty,
            c.Group?.Name ?? "Ungrouped",
            c.Agent?.State.ToString() ?? "No agent",
            c.AgentVersion ?? c.Agent?.Version ?? string.Empty,
            c.LastSeenUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "Never",
            c.CpuPercent.ToString("F1"),
            c.MemoryPercent.ToString("F1"),
            c.DiskPercent.ToString("F1"),
            c.IsManaged ? "Managed" : c.IsRegistered ? "Registered" : "Discovered"
        }).ToList();

        var file = ReportWriter.Create("network-inventory", format, "Network Inventory",
            ["Computer", "Friendly name", "IP address", "MAC", "Operating system", "Build", "Group", "Agent state", "Agent version", "Last seen", "CPU %", "Memory %", "Disk %", "Classification"], rows);
        await AuditExportAsync("Network inventory", format, rows.Count, cancellationToken);
        return file;
    }

    public async Task<ReportFile> GenerateHardwareInventoryAsync(string format, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewReports);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computers = await db.Computers.AsNoTracking().Include(c => c.Hardware).OrderBy(c => c.Name).ToListAsync(cancellationToken);
        var rows = new List<string[]>();
        foreach (var c in computers)
        {
            var hw = c.Hardware is null ? null : HardwareMapper.ToDto(c.Hardware);
            rows.Add(new[]
            {
                c.Name,
                c.IpAddress ?? string.Empty,
                hw?.CpuModel ?? "Not collected",
                hw is null ? string.Empty : $"{hw.CpuCores} / {hw.CpuLogicalProcessors}",
                hw is null ? string.Empty : FormatBytes(hw.RamTotalBytes),
                hw is null ? string.Empty : string.Join("; ", hw.Disks.Select(d => $"{d.Model} {FormatBytes(d.SizeBytes)}")),
                hw is null ? string.Empty : string.Join("; ", hw.Gpus.Select(g => g.Model)),
                hw?.MotherboardManufacturer ?? string.Empty,
                hw?.MotherboardModel ?? string.Empty,
                hw?.BiosVersion ?? string.Empty,
                hw is null ? "Not collected" : hw.CollectedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            });
        }

        var file = ReportWriter.Create("hardware-inventory", format, "Hardware Inventory",
            ["Computer", "IP address", "CPU", "Cores / threads", "RAM", "Storage", "GPU", "Motherboard vendor", "Motherboard model", "BIOS", "Collected"], rows);
        await AuditExportAsync("Hardware inventory", format, rows.Count, cancellationToken);
        return file;
    }

    public async Task<ReportFile> GenerateSoftwareInventoryAsync(string format, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewReports);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.SoftwareInventory.AsNoTracking()
            .Where(s => s.IsPresent)
            .Join(db.Computers.AsNoTracking(), s => s.ComputerId, c => c.Id, (s, c) => new { s, c.Name })
            .OrderBy(x => x.Name).ThenBy(x => x.s.Name)
            .Select(x => new[]
            {
                x.Name,
                x.s.Name,
                x.s.Publisher ?? string.Empty,
                x.s.Version ?? string.Empty,
                x.s.InstallDate ?? string.Empty,
                x.s.InstallLocation ?? string.Empty,
                x.s.EstimatedSizeBytes.HasValue ? (x.s.EstimatedSizeBytes.Value / 1024.0 / 1024.0).ToString("F1") : string.Empty,
                x.s.Architecture ?? string.Empty
            }).ToListAsync(cancellationToken);

        var file = ReportWriter.Create("software-inventory", format, "Software Inventory",
            ["Computer", "Application", "Publisher", "Version", "Install date", "Install location", "Size (MB)", "Architecture"], rows);
        await AuditExportAsync("Software inventory", format, rows.Count, cancellationToken);
        return file;
    }

    public async Task<ReportFile> GenerateAlertReportAsync(string format, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewReports);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var alerts = await db.Alerts.AsNoTracking().OrderByDescending(a => a.CreatedUtc).Take(10_000).ToListAsync(cancellationToken);
        var rows = alerts.Select(a => new[]
        {
            a.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            a.Severity.ToString(),
            a.MetricType.ToString(),
            a.ComputerName ?? string.Empty,
            a.Title,
            a.Message,
            a.IsActive ? "Active" : "Resolved",
            a.AcknowledgedBy ?? string.Empty,
            a.AcknowledgedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty
        }).ToList();
        var file = ReportWriter.Create("alerts", format, "Alerts and Events",
            ["Created", "Severity", "Category", "Computer", "Title", "Message", "State", "Acknowledged by", "Acknowledged"], rows);
        await AuditExportAsync("Alert report", format, rows.Count, cancellationToken);
        return file;
    }

    public async Task<ReportFile> GenerateAgentHealthReportAsync(string format, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewReports);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var agents = await db.Agents.AsNoTracking().Include(a => a.Computer).OrderBy(a => a.Computer!.Name).ToListAsync(cancellationToken);
        var now = clock.UtcNow;
        var rows = agents.Select(a => new[]
        {
            a.Computer?.Name ?? a.MachineName,
            a.State.ToString(),
            a.Version ?? string.Empty,
            a.LastKnownIp ?? string.Empty,
            a.ListeningPort.ToString(),
            a.EnrolledUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            a.ApprovedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty,
            a.LastSeenUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "Never",
            a.LastSeenUtc.HasValue ? $"{Math.Max(0, (now - a.LastSeenUtc.Value).TotalSeconds):F0}s ago" : "No heartbeat",
            a.HealthStatus ?? string.Empty,
            a.RevocationReason ?? string.Empty
        }).ToList();

        var file = ReportWriter.Create("agent-health", format, "Agent Health",
            ["Computer", "State", "Version", "Last known IP", "Port", "Enrolled", "Approved", "Last heartbeat", "Heartbeat age", "Health", "Revocation reason"], rows);
        await AuditExportAsync("Agent health report", format, rows.Count, cancellationToken);
        return file;
    }

    public async Task<ReportFile> GenerateAuditReportAsync(string format, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewAudit);
        var entries = await audit.QueryAsync(clock.UtcNow.AddDays(-90), null, null, 20_000, cancellationToken);
        var rows = entries.Select(a => new[]
        {
            a.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            a.Actor,
            a.Operation,
            a.ComputerName ?? string.Empty,
            a.Result,
            a.DurationMs.ToString(),
            a.Details ?? string.Empty,
            a.Error ?? string.Empty,
            a.IpAddress ?? string.Empty
        }).ToList();
        var file = ReportWriter.Create("audit-log", format, "Audit Log",
            ["Timestamp", "Actor", "Operation", "Computer", "Result", "Duration (ms)", "Details", "Error", "Source IP"], rows);
        return file;
    }

    private async Task AuditExportAsync(string report, string format, int rows, CancellationToken cancellationToken)
    {
        await audit.LogAsync(session.CurrentUser?.UserName ?? HubConstants.AuditActorSystem, AuditOperations.ReportExported,
            details: $"{report} exported as {format.ToUpperInvariant()} ({rows} rows).", cancellationToken: cancellationToken);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:F1} {units[unit]}";
    }
}
