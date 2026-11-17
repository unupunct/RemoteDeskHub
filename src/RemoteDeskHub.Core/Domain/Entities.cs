using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Core.Domain;

public class Computer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? FriendlyName { get; set; }
    public string? Description { get; set; }
    public string? IpAddress { get; set; }
    public string? MacAddress { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OsVersion { get; set; }

    /// <summary>
    /// Stable machine identity reported by the agent (Windows MachineGuid). This is deliberately
    /// separate from the IP address so a DHCP lease change does not create a second computer.
    /// </summary>
    public string? MachineId { get; set; }
    public string? Architecture { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public Guid? GroupId { get; set; }
    public ComputerGroup? Group { get; set; }
    public bool IsRegistered { get; set; }
    public bool IsTrusted { get; set; }
    public bool IsManaged { get; set; }
    public ComputerStatus Status { get; set; } = ComputerStatus.Unknown;
    public string? AgentVersion { get; set; }
    public DateTime? FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenUtc { get; set; }
    public DateTime? LastBootUtc { get; set; }
    public double CpuPercent { get; set; }
    public double MemoryPercent { get; set; }
    public double DiskPercent { get; set; }
    public double? CpuTemperatureC { get; set; }
    public long MemoryTotalBytes { get; set; }
    public long UptimeSeconds { get; set; }
    public long? TotalDiskBytes { get; set; }
    public long? FreeDiskBytes { get; set; }
    public string? Notes { get; set; }
    public string? Tags { get; set; }
    public string? TeamViewerId { get; set; }
    public string? AnyDeskId { get; set; }
    public string? LmViewerId { get; set; }
    public string? RustDeskId { get; set; }
    public string Source { get; set; } = "Agent";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public Agent? Agent { get; set; }
    public HardwareInventory? Hardware { get; set; }
}

public class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ComputerId { get; set; }
    public Computer? Computer { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string? MachineId { get; set; }
    public AgentState State { get; set; } = AgentState.PendingApproval;
    public string? CertificateThumbprint { get; set; }
    public string? CertificatePem { get; set; }
    public string? PublicKeyPem { get; set; }
    public string? Version { get; set; }
    public string? OperatingSystem { get; set; }
    public string? LastKnownIp { get; set; }
    public int ListeningPort { get; set; } = HubConstants.DefaultAgentPort;
    public Guid? InstallationId { get; set; }
    public DateTime EnrolledUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedUtc { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
    public string? RevocationReason { get; set; }
    public string? LastError { get; set; }
    public string? HealthStatus { get; set; }
}

public class AgentEnrollment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TokenHash { get; set; } = string.Empty;
    public string TokenPreview { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; } = DateTime.UtcNow.AddMinutes(30);
    public DateTime? ConsumedUtc { get; set; }
    public EnrollmentState Status { get; set; } = EnrollmentState.Pending;
    public Guid? AgentId { get; set; }
    public Guid? ComputerId { get; set; }
    public string? ComputerName { get; set; }
    public string? RequestedIp { get; set; }
    public string? PublicKeyPem { get; set; }
    public string? CsrPem { get; set; }
    public string? CertificatePem { get; set; }
    public string? Nonce { get; set; }
    public Guid? InstallationId { get; set; }
    public string? MachineId { get; set; }
    public int ManagementPort { get; set; } = HubConstants.DefaultAgentPort;
    public bool AutoApprove { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedUtc { get; set; }
    public string? RejectReason { get; set; }
    public string? Note { get; set; }
}

public class ComputerGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Kind { get; set; } = "Custom";
    public bool IsSystem { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public ICollection<ComputerGroupMembership> Memberships { get; set; } = [];
}

public class ComputerGroupMembership
{
    public Guid ComputerId { get; set; }
    public Computer? Computer { get; set; }
    public Guid GroupId { get; set; }
    public ComputerGroup? Group { get; set; }
    public DateTime AssignedUtc { get; set; } = DateTime.UtcNow;
}

public class HardwareInventory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ComputerId { get; set; }
    public Computer? Computer { get; set; }
    public string? CpuModel { get; set; }
    public string? CpuManufacturer { get; set; }
    public int CpuCores { get; set; }
    public int CpuLogicalProcessors { get; set; }
    public int? CpuMaxClockMhz { get; set; }
    public long RamTotalBytes { get; set; }
    public long RamAvailableBytes { get; set; }
    public string? MotherboardManufacturer { get; set; }
    public string? MotherboardModel { get; set; }
    public string? BiosVersion { get; set; }
    public string? BiosManufacturer { get; set; }
    public string? GpusJson { get; set; }
    public string? MemoryModulesJson { get; set; }
    public string? DisksJson { get; set; }
    public string? AdaptersJson { get; set; }
    public string? RawJson { get; set; }
    public DateTime CollectedUtc { get; set; } = DateTime.UtcNow;
}

public class HardwareInventorySnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ComputerId { get; set; }
    public Computer? Computer { get; set; }
    public string SnapshotJson { get; set; } = string.Empty;
    public string? ChangeSummary { get; set; }
    public string Source { get; set; } = "Refresh";
    public DateTime CollectedUtc { get; set; } = DateTime.UtcNow;
}

