namespace RemoteDeskHub.Shared;

/// <summary>Enumerations shared by the hub, the desktop console and the agent.</summary>
public enum ComputerStatus
{
    Unknown = 0,
    Online = 1,
    Offline = 2,
    Warning = 3,
    Critical = 4,
    PendingApproval = 5
}

public enum AgentState
{
    PendingApproval = 0,
    Active = 1,
    Revoked = 2,
    Rejected = 3
}

public enum EnrollmentState
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Expired = 3,
    Cancelled = 4
}

public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

public enum AlertMetricType
{
    CpuUsage = 0,
    MemoryUsage = 1,
    DiskFree = 2,
    ComputerOffline = 3,
    AgentUnavailable = 4,
    Temperature = 5,
    NetworkTraffic = 6,
    ServiceStopped = 7,
    UnexpectedReboot = 8
}

public enum UserRole
{
    Viewer = 0,
    Operator = 1,
    Administrator = 2
}

public enum CommandShell
{
    PowerShell = 0,
    Cmd = 1
}

public enum CommandStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    TimedOut = 4,
    Cancelled = 5
}

public enum CommandApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Expired = 3,
    Executed = 4,
    Failed = 5,
    Executing = 6
}

public enum TransferDirection
{
    Upload = 0,
    Download = 1
}

public enum TransferStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3,
    Interrupted = 4
}

public enum RemoteToolKind
{
    Rdp = 0,
    QuickAssist = 1,
    WindowsRemoteAssistance = 2,
    ThirdParty = 3,
    TeamViewer = 4,
    AnyDesk = 5,
    LmViewer = 6,
    RustDesk = 7
}

public enum RemoteSessionStatus
{
    Started = 0,
    Ended = 1,
    Failed = 2,
    Prepared = 3
}

public enum AutomationTriggerType
{
    Manual = 0,
    Interval = 1,
    Daily = 2,
    ComputerOffline = 3,
    ServiceStopped = 4,
    DiskSpaceLow = 5
}

public enum AutomationActionType
{
    CheckDiskSpace = 0,
    CheckOnline = 1,
    CollectInventory = 2,
    CheckService = 3,
    GenerateReport = 4,
    RunDiagnostic = 5,
    RunCommand = 6,
    Notify = 7
}

public enum AutomationRunStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    PartiallySucceeded = 3,
    Cancelled = 4
}

public enum MetricKind
{
    Cpu = 0,
    Memory = 1,
    Disk = 2,
    NetworkIn = 3,
    NetworkOut = 4,
    Temperature = 5
}

public static class HubConstants
{
    public const string ProductName = "RemoteDesk Hub";
    public const string AgentProductName = "RemoteDesk Agent";
    public const string ProductVersion = "1.0.0";
    public const string AgentServiceName = "RemoteDeskAgent";
    public const string AgentServiceDisplayName = "RemoteDesk Agent";
    public const string AgentServiceDescription = "RemoteDesk Agent - secure LAN management agent for RemoteDesk Hub.";
    public const int DefaultHubPort = 7443;
    public const int DefaultAgentPort = 7444;
    public const string DataFolderName = "RemoteDeskHub";
    public const string AgentDataFolderName = "RemoteDesk Agent";
    public const string DatabaseFileName = "remotedeskhub.db";
    public const string CertificateFolderName = "certs";
    public const string LogFolderName = "logs";
    public const string BackupFolderName = "backups";
    public const string DefaultAdminUserName = "admin";
    public const int MinPasswordLength = 10;
    public const int DefaultHeartbeatSeconds = 10;
    public const int DefaultMetricsSeconds = 5;
    public const int DefaultInventoryMinutes = 60;
    public const int MaxTransferChunkBytes = 1024 * 1024;
    public const int MaxCommandOutputChars = 512 * 1024;
    public const int DefaultCommandTimeoutSeconds = 120;
    public const int MaxCommandTimeoutSeconds = 3600;
    public const string AuditActorSystem = "SYSTEM";
    public const string HeaderAgentId = "X-RemoteDesk-Agent";
    public const string HeaderRequestId = "X-RemoteDesk-Request";
    public const string AgentCertificateSubjectPrefix = "CN=RemoteDesk Agent ";
    public const string HubCertificateSubject = "CN=RemoteDesk Hub";
}

