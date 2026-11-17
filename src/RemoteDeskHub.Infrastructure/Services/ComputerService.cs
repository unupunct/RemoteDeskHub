using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;
using RemoteDeskHub.Shared.Security;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class ComputerService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    IInventoryService inventory,
    IMonitoringService monitoring,
    IAgentTransport transport,
    ICommandService commandService,
    ISettingsService settings,
    ISessionContext session,
    IAppEvents events,
    IAuditService audit,
    IClock clock) : IComputerService
{
    public async Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewDashboard);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computers = await LoadComputerEntitiesAsync(db, cancellationToken);
        var alerts = await db.Alerts.AsNoTracking().Where(a => a.IsActive).OrderByDescending(a => a.CreatedUtc).ToListAsync(cancellationToken);
        var alertStats = alerts
            .Where(a => a.ComputerId.HasValue)
            .GroupBy(a => a.ComputerId!.Value)
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Highest: g.Max(a => a.Severity)));

        var timeout = await GetOfflineThresholdAsync(cancellationToken);
        var summaries = computers.Select(c => MapSummary(c, alertStats, timeout)).ToList();

        var since = clock.UtcNow.AddMinutes(-2);
        var network = await db.PerformanceMetrics.AsNoTracking()
            .Where(m => m.TimestampUtc >= since)
            .GroupBy(m => m.ComputerId)
            .Select(g => new { Id = g.Key, In = g.Average(m => m.NetworkInBps), Out = g.Average(m => m.NetworkOutBps) })
            .ToListAsync(cancellationToken);

        var activeSessions = await db.RemoteSessions.CountAsync(s => s.Status == RemoteSessionStatus.Started, cancellationToken);
        var pendingAgents = await db.Agents.CountAsync(a => a.State == AgentState.PendingApproval, cancellationToken);
        var discovered = await db.DiscoveryDevices.CountAsync(cancellationToken);
        var recentAudit = session.CurrentUser?.Can(Permission.ViewAudit) == true
            ? await audit.QueryAsync(null, null, null, 8, cancellationToken)
            : Array.Empty<AuditView>();

        return new DashboardSnapshot
        {
            TotalComputers = summaries.Count,
            OnlineComputers = summaries.Count(c => c.Status == ComputerStatus.Online),
            OfflineComputers = summaries.Count(c => c.Status == ComputerStatus.Offline),
            WarningComputers = summaries.Count(c => c.Status == ComputerStatus.Warning),
            CriticalComputers = summaries.Count(c => c.Status == ComputerStatus.Critical),
            PendingAgents = pendingAgents,
            ActiveRemoteSessions = activeSessions,
            TotalNetworkInBps = network.Sum(n => n.In),
            TotalNetworkOutBps = network.Sum(n => n.Out),
            ActiveAlerts = alerts.Count,
            UnacknowledgedAlerts = alerts.Count(a => a.AcknowledgedUtc is null),
            ManagedComputers = summaries.Count(c => c.IsManaged),
            DiscoveredDevices = discovered,
            Computers = summaries.OrderByDescending(c => c.Status).ThenBy(c => c.Name).ToList(),
            RecentAlerts = alerts.Take(8).Select(MapAlert).ToList(),
            RecentActions = recentAudit,
            RecentDiscovery = await db.DiscoveryDevices.AsNoTracking()
                .OrderByDescending(d => d.LastSeenUtc).Take(8)
                .Select(d => new DiscoveryView
                {
                    Id = d.Id,
                    IpAddress = d.IpAddress,
                    MacAddress = d.MacAddress,
                    Hostname = d.Hostname,
                    Vendor = d.Vendor,
                    OperatingSystem = d.OperatingSystem,
                    HasAgent = d.HasAgent,
                    IsRegistered = d.IsRegistered,
                    IsTrusted = d.IsTrusted,
                    FriendlyName = d.FriendlyName,
                    LastSeenUtc = d.LastSeenUtc,
                    FirstSeenUtc = d.FirstSeenUtc
                }).ToListAsync(cancellationToken)
        };
    }

    public async Task<IReadOnlyList<ComputerSummary>> GetComputersAsync(Guid? groupId = null, string? search = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Computers.AsNoTracking().Include(c => c.Group).Include(c => c.Agent).AsQueryable();
        if (groupId.HasValue)
        {
            query = query.Where(c => c.GroupId == groupId);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(c =>
                EF.Functions.Like(c.Name, pattern) ||
                EF.Functions.Like(c.FriendlyName ?? string.Empty, pattern) ||
                EF.Functions.Like(c.IpAddress ?? string.Empty, pattern) ||
                EF.Functions.Like(c.OperatingSystem ?? string.Empty, pattern));
        }

        var computers = await query.OrderBy(c => c.Name).ToListAsync(cancellationToken);
        var alertStats = await LoadAlertStatsAsync(db, cancellationToken);
        var timeout = await GetOfflineThresholdAsync(cancellationToken);
        return computers.Select(c => MapSummary(c, alertStats, timeout)).ToList();
    }

    public async Task<ComputerSummary?> GetComputerSummaryAsync(Guid computerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computer = await db.Computers.AsNoTracking().Include(c => c.Group).Include(c => c.Agent)
            .FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken);
        if (computer is null)
        {
            return null;
        }

        var stats = await LoadAlertStatsAsync(db, cancellationToken);
        return MapSummary(computer, stats, await GetOfflineThresholdAsync(cancellationToken));
    }

    public async Task<ComputerDetail?> GetComputerDetailAsync(Guid computerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computer = await db.Computers.AsNoTracking()
            .Include(c => c.Group).Include(c => c.Agent).Include(c => c.Hardware)
            .FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken);
        if (computer is null)
        {
            return null;
        }

        var stats = await LoadAlertStatsAsync(db, cancellationToken);
        var summary = MapSummary(computer, stats, await GetOfflineThresholdAsync(cancellationToken));
        var hardware = computer.Hardware is null ? null : HardwareMapper.ToDto(computer.Hardware);
        var softwareCount = await db.SoftwareInventory.CountAsync(s => s.ComputerId == computerId && s.IsPresent, cancellationToken);
        var lastInventory = await db.SoftwareInventory.Where(s => s.ComputerId == computerId)
            .MaxAsync(s => (DateTime?)s.LastSeenUtc, cancellationToken);

        double? quality = null;
        if (computer.Agent?.LastSeenUtc is { } seen)
        {
            var age = Math.Max(0, (clock.UtcNow - seen).TotalSeconds);
            var heartbeat = Math.Max(2, await settings.GetAsync(SettingsKeys.HeartbeatSeconds, HubConstants.DefaultHeartbeatSeconds, cancellationToken));
            quality = Math.Clamp(100 - (age / (heartbeat * 3.0) * 100), 0, 100);
        }

        return new ComputerDetail
        {
            Summary = summary,
            AgentId = computer.Agent?.Id,
            AgentState = computer.Agent?.State,
            AgentHealth = computer.Agent?.HealthStatus,
            MacAddress = computer.MacAddress,
            Manufacturer = computer.Manufacturer,
            Model = computer.Model,
            Architecture = computer.Architecture,
            Hardware = hardware,
            HardwareCollectedUtc = computer.Hardware?.CollectedUtc,
            SoftwareCount = softwareCount,
            LastInventoryUtc = lastInventory,
            CreatedUtc = computer.CreatedUtc,
            UpdatedUtc = computer.UpdatedUtc,
            ConnectionQualityPercent = quality
        };
    }

    public async Task<OperationResult> AddManualComputerAsync(string actor, string name, string ipAddress, string? macAddress, Guid? groupId, string? notes, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageComputers, actor);
        name = (name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 128)
        {
            return OperationResult.Fail("Computer name must be between 1 and 128 characters.");
        }

        if (!NetworkAddressGuard.TryParseLanAddress(ipAddress, out var parsedIp, out var ipError))
        {
            return OperationResult.Fail(ipError);
        }

        if (!string.IsNullOrWhiteSpace(macAddress) && !IsValidMac(macAddress))
        {
            return OperationResult.Fail("MAC address must use the format AA:BB:CC:DD:EE:FF (or be left empty).");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var normalizedIp = parsedIp.ToString();
        if (await db.Computers.AnyAsync(c => c.IpAddress == normalizedIp, cancellationToken))
        {
            return OperationResult.Fail("A computer with that IP address is already in the inventory.");
        }

        if (groupId.HasValue && !await db.ComputerGroups.AnyAsync(g => g.Id == groupId, cancellationToken))
        {
            return OperationResult.Fail("The selected group does not exist.");
        }

        var computer = new Computer
        {
            Name = name,
            IpAddress = normalizedIp,
            MacAddress = string.IsNullOrWhiteSpace(macAddress) ? null : macAddress.Trim().ToUpperInvariant(),
            GroupId = groupId,
            Notes = notes,
            IsRegistered = true,
            IsManaged = false,
            IsTrusted = false,
            Source = "Manual",
            Status = ComputerStatus.Unknown,
            CreatedUtc = clock.UtcNow,
            UpdatedUtc = clock.UtcNow,
            FirstSeenUtc = clock.UtcNow
        };
        db.Computers.Add(computer);
        if (groupId.HasValue)
        {
            db.ComputerGroupMemberships.Add(new ComputerGroupMembership { ComputerId = computer.Id, GroupId = groupId.Value, AssignedUtc = clock.UtcNow });
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, AuditOperations.ComputerAdded, computer.Name, computer.Id, details: $"Manual computer added at {normalizedIp}.", cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok($"Computer '{computer.Name}' added to the inventory.");
    }

    public async Task<OperationResult> UpdateComputerAsync(string actor, Guid computerId, string? friendlyName, string? description, string? notes, string? tags, Guid? groupId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageComputers, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computer = await db.Computers.FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken);
        if (computer is null)
        {
            return OperationResult.Fail("Computer not found.");
        }

        if (groupId.HasValue && !await db.ComputerGroups.AnyAsync(g => g.Id == groupId, cancellationToken))
        {
            return OperationResult.Fail("The selected group does not exist.");
        }

        var previousGroup = computer.GroupId;
        computer.FriendlyName = string.IsNullOrWhiteSpace(friendlyName) ? null : friendlyName.Trim();
        computer.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        computer.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        computer.Tags = string.IsNullOrWhiteSpace(tags) ? null : tags.Trim();
        computer.GroupId = groupId;
        computer.UpdatedUtc = clock.UtcNow;

        var memberships = await db.ComputerGroupMemberships.Where(m => m.ComputerId == computerId).ToListAsync(cancellationToken);
        db.ComputerGroupMemberships.RemoveRange(memberships);
        if (groupId.HasValue)
        {
            db.ComputerGroupMemberships.Add(new ComputerGroupMembership { ComputerId = computerId, GroupId = groupId.Value, AssignedUtc = clock.UtcNow });
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, AuditOperations.ComputerUpdated, computer.Name, computer.Id,
            details: $"Group: {previousGroup?.ToString() ?? "none"} -> {groupId?.ToString() ?? "none"}; friendly name '{computer.FriendlyName}'.", cancellationToken: cancellationToken);
        events.RaiseComputerChanged(computerId);
        events.RaiseDataChanged();
        return OperationResult.Ok("Computer details updated.");
    }

    public async Task<OperationResult> SetTrustedAsync(string actor, Guid computerId, bool trusted, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageComputers, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computer = await db.Computers.FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken);
        if (computer is null)
        {
            return OperationResult.Fail("Computer not found.");
        }

        computer.IsTrusted = trusted;
        computer.UpdatedUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, "Computer.TrustChanged", computer.Name, computer.Id, details: $"Trusted={trusted}.", cancellationToken: cancellationToken);
        events.RaiseComputerChanged(computerId);
        return OperationResult.Ok(trusted ? "Computer marked as trusted." : "Computer is no longer trusted.");
    }

    public async Task<OperationResult> RemoveComputerAsync(string actor, Guid computerId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageComputers, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computer = await db.Computers.FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken);
        if (computer is null)
        {
            return OperationResult.Fail("Computer not found.");
        }

        db.Computers.Remove(computer);
        var devices = await db.DiscoveryDevices.Where(d => d.ComputerId == computerId || (computer.IpAddress != null && d.IpAddress == computer.IpAddress)).ToListAsync(cancellationToken);
        foreach (var device in devices)
        {
            device.IsRegistered = false;
            device.ComputerId = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, AuditOperations.ComputerRemoved, computer.Name, computerId, details: "Computer and associated inventory removed.", cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok($"Computer '{computer.Name}' removed.");
    }

    public async Task<OperationResult> RefreshInventoryAsync(string actor, Guid computerId, CancellationToken cancellationToken = default)
    {
        var hardware = await inventory.RefreshHardwareAsync(actor, computerId, cancellationToken);
        var software = await inventory.RefreshSoftwareAsync(actor, computerId, cancellationToken);
        events.RaiseComputerChanged(computerId);
        if (hardware.Success && software.Success)
        {
            return OperationResult.Ok("Hardware and software inventory refreshed.");
        }

        var errors = string.Join(" ", new[] { hardware.Error, software.Error }.Where(e => !string.IsNullOrWhiteSpace(e)));
        return OperationResult.Fail(string.IsNullOrWhiteSpace(errors) ? "Inventory refresh did not complete." : errors);
    }

    public async Task<OperationResult> RefreshMetricsAsync(Guid computerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computer = await db.Computers.Include(c => c.Agent).FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken);
        if (computer is null)
        {
            return OperationResult.Fail("Computer not found.");
        }

        try
        {
            var metrics = await transport.GetMetricsAsync(computer, cancellationToken);
            await monitoring.IngestMetricsAsync(computer, metrics, cancellationToken);
            return OperationResult.Ok("Live metrics refreshed.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Live metrics unavailable: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<GroupView>> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var groups = await db.ComputerGroups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(cancellationToken);
        var counts = await db.Computers.AsNoTracking().Where(c => c.GroupId != null)
            .GroupBy(c => c.GroupId!.Value).Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Id, g => g.Count, cancellationToken);
        return groups.Select(g => new GroupView
        {
            Id = g.Id,
            Name = g.Name,
            Description = g.Description,
            Kind = g.Kind,
            IsSystem = g.IsSystem,
            ComputerCount = counts.GetValueOrDefault(g.Id)
        }).ToList();
    }

    public async Task<OperationResult> CreateGroupAsync(string actor, string name, string? description, string kind, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageGroups, actor);
        name = (name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 128)
        {
            return OperationResult.Fail("Group name must be between 1 and 128 characters.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.ComputerGroups.AnyAsync(g => g.Name == name, cancellationToken))
        {
            return OperationResult.Fail("A group with that name already exists.");
        }

        var group = new ComputerGroup
        {
            Name = name,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Kind = string.IsNullOrWhiteSpace(kind) ? "Custom" : kind.Trim(),
            CreatedUtc = clock.UtcNow
        };
        db.ComputerGroups.Add(group);
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, AuditOperations.GroupChanged, details: $"Group '{name}' created.", cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok($"Group '{name}' created.");
    }

    public async Task<OperationResult> RenameGroupAsync(string actor, Guid groupId, string name, string? description, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageGroups, actor);
        name = (name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 128)
        {
            return OperationResult.Fail("Group name must be between 1 and 128 characters.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var group = await db.ComputerGroups.FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null)
        {
            return OperationResult.Fail("Group not found.");
        }

        if (await db.ComputerGroups.AnyAsync(g => g.Name == name && g.Id != groupId, cancellationToken))
        {
            return OperationResult.Fail("Another group already uses that name.");
        }

        group.Name = name;
        group.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, AuditOperations.GroupChanged, details: $"Group '{groupId}' renamed to '{name}'.", cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok("Group updated.");
    }

    public async Task<OperationResult> DeleteGroupAsync(string actor, Guid groupId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageGroups, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var group = await db.ComputerGroups.FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null)
        {
            return OperationResult.Fail("Group not found.");
        }

        if (group.IsSystem)
        {
            return OperationResult.Fail("Built-in groups cannot be deleted. Rename the group or move its computers instead.");
        }

        db.ComputerGroups.Remove(group);
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, AuditOperations.GroupChanged, details: $"Group '{group.Name}' deleted.", cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok($"Group '{group.Name}' deleted. Its computers are now ungrouped.");
    }

    public Task<BulkOperationProgress> BulkRefreshInventoryAsync(string actor, IReadOnlyCollection<Guid> computerIds, IProgress<BulkOperationProgress>? progress, CancellationToken cancellationToken = default)
        => RunBulkAsync("Refresh inventory", actor, computerIds,
            c => inventory.RefreshHardwareAsync(actor, c.Id, cancellationToken)
                .ContinueWith(async t =>
                {
                    var hw = await t;
                    var sw = await inventory.RefreshSoftwareAsync(actor, c.Id, cancellationToken);
                    return hw.Success && sw.Success ? OperationResult.Ok("Inventory refreshed.") : OperationResult.Fail(string.Join(' ', new[] { hw.Error, sw.Error }.Where(e => !string.IsNullOrWhiteSpace(e))));
                }, cancellationToken).Unwrap(), progress, cancellationToken);

    public Task<BulkOperationProgress> BulkCollectHardwareAsync(string actor, IReadOnlyCollection<Guid> computerIds, IProgress<BulkOperationProgress>? progress, CancellationToken cancellationToken = default)
        => RunBulkAsync("Collect hardware", actor, computerIds, c => inventory.RefreshHardwareAsync(actor, c.Id, cancellationToken), progress, cancellationToken);

    public Task<BulkOperationProgress> BulkCheckDiskSpaceAsync(string actor, IReadOnlyCollection<Guid> computerIds, IProgress<BulkOperationProgress>? progress, CancellationToken cancellationToken = default)
        => RunBulkAsync("Check disk space", actor, computerIds, async c =>
        {
            try
            {
                var metrics = await transport.GetMetricsAsync(c, cancellationToken);
                var freePercent = 100 - metrics.DiskPercent;
                return OperationResult.Ok($"Disk usage {metrics.DiskPercent:F1}% (free {freePercent:F1}%).");
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(ex.Message);
            }
        }, progress, cancellationToken);

    public Task<BulkOperationProgress> BulkCheckServiceStatusAsync(string actor, IReadOnlyCollection<Guid> computerIds, string serviceName, IProgress<BulkOperationProgress>? progress, CancellationToken cancellationToken = default)
        => RunBulkAsync($"Check service '{serviceName}'", actor, computerIds, async c =>
        {
            try
            {
                var services = await transport.GetServicesAsync(c, cancellationToken);
                var service = services.FirstOrDefault(s => string.Equals(s.Name, serviceName, StringComparison.OrdinalIgnoreCase));
                return service is null
                    ? OperationResult.Fail($"Service '{serviceName}' was not found.")
                    : OperationResult.Ok($"Service '{service.DisplayName}' is {service.Status} ({service.StartupType}).");
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(ex.Message);
            }
        }, progress, cancellationToken);

    public Task<BulkOperationProgress> BulkRunDiagnosticAsync(string actor, IReadOnlyCollection<Guid> computerIds, IProgress<BulkOperationProgress>? progress, CancellationToken cancellationToken = default)
        => RunBulkAsync("Run diagnostic", actor, computerIds, async c =>
        {
            const string diagnostic = "Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,LastBootUpTime | Format-List; " +
                                      "Get-CimInstance Win32_LogicalDisk -Filter \"DriveType=3\" | Select-Object DeviceID,Size,FreeSpace | Format-Table -AutoSize";
            var result = await commandService.ExecuteAsync(actor, c, diagnostic, CommandShell.PowerShell, 60, false, true, true, cancellationToken);
            return result.Error is null && result.ExitCode == 0
                ? OperationResult.Ok("Diagnostic completed.")
                : OperationResult.Fail(result.Error ?? result.StdErr ?? $"Exit code {result.ExitCode}.");
        }, progress, cancellationToken);

    private async Task<BulkOperationProgress> RunBulkAsync(
        string operation,
        string actor,
        IReadOnlyCollection<Guid> computerIds,
        Func<Computer, Task<OperationResult>> action,
        IProgress<BulkOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        AccessGuard.Require(session, Permission.ManageComputers, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computers = await db.Computers.Include(c => c.Agent)
            .Where(c => computerIds.Contains(c.Id)).ToListAsync(cancellationToken);

        var results = new List<BulkResult>(computers.Count);
        var completed = 0;
        var failed = 0;
        var sync = new object();
        using var gate = new SemaphoreSlim(4);

        var tasks = computers.Select(async computer =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                OperationResult result;
                try
                {
                    result = await action(computer);
                }
                catch (Exception ex)
                {
                    result = OperationResult.Fail(ex.Message);
                }

                lock (sync)
                {
                    completed++;
                    if (!result.Success)
                    {
                        failed++;
                    }

                    results.Add(new BulkResult(computer.Id, computer.Name, result.Success, result.Message ?? result.Error ?? string.Empty));
                    progress?.Report(new BulkOperationProgress
                    {
                        Operation = operation,
                        Total = computers.Count,
                        Completed = completed,
                        Failed = failed,
                        CurrentComputer = computer.Name,
                        Results = results.ToList()
                    });
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        await audit.LogAsync(actor, "Bulk.Operation", result: failed == 0 ? "Success" : "PartialFailure",
            error: failed == 0 ? null : $"{failed} of {computers.Count} computers failed.",
            details: $"{operation} on {computers.Count} computers.", cancellationToken: cancellationToken);
        return new BulkOperationProgress
        {
            Operation = operation,
            Total = computers.Count,
            Completed = completed,
            Failed = failed,
            Results = results.OrderBy(r => r.ComputerName).ToList()
        };
    }

    private static async Task<List<Computer>> LoadComputerEntitiesAsync(RemoteDeskDbContext db, CancellationToken cancellationToken)
        => await db.Computers.AsNoTracking().Include(c => c.Group).Include(c => c.Agent)
            .OrderBy(c => c.Name).ToListAsync(cancellationToken);

    private static async Task<Dictionary<Guid, (int Count, AlertSeverity Highest)>> LoadAlertStatsAsync(RemoteDeskDbContext db, CancellationToken cancellationToken)
    {
        var alerts = await db.Alerts.AsNoTracking().Where(a => a.IsActive && a.ComputerId != null)
            .Select(a => new { a.ComputerId, a.Severity }).ToListAsync(cancellationToken);
        return alerts.GroupBy(a => a.ComputerId!.Value)
            .ToDictionary(g => g.Key, g => (g.Count(), g.Max(a => a.Severity)));
    }

    private ComputerSummary MapSummary(Computer c, Dictionary<Guid, (int Count, AlertSeverity Highest)> alertStats, TimeSpan offlineThreshold)
    {
        var stats = alertStats.GetValueOrDefault(c.Id);
        var status = ResolveStatus(c, stats.Highest, alertStats.ContainsKey(c.Id), offlineThreshold);
        return new ComputerSummary
        {
            Id = c.Id,
            Name = c.Name,
            FriendlyName = c.FriendlyName,
            IpAddress = c.IpAddress,
            MacAddress = c.MacAddress,
            OperatingSystem = c.OperatingSystem,
            OsVersion = c.OsVersion,
            MachineId = c.MachineId ?? c.Agent?.MachineId,
            AgentVersion = c.AgentVersion ?? c.Agent?.Version,
            Status = status,
            IsManaged = c.IsManaged,
            IsTrusted = c.IsTrusted,
            IsRegistered = c.IsRegistered,
            GroupId = c.GroupId,
            GroupName = c.Group?.Name,
            CpuPercent = c.CpuPercent,
            MemoryPercent = c.MemoryPercent,
            DiskPercent = c.DiskPercent,
            CpuTemperatureC = c.CpuTemperatureC,
            MemoryTotalBytes = c.MemoryTotalBytes,
            UptimeSeconds = c.UptimeSeconds,
            LastSeenUtc = c.LastSeenUtc,
            LastBootUtc = c.LastBootUtc,
            Notes = c.Notes,
            Description = c.Description,
            TeamViewerId = c.TeamViewerId,
            AnyDeskId = c.AnyDeskId,
            LmViewerId = c.LmViewerId,
            RustDeskId = c.RustDeskId,
            ActiveAlertCount = stats.Count,
            HighestAlert = stats.Highest
        };
    }

    private ComputerStatus ResolveStatus(Computer computer, AlertSeverity highest, bool hasAlerts, TimeSpan offlineThreshold)
    {
        if (computer.Agent?.State == AgentState.PendingApproval)
        {
            return ComputerStatus.PendingApproval;
        }

        if (!computer.IsManaged)
        {
            return computer.Status == ComputerStatus.Offline ? ComputerStatus.Offline : ComputerStatus.Unknown;
        }

        if (!computer.LastSeenUtc.HasValue || clock.UtcNow - computer.LastSeenUtc.Value > offlineThreshold)
        {
            return ComputerStatus.Offline;
        }

        if (hasAlerts)
        {
            return highest switch
            {
                AlertSeverity.Critical => ComputerStatus.Critical,
                AlertSeverity.Warning => ComputerStatus.Warning,
                _ => ComputerStatus.Online
            };
        }

        return ComputerStatus.Online;
    }

    private async Task<TimeSpan> GetOfflineThresholdAsync(CancellationToken cancellationToken)
    {
        var heartbeat = await settings.GetAsync(SettingsKeys.HeartbeatSeconds, HubConstants.DefaultHeartbeatSeconds, cancellationToken);
        return TimeSpan.FromSeconds(Math.Max(30, heartbeat * 3));
    }

    private static AlertView MapAlert(Alert a) => new()
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
    };

    private static bool IsValidMac(string mac)
    {
        var parts = mac.Trim().Replace('-', ':').Split(':');
        return parts.Length == 6 && parts.All(p => p.Length == 2 && byte.TryParse(p, System.Globalization.NumberStyles.HexNumber, null, out _));
    }
}
