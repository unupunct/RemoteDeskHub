using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Core.Models;

/// <summary>Account information safe to expose to the management UI (never contains password material).</summary>
public sealed class UserAccountView
{
    public Guid Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public UserRole Role { get; init; }
    public bool IsEnabled { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime? LastLoginUtc { get; init; }
    public int FailedLoginCount { get; init; }
    public DateTime? LockoutUntilUtc { get; init; }
}

public sealed class ComputerSummary
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? FriendlyName { get; init; }
    public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName) ? Name : $"{FriendlyName} ({Name})";
    public string? IpAddress { get; init; }
    public string? MacAddress { get; init; }
    public string? OperatingSystem { get; init; }
    public string? OsVersion { get; init; }
    public string? MachineId { get; init; }
    public string? AgentVersion { get; init; }
    public ComputerStatus Status { get; init; }
    public bool IsManaged { get; init; }
    public bool IsTrusted { get; init; }
    public bool IsRegistered { get; init; }
    public string? GroupName { get; init; }
    public Guid? GroupId { get; init; }
    public double CpuPercent { get; init; }
    public double MemoryPercent { get; init; }
    public double DiskPercent { get; init; }
    public double? CpuTemperatureC { get; init; }
    public long MemoryTotalBytes { get; init; }
    public long UptimeSeconds { get; init; }
    public DateTime? LastSeenUtc { get; init; }
    public DateTime? LastBootUtc { get; init; }
    public string? Notes { get; init; }
    public string? Description { get; init; }
    public string? TeamViewerId { get; init; }
    public string? AnyDeskId { get; init; }
    public string? LmViewerId { get; init; }
    public string? RustDeskId { get; init; }
    public int ActiveAlertCount { get; init; }
    public AlertSeverity HighestAlert { get; init; }
    public bool IsOnline => Status == ComputerStatus.Online || Status == ComputerStatus.Warning || Status == ComputerStatus.Critical;
    public string StatusText => Status switch
    {
        ComputerStatus.Online => "Online",
        ComputerStatus.Offline => "Offline",
        ComputerStatus.Warning => "Warning",
        ComputerStatus.Critical => "Critical",
        ComputerStatus.PendingApproval => "Pending approval",
        _ => "Unknown"
    };
}

