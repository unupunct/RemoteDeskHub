using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Infrastructure.Services;

internal static class HardwareMapper
{
    public static HardwareInventoryDto ToDto(HardwareInventory entity)
    {
        return new HardwareInventoryDto
        {
            CpuModel = entity.CpuModel,
            CpuManufacturer = entity.CpuManufacturer,
            CpuCores = entity.CpuCores,
            CpuLogicalProcessors = entity.CpuLogicalProcessors,
            CpuMaxClockMhz = entity.CpuMaxClockMhz,
            RamTotalBytes = entity.RamTotalBytes,
            RamAvailableBytes = entity.RamAvailableBytes,
            MotherboardManufacturer = entity.MotherboardManufacturer,
            MotherboardModel = entity.MotherboardModel,
            BiosVersion = entity.BiosVersion,
            BiosManufacturer = entity.BiosManufacturer,
            Gpus = Deserialize<List<GpuInfoDto>>(entity.GpusJson),
            MemoryModules = Deserialize<List<MemoryModuleDto>>(entity.MemoryModulesJson),
            Disks = Deserialize<List<DiskInfoDto>>(entity.DisksJson),
            Adapters = Deserialize<List<NetworkAdapterInfoDto>>(entity.AdaptersJson),
            CollectedUtc = new DateTimeOffset(DateTime.SpecifyKind(entity.CollectedUtc, DateTimeKind.Utc))
        };
    }

    private static T Deserialize<T>(string? json) where T : new()
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new T();
        }

        try
        {
            return JsonDefaults.Deserialize<T>(json) ?? new T();
        }
        catch
        {
            return new T();
        }
    }
}
