using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Agent.Collectors;

/// <summary>
/// Collects live performance metrics directly from Windows. No simulated values: when a metric
/// cannot be read on this machine it is reported as zero or null.
/// </summary>
public sealed class MetricsCollector
{
    private readonly object _gate = new();

    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasCpuSample;

    private long _prevReceived;
    private long _prevSent;
    private DateTime _prevNetworkUtc = DateTime.MinValue;

    private double? _cachedTemperature;
    private DateTime _temperatureCollectedUtc = DateTime.MinValue;
    private bool _temperatureInFlight;

    private int _cachedServiceCount;
    private DateTime _serviceCountUtc = DateTime.MinValue;

    public AgentMetricsDto Collect()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var cpu = ReadCpuPercent();
            var (memoryTotal, memoryAvailable) = ReadMemory();
            var (disks, diskTotal, diskFree, diskPercent) = ReadDisks();
            var (inBps, outBps) = ReadNetwork(now);

            return new AgentMetricsDto
            {
                TimestampUtc = now,
                CpuPercent = Math.Round(cpu, 1),
                MemoryPercent = memoryTotal > 0 ? Math.Round((memoryTotal - memoryAvailable) * 100.0 / memoryTotal, 1) : 0,
                MemoryTotalBytes = memoryTotal,
                MemoryAvailableBytes = memoryAvailable,
                DiskPercent = Math.Round(diskPercent, 1),
                DiskTotalBytes = diskTotal,
                DiskFreeBytes = diskFree,
                NetworkInBps = Math.Round(inBps, 1),
                NetworkOutBps = Math.Round(outBps, 1),
                CpuTemperatureC = ReadTemperature(now),
                UptimeSeconds = GetSystemUptimeSeconds(),
                ProcessCount = CountProcesses(),
                ServiceCount = CountServices(now),
                Disks = disks
            };
        }
    }

    private double ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return 0;
        }

        var idle = ToUInt64(idleTime);
        var kernel = ToUInt64(kernelTime);
        var user = ToUInt64(userTime);
        if (!_hasCpuSample)
        {
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _hasCpuSample = true;
            return 0;
        }

        var idleDelta = idle - _prevIdle;
        var totalDelta = kernel - _prevKernel + (user - _prevUser);
        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;
        return totalDelta <= 0 ? 0 : Math.Clamp((totalDelta - idleDelta) * 100.0 / totalDelta, 0, 100);
    }

    private static (long Total, long Available) ReadMemory()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return (0, 0);
        }

        return ((long)status.ullTotalPhys, (long)status.ullAvailPhys);
    }

    private static (IReadOnlyList<DiskMetricsDto> Disks, long Total, long Free, double Percent) ReadDisks()
    {
        var disks = new List<DiskMetricsDto>();
        long total = 0;
        long free = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                {
                    continue;
                }

                disks.Add(new DiskMetricsDto(drive.Name.TrimEnd('\\'), drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace));
                total += drive.TotalSize;
                free += drive.AvailableFreeSpace;
            }
            catch
            {
                // A drive can disappear between enumeration and inspection.
            }
        }

        var percent = total > 0 ? (total - free) * 100.0 / total : 0;
        return (disks, total, free, percent);
    }

    private (double InBps, double OutBps) ReadNetwork(DateTime now)
    {
        long received = 0;
        long sent = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var stats = nic.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
        }
        catch
        {
            // Statistics are best-effort.
        }

        var inBps = 0.0;
        var outBps = 0.0;
        if (_prevNetworkUtc != DateTime.MinValue)
        {
            var seconds = (now - _prevNetworkUtc).TotalSeconds;
            if (seconds > 0.1)
            {
                inBps = Math.Max(0, received - _prevReceived) / seconds;
                outBps = Math.Max(0, sent - _prevSent) / seconds;
            }
        }

        _prevReceived = received;
        _prevSent = sent;
        _prevNetworkUtc = now;
        return (inBps, outBps);
    }

    private static int CountProcesses()
    {
        try
        {
            return System.Diagnostics.Process.GetProcesses().Length;
        }
        catch
        {
            return 0;
        }
    }

    private int CountServices(DateTime now)
    {
        if (_serviceCountUtc != DateTime.MinValue && now - _serviceCountUtc < TimeSpan.FromSeconds(60))
        {
            return _cachedServiceCount;
        }

        try
        {
            _cachedServiceCount = System.ServiceProcess.ServiceController.GetServices().Length;
            _serviceCountUtc = now;
        }
        catch
        {
            // Leave the last known value.
        }

        return _cachedServiceCount;
    }

    private double? ReadTemperature(DateTime now)
    {
        if (_temperatureCollectedUtc != DateTime.MinValue && now - _temperatureCollectedUtc < TimeSpan.FromSeconds(60))
        {
            return _cachedTemperature;
        }

        if (!_temperatureInFlight)
        {
            _temperatureInFlight = true;
            var task = Task.Run(() =>
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                    foreach (var item in searcher.Get())
                    {
                        using var mo = (ManagementObject)item;
                        var raw = Convert.ToDouble(mo["CurrentTemperature"], CultureInfo.InvariantCulture);
                        var celsius = raw / 10.0 - 273.15;
                        if (celsius is > -20 and < 150)
                        {
                            return (double?)Math.Round(celsius, 1);
                        }
                    }
                }
                catch
                {
                    // Thermal zone data is frequently unavailable on desktops.
                }

                return null;
            });

            if (task.Wait(TimeSpan.FromSeconds(3)))
            {
                _cachedTemperature = task.Result;
                _temperatureCollectedUtc = now;
            }

            _temperatureInFlight = false;
        }

        return _cachedTemperature;
    }

    private static long GetSystemUptimeSeconds()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return (long)uptime.TotalSeconds;
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