public static class SettingsKeys
{
    public const string ApplicationTheme = "app.theme";
    public const string ApplicationStartWithWindows = "app.startWithWindows";
    public const string ApplicationMinimizeToTray = "app.minimizeToTray";
    public const string LastSelectedGroup = "app.lastGroup";
    public const string DefaultGroupId = "app.defaultGroupId";
    public const string HubUrl = "network.hubUrl";
    public const string HubPort = "network.hubPort";
    public const string AgentPort = "network.agentPort";
    public const string BindAddress = "network.bindAddress";
    public const string MetricsSeconds = "monitoring.metricsSeconds";
    public const string HeartbeatSeconds = "monitoring.heartbeatSeconds";
    public const string InventoryMinutes = "monitoring.inventoryMinutes";
    public const string MetricsRetentionDays = "monitoring.retentionDays";
    public const string AlertCooldownMinutes = "monitoring.alertCooldownMinutes";
    public const string AlertCpuThreshold = "monitoring.cpuThreshold";
    public const string AlertMemoryThreshold = "monitoring.memoryThreshold";
    public const string AlertDiskThreshold = "monitoring.diskThreshold";
    public const string AlertTemperatureThreshold = "monitoring.temperatureThreshold";
    public const string DiscoveryTimeoutMs = "discovery.timeoutMs";
    public const string DiscoveryConcurrency = "discovery.concurrency";
    public const string DiscoveryIntervalMinutes = "discovery.intervalMinutes";
    public const string DiscoveryProbePorts = "discovery.probePorts";
    public const string AgentAutoApprove = "agent.autoApprove";
    public const string AgentAllowCommands = "agent.allowCommands";
    public const string AgentAllowedRoots = "agent.allowedRoots";
    public const string AuditRetentionDays = "security.auditRetentionDays";
    public const string SessionTimeoutMinutes = "security.sessionTimeoutMinutes";
    public const string RequireTls = "security.requireTls";
    public const string CommandBlocklistEnabled = "security.commandBlocklistEnabled";
    public const string CommandAllowlist = "security.commandAllowlist";
    public const string CommandApprovalRequired = "security.commandApprovalRequired";
    public const string RemoteConsentRequired = "security.remoteConsentRequired";
    public const string AuditAnchorPath = "security.auditAnchorPath";
    public const string EnableWindowsNotifications = "app.windowsNotifications";
    public const string SharedFolderEnabled = "sharedFolder.enabled";
    public const string SharedFolderPath = "sharedFolder.path";
    public const string SharedFolderIntervalSeconds = "sharedFolder.intervalSeconds";
    public const string SharedFolderMaxFileBytes = "sharedFolder.maxFileBytes";
    public const string SharedFolderMaxFiles = "sharedFolder.maxFiles";
    public const string LastBackupUtc = "app.lastBackupUtc";
    public const string SchemaVersion = "app.schemaVersion";
}

public static class AuditOperations
{
    public const string Login = "Security.Login";
    public const string Logout = "Security.Logout";
    public const string LoginFailed = "Security.LoginFailed";
    public const string AgentApproved = "Agent.Approved";
    public const string AgentRejected = "Agent.Rejected";
    public const string AgentRevoked = "Agent.Revoked";
    public const string EnrollmentTokenCreated = "Agent.EnrollmentTokenCreated";
    public const string ComputerAdded = "Computer.Added";
    public const string ComputerRemoved = "Computer.Removed";
    public const string ComputerUpdated = "Computer.Updated";
    public const string GroupChanged = "Group.Changed";
    public const string CommandExecuted = "Remote.CommandExecuted";
    public const string CommandBlocked = "Remote.CommandBlocked";
    public const string CommandApprovalRequested = "Remote.CommandApprovalRequested";
    public const string CommandApprovalApproved = "Remote.CommandApprovalApproved";
    public const string CommandApprovalRejected = "Remote.CommandApprovalRejected";
    public const string CommandApprovalExecuted = "Remote.CommandApprovalExecuted";
    public const string FileUploaded = "File.Uploaded";
    public const string FileDownloaded = "File.Downloaded";
    public const string FileDeleted = "File.Deleted";
    public const string FileRenamed = "File.Renamed";
    public const string DirectoryCreated = "File.DirectoryCreated";
    public const string SharedFolderFileSynced = "File.SharedFolderSynced";
    public const string SharedFolderConflict = "File.SharedFolderConflict";
    public const string SharedFolderDeleted = "File.SharedFolderDeleted";
    public const string ProcessTerminated = "Process.Terminated";
    public const string ServiceChanged = "Service.Changed";
    public const string RemoteSessionPrepared = "RemoteSession.Prepared";
    public const string RemoteSessionLaunched = "RemoteSession.Launched";
    public const string RemoteSessionLaunchFailed = "RemoteSession.LaunchFailed";
    public const string RemoteSessionEnded = "RemoteSession.Ended";
    public const string RemoteAccessIdsUpdated = "Computer.RemoteAccessIdsUpdated";
    public const string AutomationCreated = "Automation.Created";
    public const string AutomationUpdated = "Automation.Updated";
    public const string AutomationDeleted = "Automation.Deleted";
    public const string AutomationExecuted = "Automation.Executed";
    public const string ReportExported = "Report.Exported";
    public const string SettingsChanged = "Settings.Changed";
    public const string BackupCreated = "Database.BackupCreated";
    public const string UserCreated = "Security.UserCreated";
    public const string AlertAcknowledged = "Alert.Acknowledged";
}

/// <summary>Windows processes that must never be terminated through the remote process manager.</summary>
public static class ProtectedProcesses
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "csrss", "wininit", "winlogon", "services", "lsass", "smss",
        "svchost", "fontdrvhost", "dwm", "memory compression", "registry"
    };

    public static bool IsProtected(string processName)
    {
        var name = processName;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        return Names.Contains(name) || Names.Contains(processName);
    }
}

/// <summary>Services whose interruption can destabilise or disconnect the machine.</summary>
public static class CriticalServices
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs", "RpcEptMapper", "DcomLaunch", "Winmgmt", "EventLog", "Schedule", "BFE",
        "MpsSvc", "LanmanServer", "LanmanWorkstation", "Dhcp", "Dnscache", "nsi",
        "CryptSvc", "TrustedInstaller", "WSearch", "Power", "PlugPlay", "ProfSvc", "Themes"
    };

    public static bool IsCritical(string serviceName) => Names.Contains(serviceName);
}
