using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;
using System.Security.Cryptography.X509Certificates;

namespace RemoteDeskHub.Core.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public interface IAppPaths
{
    string RootDirectory { get; }
    string DatabasePath { get; }
    string CertificatesDirectory { get; }
    string LogsDirectory { get; }
    string BackupsDirectory { get; }
    string TransfersDirectory { get; }
    string ExportsDirectory { get; }
    string SharedFolderDirectory { get; }
    string SharedFolderBlobsDirectory { get; }
    string SharedFolderStagingDirectory { get; }
    string EnsureDirectory(string name);
}

// ---------------------------------------------------------------------------
// Settings
// ---------------------------------------------------------------------------

public interface ISettingsService
{
    Task SeedDefaultsAsync(CancellationToken cancellationToken = default);
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string? value, string category, CancellationToken cancellationToken = default);

    /// <summary>Writes a setting on behalf of an authenticated administrator.</summary>
    Task SetUserAsync(string actor, string key, string? value, string category, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Authentication / accounts
// ---------------------------------------------------------------------------

public interface IAuthService
{
    Task<bool> HasAnyUserAsync(CancellationToken cancellationToken = default);
    Task<OperationResult<SessionUser>> CreateInitialAdministratorAsync(string username, string password, CancellationToken cancellationToken = default);
    Task<OperationResult<SessionUser>> LoginAsync(string username, string password, string? remoteAddress, CancellationToken cancellationToken = default);
    Task<OperationResult> CreateUserAsync(string actor, string username, string password, UserRole role, CancellationToken cancellationToken = default);
    Task<OperationResult> SetUserEnabledAsync(string actor, Guid userId, bool enabled, CancellationToken cancellationToken = default);
    Task<OperationResult> ChangeRoleAsync(string actor, Guid userId, UserRole role, CancellationToken cancellationToken = default);
    Task<OperationResult> ResetPasswordAsync(string actor, Guid userId, string newPassword, CancellationToken cancellationToken = default);
    Task<OperationResult> ChangePasswordAsync(string username, string currentPassword, string newPassword, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserAccountView>> GetUsersAsync(CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Computers / groups
// ---------------------------------------------------------------------------

public interface IComputerService
{
    Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ComputerSummary>> GetComputersAsync(Guid? groupId = null, string? search = null, CancellationToken cancellationToken = default);
    Task<ComputerSummary?> GetComputerSummaryAsync(Guid computerId, CancellationToken cancellationToken = default);
    Task<ComputerDetail?> GetComputerDetailAsync(Guid computerId, CancellationToken cancellationToken = default);
    Task<OperationResult> AddManualComputerAsync(string actor, string name, string ipAddress, string? macAddress, Guid? groupId, string? notes, CancellationToken cancellationToken = default);
    Task<OperationResult> UpdateComputerAsync(string actor, Guid computerId, string? friendlyName, string? description, string? notes, string? tags, Guid? groupId, CancellationToken cancellationToken = default);
    Task<OperationResult> SetTrustedAsync(string actor, Guid computerId, bool trusted, CancellationToken cancellationToken = default);
    Task<OperationResult> RemoveComputerAsync(string actor, Guid computerId, CancellationToken cancellationToken = default);
    Task<OperationResult> RefreshInventoryAsync(string actor, Guid computerId, CancellationToken cancellationToken = default);
    Task<OperationResult> RefreshMetricsAsync(Guid computerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GroupView>> GetGroupsAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> CreateGroupAsync(string actor, string name, string? description, string kind, CancellationToken cancellationToken = default);
    Task<OperationResult> RenameGroupAsync(string actor, Guid groupId, string name, string? description, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteGroupAsync(string actor, Guid groupId, CancellationToken cancellationToken = default);
    Task<BulkOperationProgress> BulkRefreshInventoryAsync(string actor, IReadOnlyCollection<Guid> computerIds, IProgress<BulkOperationProgress>? progress, CancellationToken cancellationToken = default);
    Task<BulkOperationProgress> BulkCollectHardwareAsync(string actor, IReadOnlyCollection<Guid> computerIds, IProgress<BulkOperationProgress>? progress, CancellationToken cancellationToken = default);
    Task<BulkOperationProgress> BulkCheckDiskSpaceAsync(string actor, IReadOnlyCollection<Guid> computerIds, IProgress<BulkOperationProgress>? progress, CancellationToken cancellationToken = default);
    Task<BulkOperationProgress> BulkCheckServiceStatusAsync(string actor, IReadOnlyCollection<Guid> computerIds, string serviceName, IProgress<BulkOperationProgress>? progress, CancellationToken cancellationToken = default);
    Task<BulkOperationProgress> BulkRunDiagnosticAsync(string actor, IReadOnlyCollection<Guid> computerIds, IProgress<BulkOperationProgress>? progress, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Inventory
// ---------------------------------------------------------------------------

public interface IInventoryService
{
    Task<HardwareInventoryDto?> GetHardwareAsync(Guid computerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HardwareInventoryHistoryView>> GetHardwareHistoryAsync(Guid computerId, int limit = 50, CancellationToken cancellationToken = default);
    Task<OperationResult> RefreshHardwareAsync(string actor, Guid computerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SoftwareInventoryItem>> GetSoftwareAsync(Guid computerId, string? search = null, string? publisher = null, CancellationToken cancellationToken = default);
    Task<OperationResult> RefreshSoftwareAsync(string actor, Guid computerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SoftwareInventoryItem>> CompareSoftwareAsync(IReadOnlyCollection<Guid> computerIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetPublishersAsync(Guid computerId, CancellationToken cancellationToken = default);
    Task<int> GetSoftwareCountAsync(Guid computerId, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Monitoring and alerts
// ---------------------------------------------------------------------------

public interface IMonitoringService
{
    Task IngestMetricsAsync(Computer computer, AgentMetricsDto metrics, CancellationToken cancellationToken = default);
    Task RecordOfflineStatusesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MonitoringSeries>> GetSeriesAsync(IReadOnlyCollection<Guid> computerIds, DateTime fromUtc, DateTime toUtc, int maxPointsPerComputer, CancellationToken cancellationToken = default);
    Task<AgentMetricsDto?> GetLiveMetricsAsync(Guid computerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AlertView>> GetAlertsAsync(bool activeOnly, int limit, CancellationToken cancellationToken = default);
    Task<OperationResult> AcknowledgeAlertAsync(string actor, Guid alertId, CancellationToken cancellationToken = default);
    Task<OperationResult> ResolveAlertAsync(string actor, Guid alertId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AlertRuleView>> GetRulesAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> SaveRuleAsync(string actor, AlertRuleView rule, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteRuleAsync(string actor, Guid ruleId, CancellationToken cancellationToken = default);
    Task<OperationResult> ApplyDefaultThresholdsAsync(string actor, double cpu, double memory, double disk, double temperature, int cooldownMinutes, CancellationToken cancellationToken = default);
    Task<int> PurgeOldMetricsAsync(string? actor = null, CancellationToken cancellationToken = default);
    Task<ReportFile> ExportHistoryAsync(IReadOnlyCollection<Guid> computerIds, DateTime fromUtc, DateTime toUtc, string format, CancellationToken cancellationToken = default);
    Task EvaluateRulesForAsync(Computer computer, AgentMetricsDto metrics, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Remote agent transport (implemented in Networking)
// ---------------------------------------------------------------------------

public interface IAgentTransport
{
    Task<AgentInfoDto> GetInfoAsync(Computer computer, CancellationToken cancellationToken = default);
    Task<AgentMetricsDto> GetMetricsAsync(Computer computer, CancellationToken cancellationToken = default);
    Task<HardwareInventoryDto> GetHardwareAsync(Computer computer, CancellationToken cancellationToken = default);
    Task<SoftwareInventoryDto> GetSoftwareAsync(Computer computer, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteProcessDto>> GetProcessesAsync(Computer computer, CancellationToken cancellationToken = default);
    Task<OperationResult> TerminateProcessAsync(Computer computer, int pid, string processName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteServiceDto>> GetServicesAsync(Computer computer, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteServiceDto>> GetServiceDependenciesAsync(Computer computer, string serviceName, CancellationToken cancellationToken = default);
    Task<OperationResult> ServiceActionAsync(Computer computer, string serviceName, string action, string? startupType, CancellationToken cancellationToken = default);
    Task<CommandResultDto> ExecuteCommandAsync(Computer computer, CommandRequestDto request, CancellationToken cancellationToken = default);
    Task<OperationResult> CancelCommandAsync(Computer computer, Guid correlationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteEventLogDto>> GetEventsAsync(Computer computer, string logName, int maxEntries, CancellationToken cancellationToken = default);
    Task<SharedFolderManifestDto> GetSharedFolderManifestAsync(Computer computer, CancellationToken cancellationToken = default);
    Task<FileListDto> ListFilesAsync(Computer computer, string path, CancellationToken cancellationToken = default);
    Task<FileStatDto> StatFileAsync(Computer computer, string path, bool includeHash, CancellationToken cancellationToken = default);
    Task<FileChunkDto> ReadFileChunkAsync(Computer computer, string path, long offset, int length, CancellationToken cancellationToken = default);
    Task<FileWriteResultDto> WriteFileChunkAsync(Computer computer, FileWriteRequestDto request, CancellationToken cancellationToken = default);
    Task<OperationResult> CreateDirectoryAsync(Computer computer, string path, CancellationToken cancellationToken = default);
    Task<OperationResult> RenameFileAsync(Computer computer, string path, string newName, bool confirmed, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteFileAsync(Computer computer, string path, bool recursive, bool confirmed, CancellationToken cancellationToken = default);
    Task<FileHashDto> GetFileHashAsync(Computer computer, string path, CancellationToken cancellationToken = default);
    Task<OperationResult> FinalizeUploadAsync(Computer computer, string temporaryPath, string finalPath, CancellationToken cancellationToken = default);
    Task<OperationResult> PingAsync(Computer computer, CancellationToken cancellationToken = default);
}

public interface IAgentLifecycleService
{
    Task<HeartbeatResponseDto> ProcessHeartbeatAsync(HeartbeatRequestDto request, string? certificateThumbprint, string? remoteIp, CancellationToken cancellationToken = default);
    Task<OperationResult> RecordTelemetryAsync(TelemetryBatchDto batch, string? certificateThumbprint, CancellationToken cancellationToken = default);
    Task<AgentInfoDto?> GetAgentInfoAsync(string? certificateThumbprint, CancellationToken cancellationToken = default);
    Task<AgentRuntimeConfigDto> BuildRuntimeConfigAsync(CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Remote administration
// ---------------------------------------------------------------------------

public interface ICommandService
{
    Task<CommandResultDto> ExecuteAsync(string actor, Computer computer, string command, CommandShell shell, int timeoutSeconds, bool requireElevation, bool confirmed, bool isBulk, CancellationToken cancellationToken = default, Guid? approvalId = null);
    Task<OperationResult> CancelAsync(string actor, Guid computerId, Guid correlationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommandHistoryView>> GetHistoryAsync(Guid? computerId = null, int limit = 200, CancellationToken cancellationToken = default);
    Task<ReportFile> ExportHistoryAsync(string format, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommandApprovalView>> GetApprovalsAsync(bool pendingOnly = true, int limit = 100, CancellationToken cancellationToken = default);
    Task<OperationResult> ApproveCommandAsync(string actor, Guid approvalId, string? note = null, CancellationToken cancellationToken = default);
    Task<OperationResult> RejectCommandAsync(string actor, Guid approvalId, string? note = null, CancellationToken cancellationToken = default);
    Task<CommandResultDto> ExecuteApprovedAsync(string actor, Guid approvalId, CancellationToken cancellationToken = default);
}

public interface IProcessService
{
    Task<IReadOnlyList<RemoteProcessDto>> GetProcessesAsync(Guid computerId, CancellationToken cancellationToken = default);
    Task<ProcessTerminateOutcome> TerminateAsync(string actor, Guid computerId, int pid, string processName, bool confirmed, CancellationToken cancellationToken = default);
    Task<ReportFile> ExportAsync(Guid computerId, string format, CancellationToken cancellationToken = default);
}

public interface IServiceManagerService
{
    Task<IReadOnlyList<RemoteServiceDto>> GetServicesAsync(Guid computerId, CancellationToken cancellationToken = default);
    Task<OperationResult> ExecuteActionAsync(string actor, Guid computerId, string serviceName, string action, string? startupType, bool confirmed, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteServiceDto>> GetDependenciesAsync(Guid computerId, string serviceName, CancellationToken cancellationToken = default);
}

public interface IFileTransferService
{
    Task<FileListDto> ListRemoteAsync(Guid computerId, string path, CancellationToken cancellationToken = default);
    Task<OperationResult> CreateRemoteDirectoryAsync(string actor, Guid computerId, string path, CancellationToken cancellationToken = default);
    Task<OperationResult> RenameRemoteAsync(string actor, Guid computerId, string path, string newName, bool confirmed, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteRemoteAsync(string actor, Guid computerId, string path, bool recursive, bool confirmed, CancellationToken cancellationToken = default);
    Task<FileTransferView> UploadAsync(string actor, Guid computerId, string localPath, string remotePath, IProgress<TransferProgress>? progress, CancellationToken cancellationToken = default);
    Task<FileTransferView> DownloadAsync(string actor, Guid computerId, string remotePath, string localPath, IProgress<TransferProgress>? progress, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileTransferView>> GetHistoryAsync(Guid? computerId = null, int limit = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetAllowedRootsAsync(Guid computerId, CancellationToken cancellationToken = default);
}

public sealed record TransferProgress(long TransferredBytes, long TotalBytes, double BytesPerSecond, string CurrentFile)
{
    public double Percent => TotalBytes <= 0 ? 0 : Math.Clamp(TransferredBytes * 100.0 / TotalBytes, 0, 100);
}

public interface ISharedFolderService
{
    Task<SharedFolderStatusView> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SharedFolderFileView>> GetFilesAsync(int limit = 500, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SharedFolderAgentView>> GetAgentsAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> AddLocalFilesAsync(string actor, IReadOnlyList<string> sourceFiles, CancellationToken cancellationToken = default);
    Task<OperationResult> AddLocalFolderAsync(string actor, string sourceFolder, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Discovery
// ---------------------------------------------------------------------------

public interface IDiscoveryService
{
    Task<DiscoveryScanResult> ScanAsync(DiscoveryScanRequest request, IProgress<string>? progress, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DiscoveryView>> GetDevicesAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> AddToInventoryAsync(string actor, Guid deviceId, string? friendlyName, Guid? groupId, string? notes, bool trusted, CancellationToken cancellationToken = default);
    Task<OperationResult> UpdateDeviceAsync(string actor, Guid deviceId, string? friendlyName, string? notes, bool? trusted, Guid? groupId, CancellationToken cancellationToken = default);
    Task<OperationResult> RemoveDeviceAsync(string actor, Guid deviceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetLocalSubnetsAsync(CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Agent enrollment
// ---------------------------------------------------------------------------

public interface IEnrollmentService
{
    Task<(Guid Id, string Token, DateTime ExpiresUtc)> CreateTokenAsync(string actor, string? note, TimeSpan? lifetime, bool autoApprove, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentEnrollment>> GetEnrollmentsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ComputerSummary>> GetPendingAgentsAsync(CancellationToken cancellationToken = default);
    Task<OperationResult<EnrollmentResponseDto>> HandleEnrollmentRequestAsync(EnrollmentRequestDto request, string remoteIp, CancellationToken cancellationToken = default);
    Task<EnrollmentStatusDto> GetEnrollmentStatusAsync(Guid enrollmentId, string token, CancellationToken cancellationToken = default);
    Task<OperationResult> ApproveAsync(string actor, Guid enrollmentId, CancellationToken cancellationToken = default);
    Task<OperationResult> RejectAsync(string actor, Guid enrollmentId, string? reason, CancellationToken cancellationToken = default);
    Task<OperationResult> RevokeAgentAsync(string actor, Guid agentId, string reason, CancellationToken cancellationToken = default);
    Task<PairingPackageDto> CreatePairingPackageAsync(string actor, TimeSpan lifetime, string hubUrl, CancellationToken cancellationToken = default);
    Task<PairingPackageDto> CreatePairingPackageAsync(string actor, TimeSpan lifetime, string hubUrl, bool autoApprove, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteEnrollmentAsync(string actor, Guid enrollmentId, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Audit and notifications
// ---------------------------------------------------------------------------

public interface IAuditService
{
    Task LogAsync(string actor, string operation, string? computerName = null, Guid? computerId = null, string result = "Success", long durationMs = 0, string? error = null, string? details = null, string? ipAddress = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditView>> QueryAsync(DateTime? fromUtc, DateTime? toUtc, string? search, int limit, CancellationToken cancellationToken = default);
    Task<(bool Valid, string Message)> VerifyChainAsync(CancellationToken cancellationToken = default);
    Task<int> PurgeOldAsync(CancellationToken cancellationToken = default);
}

public sealed record AppNotification(string Title, string Message, AlertSeverity Severity, Guid? ComputerId = null, DateTime TimestampUtc = default)
{
    public DateTime When => TimestampUtc == default ? DateTime.UtcNow : TimestampUtc;
}

public interface IAppEvents
{
    event EventHandler<Guid>? ComputerChanged;
    event EventHandler<AppNotification>? NotificationRaised;
    event EventHandler? DataChanged;
    void RaiseComputerChanged(Guid computerId);
    void RaiseNotification(AppNotification notification);
    void RaiseDataChanged();
}

// ---------------------------------------------------------------------------
// Remote assistance
// ---------------------------------------------------------------------------

public interface IRemoteAssistanceService
{
    Task<IReadOnlyList<RemoteAssistanceTarget>> GetTargetsAsync(CancellationToken cancellationToken = default);
    Task<OperationResult<RemoteAssistanceLaunchInfo>> PrepareLaunchAsync(
        string actor,
        Guid computerId,
        RemoteToolKind tool,
        string? connectionTarget,
        bool consentConfirmed,
        CancellationToken cancellationToken = default);
    Task<OperationResult> CompleteLaunchAsync(
        string actor,
        Guid sessionId,
        bool launched,
        string? details,
        int? processId = null,
        CancellationToken cancellationToken = default);
    Task<OperationResult> MarkSessionEndedAsync(
        string actor,
        Guid sessionId,
        string? details = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteSession>> GetSessionHistoryAsync(int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteAssistanceProfile>> GetProfilesAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> SaveProfileAsync(string actor, RemoteAssistanceProfile profile, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteProfileAsync(string actor, Guid profileId, CancellationToken cancellationToken = default);
    Task<OperationResult> SaveRemoteAccessIdsAsync(
        string actor,
        Guid computerId,
        string? teamViewerId,
        string? anyDeskId,
        string? lmViewerId,
        string? rustDeskId,
        CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Automation
// ---------------------------------------------------------------------------

public interface IAutomationService
{
    Task<IReadOnlyList<AutomationTaskView>> GetTasksAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> SaveTaskAsync(string actor, AutomationTask task, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteTaskAsync(string actor, Guid taskId, CancellationToken cancellationToken = default);
    Task<OperationResult> SetEnabledAsync(string actor, Guid taskId, bool enabled, CancellationToken cancellationToken = default);
    Task<AutomationExecutionView> RunNowAsync(string actor, Guid taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AutomationExecutionView>> GetExecutionsAsync(Guid? taskId = null, int limit = 100, CancellationToken cancellationToken = default);
    Task TickAsync(CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Reporting / backup / host
// ---------------------------------------------------------------------------

public interface IReportService
{
    Task<ReportFile> GenerateAsync(ExportRequest request, CancellationToken cancellationToken = default);
    Task<ReportFile> GenerateNetworkInventoryAsync(string format, CancellationToken cancellationToken = default);
    Task<ReportFile> GenerateHardwareInventoryAsync(string format, CancellationToken cancellationToken = default);
    Task<ReportFile> GenerateSoftwareInventoryAsync(string format, CancellationToken cancellationToken = default);
    Task<ReportFile> GenerateAlertReportAsync(string format, CancellationToken cancellationToken = default);
    Task<ReportFile> GenerateAgentHealthReportAsync(string format, CancellationToken cancellationToken = default);
    Task<ReportFile> GenerateAuditReportAsync(string format, CancellationToken cancellationToken = default);
}

public interface IBackupService
{
    Task<OperationResult<string>> CreateBackupAsync(string actor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileInfo>> ListBackupsAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> RestoreBackupAsync(string actor, string backupFileName, CancellationToken cancellationToken = default);
    Task<OperationResult> VerifyDatabaseAsync(CancellationToken cancellationToken = default);
}

public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> IsFirstRunAsync(CancellationToken cancellationToken = default);
    bool IsFirstRun { get; }
}

public interface IHubServer
{
    bool IsRunning { get; }
    int BoundPort { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface ICertificateAuthority
{
    Task EnsureCreatedAsync(CancellationToken cancellationToken = default);
    string GetCaCertificatePem();
    string GetClientCertificatePem();
    string GetServerCertificateThumbprint();
    string GetClientCertificateThumbprint();
    X509Certificate2 GetServerCertificate();
    X509Certificate2 GetClientCertificate();
    X509Certificate2 GetCaCertificate();
    bool ValidateCertificate(X509Certificate2 certificate, bool requireServerAuthentication);
    Task<string> SignAgentCertificateAsync(string csrPem, string machineName, Guid agentId, CancellationToken cancellationToken = default);
    bool TryValidateAgentCertificate(string? certificatePem, out string? thumbprint, out string? error);
    bool IsAgentCertificateTrusted(string? thumbprint);
}

public interface IAgentConnectionInfoProvider
{
    (string Host, int Port)? Resolve(Computer computer);
}
