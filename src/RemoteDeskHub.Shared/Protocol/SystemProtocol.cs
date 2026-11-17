namespace RemoteDeskHub.Shared.Protocol;

public sealed record AgentMetricsDto
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public double CpuPercent { get; init; }
    public double MemoryPercent { get; init; }
    public long MemoryTotalBytes { get; init; }
    public long MemoryAvailableBytes { get; init; }
    public double DiskPercent { get; init; }
    public long DiskTotalBytes { get; init; }
    public long DiskFreeBytes { get; init; }
    public double NetworkInBps { get; init; }
    public double NetworkOutBps { get; init; }
    public double? CpuTemperatureC { get; init; }
    public long UptimeSeconds { get; init; }
    public int ProcessCount { get; init; }
    public int ServiceCount { get; init; }
    public IReadOnlyList<DiskMetricsDto> Disks { get; init; } = [];
}

public sealed record DiskMetricsDto(string Name, string FileSystem, long TotalBytes, long FreeBytes);

public sealed record GpuInfoDto
{
    public required string Model { get; init; }
    public string? DriverVersion { get; init; }
    public long? DedicatedMemoryBytes { get; init; }
    public double? UtilizationPercent { get; init; }
}

public sealed record MemoryModuleDto
{
    public string? Slot { get; init; }
    public string? Manufacturer { get; init; }
    public long CapacityBytes { get; init; }
    public int? SpeedMhz { get; init; }
    public string? PartNumber { get; init; }
}

public sealed record DiskInfoDto
{
    public required string Model { get; init; }
    public string? SerialNumber { get; init; }
    public string? MediaType { get; init; }
    public long SizeBytes { get; init; }
    public long FreeBytes { get; init; }
    public string? FileSystem { get; init; }
    public string? HealthStatus { get; init; }
    public string? DriveLetter { get; init; }
}

public sealed record NetworkAdapterInfoDto
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? MacAddress { get; init; }
    public long? LinkSpeedBps { get; init; }
    public bool IsUp { get; init; }
    public string? AdapterType { get; init; }
    public IReadOnlyList<string> IpAddresses { get; init; } = [];
}

public sealed record HardwareInventoryDto
{
    public string? CpuModel { get; init; }
    public string? CpuManufacturer { get; init; }
    public int CpuCores { get; init; }
    public int CpuLogicalProcessors { get; init; }
    public int? CpuMaxClockMhz { get; init; }
    public long RamTotalBytes { get; init; }
    public long RamAvailableBytes { get; init; }
    public string? MotherboardManufacturer { get; init; }
    public string? MotherboardModel { get; init; }
    public string? BiosVersion { get; init; }
    public string? BiosManufacturer { get; init; }
    public IReadOnlyList<GpuInfoDto> Gpus { get; init; } = [];
    public IReadOnlyList<MemoryModuleDto> MemoryModules { get; init; } = [];
    public IReadOnlyList<DiskInfoDto> Disks { get; init; } = [];
    public IReadOnlyList<NetworkAdapterInfoDto> Adapters { get; init; } = [];
    public DateTimeOffset CollectedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SoftwareItemDto
{
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public string? InstallDate { get; init; }
    public string? InstallLocation { get; init; }
    public long? EstimatedSizeBytes { get; init; }
    public string? Architecture { get; init; }
    public string? RegistryKey { get; init; }
}

public sealed record SoftwareInventoryDto
{
    public IReadOnlyList<SoftwareItemDto> Items { get; init; } = [];
    public DateTimeOffset CollectedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RemoteProcessDto
{
    public required string Name { get; init; }
    public int Pid { get; init; }
    public int SessionId { get; init; }
    public double CpuPercent { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateMemoryBytes { get; init; }
    public string? UserName { get; init; }
    public DateTimeOffset? StartTimeUtc { get; init; }
    public string? ExecutablePath { get; init; }
    public string? CommandLine { get; init; }
    public required string Status { get; init; }
    public bool IsProtected { get; init; }
}

public sealed record RemoteServiceDto
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required string Status { get; init; }
    public required string StartupType { get; init; }
    public string? Account { get; init; }
    public string? ExecutablePath { get; init; }
    public bool CanStop { get; init; }
    public bool CanPauseAndContinue { get; init; }
    public bool IsCritical { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyList<string> DependentServices { get; init; } = [];
}

public sealed record RemoteEventLogDto
{
    public required string LogName { get; init; }
    public DateTimeOffset TimeCreatedUtc { get; init; }
    public required string Level { get; init; }
    public string? Provider { get; init; }
    public int EventId { get; init; }
    public required string Message { get; init; }
}

public sealed record ProcessTerminateRequest(bool Confirmed);
public sealed record ServiceActionRequest(string Action, string? StartupType, bool Confirmed);
public sealed record FilePathRequest(string Path);
public sealed record FileDeleteRequest(string Path, bool Recursive, bool Confirmed);
public sealed record FileRenameRequest(string Path, string NewName, bool Confirmed);
public sealed record FileCreateDirectoryRequest(string Path);