public class SoftwareInventoryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ComputerId { get; set; }
    public Computer? Computer { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public string? InstallDate { get; set; }
    public string? InstallLocation { get; set; }
    public long? EstimatedSizeBytes { get; set; }
    public string? Architecture { get; set; }
    public string? RegistryKey { get; set; }
    public bool IsPresent { get; set; } = true;
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime ScanUtc { get; set; } = DateTime.UtcNow;
}

public class PerformanceMetric
{
    public long Id { get; set; }
    public Guid ComputerId { get; set; }
    public Computer? Computer { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public double CpuPercent { get; set; }
    public double MemoryPercent { get; set; }
    public double DiskPercent { get; set; }
    public double NetworkInBps { get; set; }
    public double NetworkOutBps { get; set; }
    public double? CpuTemperatureC { get; set; }
    public long UptimeSeconds { get; set; }
    public int ProcessCount { get; set; }
}

public class Alert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ComputerId { get; set; }
    public Computer? Computer { get; set; }
    public string? ComputerName { get; set; }
    public AlertSeverity Severity { get; set; } = AlertSeverity.Warning;
    public AlertMetricType MetricType { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string DedupeKey { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AcknowledgedUtc { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTime? ResolvedUtc { get; set; }
    public string? Details { get; set; }
}

public class AlertRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public AlertMetricType MetricType { get; set; }
    public double Threshold { get; set; }
    public AlertSeverity Severity { get; set; } = AlertSeverity.Warning;
    public bool Enabled { get; set; } = true;
    public Guid? ScopeComputerId { get; set; }
    public Guid? ScopeGroupId { get; set; }
    public int CooldownMinutes { get; set; } = 30;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class AuditLog
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Actor { get; set; } = string.Empty;
    public Guid? ComputerId { get; set; }
    public string? ComputerName { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string Result { get; set; } = "Success";
    public long DurationMs { get; set; }
    public string? Error { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public string PrevHash { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
}

public class CommandHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ComputerId { get; set; }
    public string ComputerName { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public CommandShell Shell { get; set; }
    public string Command { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
    public CommandStatus Status { get; set; } = CommandStatus.Queued;
    public int? ExitCode { get; set; }
    public string? StdOut { get; set; }
    public string? StdErr { get; set; }
    public long? DurationMs { get; set; }
    public bool RequireElevation { get; set; }
    public bool IsBulk { get; set; }
    public string? Error { get; set; }
}

public class CommandApproval
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ComputerId { get; set; }
    public string ComputerName { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public CommandShell Shell { get; set; }
    public int TimeoutSeconds { get; set; }
    public bool RequireElevation { get; set; }
    public CommandApprovalStatus Status { get; set; } = CommandApprovalStatus.Pending;
    public DateTime RequestedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedUtc { get; set; }
    public string? ReviewNote { get; set; }
    public DateTime? ExecutedUtc { get; set; }
}

public class FileTransfer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ComputerId { get; set; }
    public string ComputerName { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public TransferDirection Direction { get; set; }
    public string LocalPath { get; set; } = string.Empty;
    public string RemotePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public long TransferredBytes { get; set; }
    public string? Sha256 { get; set; }
    public TransferStatus Status { get; set; } = TransferStatus.Running;
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
    public string? Error { get; set; }
    public double? SpeedBps { get; set; }
}

public class RemoteSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ComputerId { get; set; }
    public string ComputerName { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public RemoteToolKind Tool { get; set; }
    public string? ConnectionInfo { get; set; }
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; set; }
    public int? ProcessId { get; set; }
    public RemoteSessionStatus Status { get; set; } = RemoteSessionStatus.Started;
    public string? Notes { get; set; }

    public TimeSpan? Duration => EndedUtc.HasValue ? EndedUtc.Value - StartedUtc : null;
    public string DurationText => Duration is not { } duration
        ? (Status == RemoteSessionStatus.Started ? "Running" : "—")
        : duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : duration.TotalMinutes >= 1
                ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
                : $"{Math.Max(0, (int)duration.TotalSeconds)}s";
}

public class AutomationTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public AutomationTriggerType TriggerType { get; set; }
    public string? TriggerConfigJson { get; set; }
    public AutomationActionType ActionType { get; set; }
    public string? ActionConfigJson { get; set; }
    public Guid? TargetComputerId { get; set; }
    public Guid? TargetGroupId { get; set; }
    public int? IntervalMinutes { get; set; }
    public string? DailyTime { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public DateTime? NextRunUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public string? LastResult { get; set; }
}

public class AutomationExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public AutomationTask? Task { get; set; }
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
    public AutomationRunStatus Status { get; set; } = AutomationRunStatus.Running;
    public string? Log { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public string TriggeredBy { get; set; } = "Scheduler";
}

public class SharedFolderFile
{
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; } = DateTime.UtcNow;
    public long Version { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? SourceComputerId { get; set; }
    public string? SourceComputerName { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public class SharedFolderAgentFile
{
    public Guid ComputerId { get; set; }
    public Computer? Computer { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string? CentralSha256 { get; set; }
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; } = DateTime.UtcNow;
    public DateTime ObservedUtc { get; set; } = DateTime.UtcNow;
}

public class SharedFolderLocalFile
{
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string? CentralSha256 { get; set; }
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; } = DateTime.UtcNow;
    public DateTime ObservedUtc { get; set; } = DateTime.UtcNow;
}

public class SharedFolderSyncState
{
    public int Id { get; set; } = 1;
    public DateTime? LastScanUtc { get; set; }
    public DateTime? LastCompletedUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public int Files { get; set; }
    public long TotalBytes { get; set; }
    public int Agents { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public class ApplicationSetting
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string? Category { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public class UserAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public int Iterations { get; set; } = 210_000;
    public UserRole Role { get; set; } = UserRole.Administrator;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginUtc { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutUntilUtc { get; set; }
}

public class DiscoveryDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IpAddress { get; set; } = string.Empty;
    public string? MacAddress { get; set; }
    public string? Hostname { get; set; }
    public string? Vendor { get; set; }
    public string? OperatingSystem { get; set; }
    public double? ResponseTimeMs { get; set; }
    public string? OpenServicesCsv { get; set; }
    public bool HasAgent { get; set; }
    public bool IsRegistered { get; set; }
    public bool IsTrusted { get; set; }
    public string? FriendlyName { get; set; }
    public string? Notes { get; set; }
    public Guid? ComputerId { get; set; }
    public Guid? GroupId { get; set; }
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastScanUtc { get; set; } = DateTime.UtcNow;
}

public class RemoteAssistanceProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public RemoteToolKind Tool { get; set; }
    public Guid? TargetComputerId { get; set; }
    public string? Arguments { get; set; }
    public string? Instructions { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class SchemaVersion
{
    public int Id { get; set; }
    public string Version { get; set; } = string.Empty;
    public DateTime AppliedUtc { get; set; } = DateTime.UtcNow;
}