public sealed class ComputerDetail
{
    public ComputerSummary Summary { get; init; } = new();
    public Guid? AgentId { get; init; }
    public AgentState? AgentState { get; init; }
    public string? AgentHealth { get; init; }
    public string? MacAddress { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? Architecture { get; init; }
    public HardwareInventoryDto? Hardware { get; init; }
    public DateTime? HardwareCollectedUtc { get; init; }
    public int SoftwareCount { get; init; }
    public DateTime? LastInventoryUtc { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
    public double? ConnectionQualityPercent { get; init; }
}

public sealed class HardwareInventoryHistoryView
{
    public Guid Id { get; init; }
    public DateTime CollectedUtc { get; init; }
    public string? ChangeSummary { get; init; }
    public string Source { get; init; } = string.Empty;
    public string CollectedText => CollectedUtc.ToLocalTime().ToString("g");
}

public sealed class DashboardSnapshot
{
    public int TotalComputers { get; init; }
    public int OnlineComputers { get; init; }
    public int OfflineComputers { get; init; }
    public int WarningComputers { get; init; }
    public int CriticalComputers { get; init; }
    public int PendingAgents { get; init; }
    public int ActiveRemoteSessions { get; init; }
    public double TotalNetworkInBps { get; init; }
    public double TotalNetworkOutBps { get; init; }
    public int ActiveAlerts { get; init; }
    public int UnacknowledgedAlerts { get; init; }
    public int ManagedComputers { get; init; }
    public int DiscoveredDevices { get; init; }
    public IReadOnlyList<ComputerSummary> Computers { get; init; } = [];
    public IReadOnlyList<AlertView> RecentAlerts { get; init; } = [];
    public IReadOnlyList<AuditView> RecentActions { get; init; } = [];
    public IReadOnlyList<DiscoveryView> RecentDiscovery { get; init; } = [];
}

public sealed class AlertView
{
    public Guid Id { get; init; }
    public Guid? ComputerId { get; init; }
    public string? ComputerName { get; init; }
    public AlertSeverity Severity { get; init; }
    public AlertMetricType MetricType { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime? AcknowledgedUtc { get; init; }
    public string? AcknowledgedBy { get; init; }
    public string SeverityText => Severity.ToString();
    public string StatusText => AcknowledgedUtc.HasValue ? "Acknowledged" : IsActive ? "Active" : "Resolved";
}

public sealed class AlertRuleView
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public AlertMetricType MetricType { get; init; }
    public double Threshold { get; init; }
    public AlertSeverity Severity { get; init; }
    public bool Enabled { get; init; }
    public Guid? ScopeComputerId { get; init; }
    public Guid? ScopeGroupId { get; init; }
    public string? ScopeName { get; init; }
    public int CooldownMinutes { get; init; }
}

public sealed class AuditView
{
    public long Id { get; init; }
    public DateTime TimestampUtc { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string? ComputerName { get; init; }
    public string Operation { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public string? Error { get; init; }
    public string? Details { get; init; }
    public string? IpAddress { get; init; }
}

public sealed class DiscoveryView
{
    public Guid Id { get; init; }
    public string IpAddress { get; init; } = string.Empty;
    public string? MacAddress { get; init; }
    public string? Hostname { get; init; }
    public string? Vendor { get; init; }
    public string? OperatingSystem { get; init; }
    public double? ResponseTimeMs { get; init; }
    public string? OpenServices { get; init; }
    public bool HasAgent { get; init; }
    public bool IsRegistered { get; init; }
    public bool IsTrusted { get; init; }
    public string? FriendlyName { get; init; }
    public string? Notes { get; init; }
    public Guid? ComputerId { get; init; }
    public DateTime FirstSeenUtc { get; init; }
    public DateTime LastSeenUtc { get; init; }
    public string DisplayName => FriendlyName ?? Hostname ?? IpAddress;
}

public sealed class MetricSample
{
    public DateTime TimestampUtc { get; init; }
    public double CpuPercent { get; init; }
    public double MemoryPercent { get; init; }
    public double DiskPercent { get; init; }
    public double NetworkInBps { get; init; }
    public double NetworkOutBps { get; init; }
    public double? CpuTemperatureC { get; init; }
    public long UptimeSeconds { get; init; }
}

public sealed class MonitoringSeries
{
    public Guid ComputerId { get; init; }
    public string ComputerName { get; init; } = string.Empty;
    public List<MetricSample> Samples { get; init; } = [];
}

public sealed class GroupView
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Kind { get; init; } = "Custom";
    public bool IsSystem { get; init; }
    public int ComputerCount { get; init; }
}

public sealed class FileTransferView
{
    public Guid Id { get; init; }
    public string ComputerName { get; init; } = string.Empty;
    public string Actor { get; init; } = string.Empty;
    public TransferDirection Direction { get; init; }
    public string LocalPath { get; init; } = string.Empty;
    public string RemotePath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public long TransferredBytes { get; init; }
    public string? Sha256 { get; init; }
    public TransferStatus Status { get; init; }
    public DateTime StartedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public string? Error { get; init; }
    public double? SpeedBps { get; init; }
}

public sealed class CommandHistoryView
{
    public Guid Id { get; init; }
    public string ComputerName { get; init; } = string.Empty;
    public string Actor { get; init; } = string.Empty;
    public CommandShell Shell { get; init; }
    public string Command { get; init; } = string.Empty;
    public DateTime StartedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public CommandStatus Status { get; init; }
    public int? ExitCode { get; init; }
    public string? StdOut { get; init; }
    public string? StdErr { get; init; }
    public long? DurationMs { get; init; }
    public string? Error { get; init; }
}

public sealed class CommandApprovalView
{
    public Guid Id { get; init; }
    public Guid? ComputerId { get; init; }
    public string ComputerName { get; init; } = string.Empty;
    public string RequestedBy { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public CommandShell Shell { get; init; }
    public int TimeoutSeconds { get; init; }
    public bool RequireElevation { get; init; }
    public CommandApprovalStatus Status { get; init; }
    public DateTime RequestedUtc { get; init; }
    public DateTime ExpiresUtc { get; init; }
    public string? ReviewedBy { get; init; }
    public DateTime? ReviewedUtc { get; init; }
    public string? ReviewNote { get; init; }
    public DateTime? ExecutedUtc { get; init; }
    public bool IsPending => Status == CommandApprovalStatus.Pending;
}

public sealed class AutomationTaskView
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool Enabled { get; init; }
    public AutomationTriggerType TriggerType { get; init; }
    public AutomationActionType ActionType { get; init; }
    public string? TargetName { get; init; }
    public int? IntervalMinutes { get; init; }
    public string? DailyTime { get; init; }
    public DateTime? LastRunUtc { get; init; }
    public DateTime? NextRunUtc { get; init; }
    public string? LastResult { get; init; }
}

public sealed class AutomationExecutionView
{
    public Guid Id { get; init; }
    public Guid TaskId { get; init; }
    public string TaskName { get; init; } = string.Empty;
    public DateTime StartedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public AutomationRunStatus Status { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public string? Log { get; init; }
    public string TriggeredBy { get; init; } = string.Empty;
}

public sealed class SharedFolderStatusView
{
    public bool Enabled { get; init; }
    public string RootPath { get; init; } = @"C:\zzz";
    public DateTime? LastScanUtc { get; init; }
    public DateTime? LastCompletedUtc { get; init; }
    public string? LastError { get; init; }
    public DateTime? LastErrorUtc { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    public int AgentCount { get; init; }
    public int ConflictCopyCount { get; init; }
}

public sealed class SharedFolderFileView
{
    public string Path { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public long Version { get; init; }
    public string? SourceComputerName { get; init; }
    public DateTime UpdatedUtc { get; init; }
    public string SizeText => FormatBytes(SizeBytes);
    public string SourceText => string.IsNullOrWhiteSpace(SourceComputerName) ? "Hub" : SourceComputerName;

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

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
}

public sealed class SharedFolderAgentView
{
    public Guid ComputerId { get; init; }
    public string ComputerName { get; init; } = string.Empty;
    public int FileCount { get; init; }
    public DateTime? LastObservedUtc { get; init; }
    public bool Online { get; init; }
    public string OnlineText => Online ? "Online" : "Offline";
    public string ObservedText => LastObservedUtc is null
        ? "Not scanned yet"
        : LastObservedUtc.Value.ToLocalTime().ToString("g");
}

public sealed class BulkOperationProgress
{
    public string Operation { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }
    public string? CurrentComputer { get; init; }
    public double Percent => Total == 0 ? 0 : (Completed + Failed) * 100.0 / Total;
    public IReadOnlyList<BulkResult> Results { get; init; } = [];
}

public sealed record BulkResult(Guid ComputerId, string ComputerName, bool Success, string Message);

public sealed class ReportFile(string fileName, string contentType, byte[] content)
{
    public string FileName { get; } = fileName;
    public string ContentType { get; } = contentType;
    public byte[] Content { get; } = content;
}

public sealed class DiscoveryScanRequest
{
    public string Mode { get; init; } = "range";
    public string? Range { get; init; }
    public string? Cidr { get; init; }
    public bool IncludeLocalSubnet { get; init; } = true;
    public int TimeoutMs { get; init; } = 800;
    public int Concurrency { get; init; } = 24;
    public IReadOnlyList<int> ProbePorts { get; init; } = [445, 3389, HubConstants.DefaultAgentPort];
}

public sealed class DiscoveryScanResult
{
    public int HostsScanned { get; init; }
    public int DevicesFound { get; init; }
    public int NewDevices { get; init; }
    public int AgentsFound { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<DiscoveryView> Devices { get; init; } = [];
}

public sealed class ProcessTerminateOutcome
{
    public Guid ComputerId { get; init; }
    public int Pid { get; init; }
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class RemoteAssistanceTarget
{
    public Guid ComputerId { get; init; }
    public string ComputerName { get; init; } = string.Empty;
    public string? IpAddress { get; init; }
    public string? OperatingSystem { get; init; }
    public string? TeamViewerId { get; init; }
    public string? AnyDeskId { get; init; }
    public string? LmViewerId { get; init; }
    public string? RustDeskId { get; init; }
    public bool Online { get; init; }

    public string SavedRemoteAccessText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(TeamViewerId)) parts.Add($"TeamViewer {TeamViewerId}");
            if (!string.IsNullOrWhiteSpace(AnyDeskId)) parts.Add($"AnyDesk {AnyDeskId}");
            if (!string.IsNullOrWhiteSpace(LmViewerId)) parts.Add($"LM {LmViewerId}");
            if (!string.IsNullOrWhiteSpace(RustDeskId)) parts.Add($"RustDesk {RustDeskId}");
            return parts.Count == 0 ? "No saved remote-access IDs" : string.Join(" · ", parts);
        }
    }

    public string? GetConnectionTarget(RemoteToolKind kind) => kind switch
    {
        RemoteToolKind.TeamViewer => TeamViewerId,
        RemoteToolKind.AnyDesk => AnyDeskId,
        RemoteToolKind.LmViewer => string.IsNullOrWhiteSpace(LmViewerId) ? IpAddress : LmViewerId,
        RemoteToolKind.RustDesk => RustDeskId,
        _ => IpAddress
    };
}

public sealed class ExportRequest
{
    public string ReportName { get; init; } = string.Empty;
    public string Format { get; init; } = "csv";
    public IReadOnlyList<Guid>? ComputerIds { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
}
