using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Agent.Collectors;

/// <summary>Reads hardware inventory from WMI and the network stack. All values are read-only.</summary>
public sealed class HardwareCollector
{
    public HardwareInventoryDto Collect()
    {
        var cpu = Query("root\\cimv2", "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor").ToList();
        var system = Query("root\\cimv2", "SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault();
        var board = Query("root\\cimv2", "SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        var bios = Query("root\\cimv2", "SELECT SMBIOSBIOSVersion, Manufacturer FROM Win32_BIOS").FirstOrDefault();
        var memory = Query("root\\cimv2",
            "SELECT DeviceLocator, Manufacturer, Capacity, Speed, PartNumber FROM Win32_PhysicalMemory").ToList();
        var gpus = Query("root\\cimv2", "SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController").ToList();

        var (ramTotal, ramAvailable) = ReadMemory();

        return new HardwareInventoryDto
        {
            CpuModel = cpu.Count == 0 ? null : Text(cpu[0], "Name")?.Trim(),
            CpuManufacturer = cpu.Count == 0 ? null : Text(cpu[0], "Manufacturer")?.Trim(),
            CpuCores = cpu.Sum(c => Int(c, "NumberOfCores")),
            CpuLogicalProcessors = cpu.Sum(c => Int(c, "NumberOfLogicalProcessors")),
            CpuMaxClockMhz = cpu.Count == 0 ? null : Int(cpu[0], "MaxClockSpeed"),
            RamTotalBytes = ramTotal > 0 ? ramTotal : (long)(system is null ? 0 : ULong(system, "TotalPhysicalMemory")),
            RamAvailableBytes = ramAvailable,
            MotherboardManufacturer = Text(board, "Manufacturer")?.Trim(),
            MotherboardModel = Text(board, "Product")?.Trim(),
            BiosVersion = Text(bios, "SMBIOSBIOSVersion")?.Trim(),
            BiosManufacturer = Text(bios, "Manufacturer")?.Trim(),
            Gpus = gpus.Select(g => new GpuInfoDto
            {
                Model = Text(g, "Name")?.Trim() ?? "Unknown adapter",
                DriverVersion = Text(g, "DriverVersion"),
                DedicatedMemoryBytes = g["AdapterRAM"] is null ? null : (long)Convert.ToUInt64(g["AdapterRAM"], CultureInfo.InvariantCulture)
            }).ToList(),
            MemoryModules = memory.Select(m => new MemoryModuleDto
            {
                Slot = Text(m, "DeviceLocator"),
                Manufacturer = Text(m, "Manufacturer"),
                CapacityBytes = (long)ULong(m, "Capacity"),
                SpeedMhz = m["Speed"] is null ? null : Int(m, "Speed"),
                PartNumber = Text(m, "PartNumber")?.Trim()
            }).ToList(),
            Disks = CollectDisks(),
            Adapters = CollectAdapters(),
            CollectedUtc = DateTimeOffset.UtcNow
        };
    }

    private static IReadOnlyList<DiskInfoDto> CollectDisks()
    {
        var results = new List<DiskInfoDto>();
        try
        {
            foreach (var drive in Query("root\\cimv2",
                         "SELECT Model, SerialNumber, MediaType, Size, Status FROM Win32_DiskDrive"))
            {
                using (drive)
                {
                    var model = Text(drive, "Model")?.Trim() ?? "Disk";
                    var serial = Text(drive, "SerialNumber")?.Trim();
                    var mediaType = Text(drive, "MediaType");
                    var health = Text(drive, "Status");
                    var size = (long)ULong(drive, "Size");
                    string? driveLetter = null;
                    string? fileSystem = null;
                    long free = 0;

                    try
                    {
                        foreach (ManagementObject partition in drive.GetRelated("Win32_DiskPartition"))
                        {
                            using (partition)
                            {
                                foreach (ManagementObject logical in partition.GetRelated("Win32_LogicalDisk"))
                                {
                                    using (logical)
                                    {
                                        driveLetter = Text(logical, "DeviceID");
                                        fileSystem = Text(logical, "FileSystem");
                                        free = (long)ULong(logical, "FreeSpace");
                                        if (size <= 0)
                                        {
                                            size = (long)ULong(logical, "Size");
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Association data is not always available; the physical disk row is still useful.
                    }

                    results.Add(new DiskInfoDto
                    {
                        Model = model,
                        SerialNumber = serial,
                        MediaType = mediaType,
                        SizeBytes = size,
                        FreeBytes = free,
                        FileSystem = fileSystem,
                        HealthStatus = health,
                        DriveLetter = driveLetter
                    });
                }
            }
        }
        catch
        {
            // Fall back to logical volumes only.
        }

        if (results.Count == 0)
        {
            foreach (var logical in Query("root\\cimv2", "SELECT DeviceID, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3"))
            {
                using (logical)
                {
                    results.Add(new DiskInfoDto
                    {
                        Model = $"Volume {Text(logical, "DeviceID")}",
                        DriveLetter = Text(logical, "DeviceID"),
                        FileSystem = Text(logical, "FileSystem"),
                        SizeBytes = (long)ULong(logical, "Size"),
                        FreeBytes = (long)ULong(logical, "FreeSpace")
                    });
                }
            }
        }

        return results;
    }

    private static IReadOnlyList<NetworkAdapterInfoDto> CollectAdapters()
    {
        var results = new List<NetworkAdapterInfoDto>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                string? mac = null;
                try
                {
                    mac = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                }
                catch
                {
                    // Some virtual adapters have no MAC address.
                }

                var addresses = new List<string>();
                try
                {
                    addresses.AddRange(nic.GetIPProperties().UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.Address.ToString()));
                }
                catch
                {
                    // Ignore adapters that cannot report addresses.
                }

                results.Add(new NetworkAdapterInfoDto
                {
                    Name = nic.Name,
                    Description = nic.Description,
                    MacAddress = mac,
                    LinkSpeedBps = nic.Speed > 0 ? nic.Speed : null,
                    IsUp = nic.OperationalStatus == OperationalStatus.Up,
                    AdapterType = nic.NetworkInterfaceType.ToString(),
                    IpAddresses = addresses
                });
            }
        }
        catch
        {
            // Adapter enumeration is best-effort.
        }

        return results;
    }

    private static (long Total, long Available) ReadMemory()
    {
        try
        {
            var status = new MemoryStatusEx { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status))
            {
                return (0, 0);
            }

            return ((long)status.ullTotalPhys, (long)status.ullAvailPhys);
        }
        catch
        {
            return (0, 0);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    private static IEnumerable<ManagementObject> Query(string scope, string query)
    {
        using var searcher = new ManagementObjectSearcher(scope, query);
        foreach (var item in searcher.Get())
        {
            yield return (ManagementObject)item;
        }
    }

    private static string? Text(ManagementBaseObject? mo, string name)
    {
        if (mo is null)
        {
            return null;
        }

        try
        {
            return mo[name]?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static int Int(ManagementBaseObject mo, string name)
    {
        try
        {
            return mo[name] is null ? 0 : Convert.ToInt32(mo[name], CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static ulong ULong(ManagementBaseObject? mo, string name)
    {
        if (mo is null)
        {
            return 0;
        }

        try
        {
            return mo[name] is null ? 0 : Convert.ToUInt64(mo[name], CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }
}
