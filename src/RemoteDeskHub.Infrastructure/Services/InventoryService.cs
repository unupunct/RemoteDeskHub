using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;
using RemoteDeskHub.Infrastructure.Data;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class InventoryService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    IAgentTransport transport,
    ISessionContext session,
    IAuditService audit,
    IClock clock) : IInventoryService
{
    public async Task<HardwareInventoryDto?> GetHardwareAsync(Guid computerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.HardwareInventories.AsNoTracking()
            .FirstOrDefaultAsync(h => h.ComputerId == computerId, cancellationToken);
        return entity is null ? null : HardwareMapper.ToDto(entity);
    }

    public async Task<IReadOnlyList<HardwareInventoryHistoryView>> GetHardwareHistoryAsync(
        Guid computerId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.HardwareInventorySnapshots.AsNoTracking()
            .Where(s => s.ComputerId == computerId)
            .OrderByDescending(s => s.CollectedUtc)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(s => new HardwareInventoryHistoryView
            {
                Id = s.Id,
                CollectedUtc = s.CollectedUtc,
                ChangeSummary = s.ChangeSummary,
                Source = s.Source
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<OperationResult> RefreshHardwareAsync(string actor, Guid computerId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewComputers, actor);
        var started = clock.UtcNow;
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var computer = await db.Computers.Include(c => c.Agent)
                .FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken);
            if (computer is null)
            {
                return OperationResult.Fail("Computer not found.");
            }

            if (computer.Agent is null || computer.Agent.State != AgentState.Active)
            {
                return OperationResult.Fail("The RemoteDesk Agent is not enrolled and approved for this computer.");
            }

            var dto = await transport.GetHardwareAsync(computer, cancellationToken);
            var entity = await db.HardwareInventories.FirstOrDefaultAsync(h => h.ComputerId == computerId, cancellationToken);
            var previous = entity is null ? null : HardwareMapper.ToDto(entity);
            if (entity is null)
            {
                entity = new HardwareInventory { ComputerId = computerId };
                db.HardwareInventories.Add(entity);
            }

            var hasHistory = await db.HardwareInventorySnapshots
                .AnyAsync(s => s.ComputerId == computerId, cancellationToken);
            if (previous is not null && !hasHistory)
            {
                // Capture the configuration that existed before history was enabled so the first
                // upgrade still has a comparison baseline.
                db.HardwareInventorySnapshots.Add(new HardwareInventorySnapshot
                {
                    ComputerId = computerId,
                    SnapshotJson = JsonDefaults.Serialize(previous),
                    ChangeSummary = "Baseline captured before configuration history was enabled.",
                    Source = "Baseline",
                    CollectedUtc = previous.CollectedUtc.UtcDateTime
                });
            }

            var changeSummary = previous is null
                ? "Initial hardware configuration captured."
                : DescribeHardwareChanges(previous, dto);

            entity.CpuModel = dto.CpuModel;
            entity.CpuManufacturer = dto.CpuManufacturer;
            entity.CpuCores = dto.CpuCores;
            entity.CpuLogicalProcessors = dto.CpuLogicalProcessors;
            entity.CpuMaxClockMhz = dto.CpuMaxClockMhz;
            entity.RamTotalBytes = dto.RamTotalBytes;
            entity.RamAvailableBytes = dto.RamAvailableBytes;
            entity.MotherboardManufacturer = dto.MotherboardManufacturer;
            entity.MotherboardModel = dto.MotherboardModel;
            entity.BiosVersion = dto.BiosVersion;
            entity.BiosManufacturer = dto.BiosManufacturer;
            entity.GpusJson = JsonDefaults.Serialize(dto.Gpus);
            entity.MemoryModulesJson = JsonDefaults.Serialize(dto.MemoryModules);
            entity.DisksJson = JsonDefaults.Serialize(dto.Disks);
            entity.AdaptersJson = JsonDefaults.Serialize(dto.Adapters);
            entity.RawJson = JsonDefaults.Serialize(dto);
            entity.CollectedUtc = clock.UtcNow;

            computer.MemoryTotalBytes = dto.RamTotalBytes;
            computer.UpdatedUtc = clock.UtcNow;

            if (changeSummary is not null)
            {
                db.HardwareInventorySnapshots.Add(new HardwareInventorySnapshot
                {
                    ComputerId = computerId,
                    SnapshotJson = JsonDefaults.Serialize(dto),
                    ChangeSummary = changeSummary,
                    Source = "Refresh",
                    CollectedUtc = entity.CollectedUtc
                });
            }

            await db.SaveChangesAsync(cancellationToken);

            await audit.LogAsync(actor, "Inventory.HardwareRefreshed", computer.Name, computer.Id,
                durationMs: (long)(clock.UtcNow - started).TotalMilliseconds,
                details: changeSummary, cancellationToken: cancellationToken);
            return OperationResult.Ok(changeSummary is null
                ? "Hardware inventory checked; no configuration change detected."
                : "Hardware inventory updated and a configuration snapshot was preserved.");
        }
        catch (Exception ex)
        {
            await audit.LogAsync(actor, "Inventory.HardwareRefreshed", result: "Failed", error: ex.Message,
                durationMs: (long)(clock.UtcNow - started).TotalMilliseconds, cancellationToken: cancellationToken);
            return OperationResult.Fail(Describe(ex));
        }
    }

    public async Task<IReadOnlyList<SoftwareInventoryItem>> GetSoftwareAsync(Guid computerId, string? search = null, string? publisher = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.SoftwareInventory.AsNoTracking().Where(s => s.ComputerId == computerId && s.IsPresent);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(s => EF.Functions.Like(s.Name, pattern) || EF.Functions.Like(s.Publisher ?? string.Empty, pattern));
        }

        if (!string.IsNullOrWhiteSpace(publisher))
        {
            query = query.Where(s => s.Publisher == publisher);
        }

        return await query.OrderBy(s => s.Name).ThenBy(s => s.Version).ToListAsync(cancellationToken);
    }

    public async Task<OperationResult> RefreshSoftwareAsync(string actor, Guid computerId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewComputers, actor);
        var started = clock.UtcNow;
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var computer = await db.Computers.Include(c => c.Agent)
                .FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken);
            if (computer is null)
            {
                return OperationResult.Fail("Computer not found.");
            }

            if (computer.Agent is null || computer.Agent.State != AgentState.Active)
            {
                return OperationResult.Fail("The RemoteDesk Agent is not enrolled and approved for this computer.");
            }

            var inventory = await transport.GetSoftwareAsync(computer, cancellationToken);
            var now = clock.UtcNow;
            var existing = await db.SoftwareInventory.Where(s => s.ComputerId == computerId).ToListAsync(cancellationToken);
            var byKey = existing
                .GroupBy(s => ItemKey(s.Name, s.Publisher, s.Version), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var item in existing)
            {
                item.IsPresent = false;
            }

            var incomingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dto in inventory.Items.Take(20_000))
            {
                if (string.IsNullOrWhiteSpace(dto.Name))
                {
                    continue;
                }

                var key = ItemKey(dto.Name, dto.Publisher, dto.Version);
                if (!incomingKeys.Add(key))
                {
                    continue;
                }

                if (!byKey.TryGetValue(key, out var row))
                {
                    row = new SoftwareInventoryItem
                    {
                        ComputerId = computerId,
                        Name = dto.Name,
                        FirstSeenUtc = now,
                        ScanUtc = now
                    };
                    db.SoftwareInventory.Add(row);
                }

                row.IsPresent = true;
                row.Publisher = dto.Publisher;
                row.Version = dto.Version;
                row.InstallDate = dto.InstallDate;
                row.InstallLocation = dto.InstallLocation;
                row.EstimatedSizeBytes = dto.EstimatedSizeBytes;
                row.Architecture = dto.Architecture;
                row.RegistryKey = dto.RegistryKey;
                row.LastSeenUtc = now;
                row.ScanUtc = now;
            }

            computer.UpdatedUtc = now;
            await db.SaveChangesAsync(cancellationToken);

            await audit.LogAsync(actor, "Inventory.SoftwareRefreshed", computer.Name, computer.Id,
                durationMs: (long)(clock.UtcNow - started).TotalMilliseconds,
                details: $"{incomingKeys.Count} applications inventoried.", cancellationToken: cancellationToken);
            return OperationResult.Ok($"{incomingKeys.Count} applications inventoried.");
        }
        catch (Exception ex)
        {
            await audit.LogAsync(actor, "Inventory.SoftwareRefreshed", result: "Failed", error: ex.Message,
                durationMs: (long)(clock.UtcNow - started).TotalMilliseconds, cancellationToken: cancellationToken);
            return OperationResult.Fail(Describe(ex));
        }
    }

    public async Task<IReadOnlyList<SoftwareInventoryItem>> CompareSoftwareAsync(IReadOnlyCollection<Guid> computerIds, CancellationToken cancellationToken = default)
    {
        if (computerIds.Count == 0)
        {
            return [];
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.SoftwareInventory.AsNoTracking()
            .Where(s => computerIds.Contains(s.ComputerId) && s.IsPresent)
            .OrderBy(s => s.Name).ThenBy(s => s.ComputerId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetPublishersAsync(Guid computerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.SoftwareInventory.AsNoTracking()
            .Where(s => s.ComputerId == computerId && s.IsPresent && s.Publisher != null && s.Publisher != "")
            .Select(s => s.Publisher!)
            .Distinct()
            .OrderBy(p => p)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetSoftwareCountAsync(Guid computerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.SoftwareInventory.AsNoTracking().CountAsync(s => s.ComputerId == computerId && s.IsPresent, cancellationToken);
    }

    private static string? DescribeHardwareChanges(HardwareInventoryDto previous, HardwareInventoryDto current)
    {
        var changes = new List<string>();
        AddChange(changes, "CPU", CpuText(previous), CpuText(current));
        AddChange(changes, "RAM", FormatBytes(previous.RamTotalBytes), FormatBytes(current.RamTotalBytes));
        AddChange(changes, "Motherboard",
            $"{previous.MotherboardManufacturer} {previous.MotherboardModel}".Trim(),
            $"{current.MotherboardManufacturer} {current.MotherboardModel}".Trim());
        AddChange(changes, "BIOS",
            $"{previous.BiosManufacturer} {previous.BiosVersion}".Trim(),
            $"{current.BiosManufacturer} {current.BiosVersion}".Trim());
        AddChange(changes, "GPU", GpuText(previous), GpuText(current));
        AddChange(changes, "Memory modules", MemoryText(previous), MemoryText(current));
        AddChange(changes, "Disks", DiskText(previous), DiskText(current));
        AddChange(changes, "Network adapters", AdapterText(previous), AdapterText(current));
        return changes.Count == 0 ? null : string.Join("; ", changes);
    }

    private static void AddChange(List<string> changes, string label, string? before, string? after)
    {
        if (!string.Equals(before ?? string.Empty, after ?? string.Empty, StringComparison.Ordinal))
        {
            changes.Add($"{label}: {Display(before)} -> {Display(after)}");
        }
    }

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "(none)" : value;

    private static string CpuText(HardwareInventoryDto dto) =>
        $"{dto.CpuManufacturer} {dto.CpuModel} ({dto.CpuCores}C/{dto.CpuLogicalProcessors}T)".Trim();

    private static string GpuText(HardwareInventoryDto dto) =>
        string.Join(", ", dto.Gpus.Select(g => string.IsNullOrWhiteSpace(g.DriverVersion)
            ? g.Model
            : $"{g.Model} (driver {g.DriverVersion})"));

    private static string MemoryText(HardwareInventoryDto dto) =>
        string.Join(", ", dto.MemoryModules.Select(m =>
            $"{m.Slot ?? "slot"}={FormatBytes(m.CapacityBytes)}{(m.SpeedMhz is null ? string.Empty : $"@{m.SpeedMhz}MHz")}"));

    private static string DiskText(HardwareInventoryDto dto) =>
        string.Join(", ", dto.Disks.Select(d =>
            $"{d.Model} {FormatBytes(d.SizeBytes)}{(string.IsNullOrWhiteSpace(d.MediaType) ? string.Empty : $" {d.MediaType}")}"));

    private static string AdapterText(HardwareInventoryDto dto) =>
        string.Join(", ", dto.Adapters.Select(a =>
            $"{a.Name} {a.MacAddress}{(a.LinkSpeedBps is null ? string.Empty : $" {a.LinkSpeedBps / 1_000_000}Mbps")}"));

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    private static string ItemKey(string? name, string? publisher, string? version)
        => $"{name?.Trim()}|{publisher?.Trim()}|{version?.Trim()}";

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "The agent did not respond before the request timed out.",
        HttpRequestException http => $"The agent could not be reached: {http.Message}",
        _ => ex.Message
    };
}
