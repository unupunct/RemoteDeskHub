using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Desktop.Services;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Desktop.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly IBackupService _backup;
    private readonly IMonitoringService _monitoring;
    private readonly IHubServer _hub;
    private readonly ICertificateAuthority _certificates;
    private readonly IAppPaths _paths;
    private readonly ISessionContext _session;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private bool startWithWindows;

    [ObservableProperty]
    private bool minimizeToTray = true;

    [ObservableProperty]
    private bool windowsNotifications = true;

    [ObservableProperty]
    private int hubPort = HubConstants.DefaultHubPort;

    [ObservableProperty]
    private int agentPort = HubConstants.DefaultAgentPort;

    [ObservableProperty]
    private string bindAddress = "0.0.0.0";

    [ObservableProperty]
    private string hubUrl = $"https://localhost:{HubConstants.DefaultHubPort}";

    [ObservableProperty]
    private int metricsSeconds = HubConstants.DefaultMetricsSeconds;

    [ObservableProperty]
    private int heartbeatSeconds = HubConstants.DefaultHeartbeatSeconds;

    [ObservableProperty]
    private int metricsRetentionDays = 30;

    [ObservableProperty]
    private int alertCooldownMinutes = 30;

    [ObservableProperty]
    private double cpuThreshold = 90;

    [ObservableProperty]
    private double memoryThreshold = 92;

    [ObservableProperty]
    private double diskThreshold = 10;

    [ObservableProperty]
    private double temperatureThreshold = 85;

    [ObservableProperty]
    private int discoveryTimeoutMs = 800;

    [ObservableProperty]
    private int discoveryConcurrency = 24;

    [ObservableProperty]
    private int discoveryIntervalMinutes;

    [ObservableProperty]
    private string discoveryProbePorts = $"{HubConstants.DefaultAgentPort},445,3389";

    [ObservableProperty]
    private bool agentAutoApprove;

    [ObservableProperty]
    private bool agentAllowCommands = true;

    [ObservableProperty]
    private string agentAllowedRoots = @"C:\Users;C:\ProgramData";

    [ObservableProperty]
    private int auditRetentionDays = 365;

    [ObservableProperty]
    private int sessionTimeoutMinutes = 60;

    [ObservableProperty]
    private bool requireTls = true;

    [ObservableProperty]
    private bool commandBlocklistEnabled = true;

    [ObservableProperty]
    private string commandAllowlist = string.Empty;

    [ObservableProperty]
    private bool commandApprovalRequired;

    [ObservableProperty]
    private bool remoteConsentRequired;

    [ObservableProperty]
    private string auditAnchorPath = string.Empty;

    [ObservableProperty]
    private string dataRoot = string.Empty;

    [ObservableProperty]
    private string logsPath = string.Empty;

    [ObservableProperty]
    private string backupsPath = string.Empty;

    [ObservableProperty]
    private string serverThumbprint = "Not available";

    [ObservableProperty]
    private string clientThumbprint = "Not available";

    [ObservableProperty]
    private string caThumbprint = "Not available";

    [ObservableProperty]
    private bool isHubRunning;

    [ObservableProperty]
    private int boundPort;

    [ObservableProperty]
    private ObservableCollection<FileInfo> backups = [];

    [ObservableProperty]
    private FileInfo? selectedBackup;

    [ObservableProperty]
    private string backupStatus = "No backup operation in this session.";

    public SettingsViewModel(ISettingsService settings, IBackupService backup, IMonitoringService monitoring, IHubServer hub,
        ICertificateAuthority certificates, IAppPaths paths, ISessionContext session, IDialogService dialogs)
    {
        _settings = settings;
        _backup = backup;
        _monitoring = monitoring;
        _hub = hub;
        _certificates = certificates;
        _paths = paths;
        _session = session;
        _dialogs = dialogs;
    }

    public override string Title => "Settings";
    public override string Subtitle => "Network, monitoring, agent policy, security and backups";

    public string Actor => _session.CurrentUser?.UserName ?? HubConstants.AuditActorSystem;
    public bool CanManage => _session.CurrentUser?.Can(Permission.ManageSettings) == true;
    public bool CanBackup => _session.CurrentUser?.Can(Permission.BackupDatabase) == true;
    public IReadOnlyList<string> BindAddresses { get; } = ["0.0.0.0", "127.0.0.1"];

    public override async Task LoadAsync()
    {
        await RunAsync(async token =>
        {
            StartWithWindows = await _settings.GetAsync(SettingsKeys.ApplicationStartWithWindows, false, token);
            MinimizeToTray = await _settings.GetAsync(SettingsKeys.ApplicationMinimizeToTray, true, token);
            WindowsNotifications = await _settings.GetAsync(SettingsKeys.EnableWindowsNotifications, true, token);

            HubPort = await _settings.GetAsync(SettingsKeys.HubPort, HubConstants.DefaultHubPort, token);
            AgentPort = await _settings.GetAsync(SettingsKeys.AgentPort, HubConstants.DefaultAgentPort, token);
            BindAddress = await _settings.GetAsync(SettingsKeys.BindAddress, "0.0.0.0", token);
            HubUrl = await _settings.GetAsync(SettingsKeys.HubUrl, $"https://localhost:{HubConstants.DefaultHubPort}", token);

            MetricsSeconds = await _settings.GetAsync(SettingsKeys.MetricsSeconds, HubConstants.DefaultMetricsSeconds, token);
            HeartbeatSeconds = await _settings.GetAsync(SettingsKeys.HeartbeatSeconds, HubConstants.DefaultHeartbeatSeconds, token);
            MetricsRetentionDays = await _settings.GetAsync(SettingsKeys.MetricsRetentionDays, 30, token);
            AlertCooldownMinutes = await _settings.GetAsync(SettingsKeys.AlertCooldownMinutes, 30, token);

            CpuThreshold = await _settings.GetAsync(SettingsKeys.AlertCpuThreshold, 90d, token);
            MemoryThreshold = await _settings.GetAsync(SettingsKeys.AlertMemoryThreshold, 92d, token);
            DiskThreshold = await _settings.GetAsync(SettingsKeys.AlertDiskThreshold, 10d, token);
            TemperatureThreshold = await _settings.GetAsync(SettingsKeys.AlertTemperatureThreshold, 85d, token);

            DiscoveryTimeoutMs = await _settings.GetAsync(SettingsKeys.DiscoveryTimeoutMs, 800, token);
            DiscoveryConcurrency = await _settings.GetAsync(SettingsKeys.DiscoveryConcurrency, 24, token);
            DiscoveryIntervalMinutes = await _settings.GetAsync(SettingsKeys.DiscoveryIntervalMinutes, 0, token);
            DiscoveryProbePorts = await _settings.GetAsync(SettingsKeys.DiscoveryProbePorts, $"{HubConstants.DefaultAgentPort},445,3389", token);

            AgentAutoApprove = await _settings.GetAsync(SettingsKeys.AgentAutoApprove, false, token);
            AgentAllowCommands = await _settings.GetAsync(SettingsKeys.AgentAllowCommands, true, token);
            AgentAllowedRoots = await _settings.GetAsync(SettingsKeys.AgentAllowedRoots, @"C:\Users;C:\ProgramData", token);

            AuditRetentionDays = await _settings.GetAsync(SettingsKeys.AuditRetentionDays, 365, token);
            SessionTimeoutMinutes = await _settings.GetAsync(SettingsKeys.SessionTimeoutMinutes, 60, token);
            RequireTls = await _settings.GetAsync(SettingsKeys.RequireTls, true, token);
            CommandBlocklistEnabled = await _settings.GetAsync(SettingsKeys.CommandBlocklistEnabled, true, token);
            CommandAllowlist = await _settings.GetAsync(SettingsKeys.CommandAllowlist, string.Empty, token);
            CommandApprovalRequired = await _settings.GetAsync(SettingsKeys.CommandApprovalRequired, false, token);
            RemoteConsentRequired = await _settings.GetAsync(SettingsKeys.RemoteConsentRequired, false, token);
            AuditAnchorPath = await _settings.GetAsync(SettingsKeys.AuditAnchorPath, string.Empty, token);

            DataRoot = _paths.RootDirectory;
            LogsPath = _paths.LogsDirectory;
            BackupsPath = _paths.BackupsDirectory;
            IsHubRunning = _hub.IsRunning;
            BoundPort = _hub.BoundPort;
            LoadThumbprints();
            await LoadBackupsAsync(token);
        }, "Settings loaded.");
    }

    private void LoadThumbprints()
    {
        try
        {
            ServerThumbprint = _certificates.GetServerCertificateThumbprint();
            ClientThumbprint = _certificates.GetClientCertificateThumbprint();
            CaThumbprint = _certificates.GetCaCertificate().Thumbprint;
        }
        catch
        {
            ServerThumbprint = ClientThumbprint = CaThumbprint = "Certificate authority not initialised";
        }
    }

    private async Task LoadBackupsAsync(CancellationToken token)
    {
        try
        {
            Backups = new ObservableCollection<FileInfo>(await _backup.ListBackupsAsync(token));
        }
        catch (Exception ex)
        {
            BackupStatus = $"Backups could not be listed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (HubPort is < 1 or > 65535 || AgentPort is < 1 or > 65535)
        {
            Fail("Hub and agent ports must be between 1 and 65535.");
            return;
        }

        if (MetricsSeconds is < 1 or > 300 || HeartbeatSeconds is < 2 or > 300)
        {
            Fail("Metrics interval must be 1–300 seconds and heartbeat 2–300 seconds.");
            return;
        }

        if (CpuThreshold is <= 0 or > 100 || MemoryThreshold is <= 0 or > 100 || DiskThreshold is <= 0 or > 100)
        {
            Fail("CPU, memory and disk thresholds must be between 1 and 100.");
            return;
        }

        var roots = AgentAllowedRoots.Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (roots.Length == 0 || roots.Any(r => !RemoteDeskHub.Shared.Security.PathGuard.IsValidAbsolutePath(r, out _)))
        {
            Fail("Every permitted agent file root must be an absolute local drive path.");
            return;
        }

        if (AuditRetentionDays is < 1 or > 3650)
        {
            Fail("Audit retention must be between 1 and 3650 days.");
            return;
        }

        if (SessionTimeoutMinutes is < 0 or > 1440)
        {
            Fail("Session timeout must be between 0 (disabled) and 1440 minutes.");
            return;
        }

        if (CommandAllowlist.Length > 8000)
        {
            Fail("The command allowlist cannot exceed 8,000 characters.");
            return;
        }

        var anchorPath = AuditAnchorPath.Trim();
        if (anchorPath.Length > 1024)
        {
            Fail("The audit anchor path cannot exceed 1,024 characters.");
            return;
        }

        if (anchorPath.Length > 0 &&
            (!Path.IsPathRooted(anchorPath) ||
             anchorPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
             anchorPath.EndsWith(Path.DirectorySeparatorChar) ||
             anchorPath.EndsWith(Path.AltDirectorySeparatorChar)))
        {
            Fail("The audit anchor path must be an absolute local or UNC file path, for example \\\\server\\share\\remotedesk-audit.log.");
            return;
        }

        try
        {
            StartupRegistration.Apply(StartWithWindows);
        }
        catch (Exception ex)
        {
            Fail($"The Windows startup entry could not be updated: {ex.Message}");
            return;
        }

        var restartHub = _hub.IsRunning && (HubPort != _hub.BoundPort || !_hub.IsRunning);
        await RunAsync(async token =>
        {
            Task Persist(string key, string? value, string category, CancellationToken _ = default) =>
                _settings.SetUserAsync(Actor, key, value, category, token);

            await Persist(SettingsKeys.ApplicationStartWithWindows, StartWithWindows.ToString(), "General");
            await Persist(SettingsKeys.ApplicationMinimizeToTray, MinimizeToTray.ToString(), "General", token);
            await Persist(SettingsKeys.EnableWindowsNotifications, WindowsNotifications.ToString(), "General", token);

            await Persist(SettingsKeys.HubPort, HubPort.ToString(), "Network", token);
            await Persist(SettingsKeys.AgentPort, AgentPort.ToString(), "Network", token);
            await Persist(SettingsKeys.BindAddress, BindAddress.Trim(), "Network", token);
            await Persist(SettingsKeys.HubUrl, HubUrl.Trim(), "Network", token);

            await Persist(SettingsKeys.MetricsSeconds, MetricsSeconds.ToString(), "Monitoring", token);
            await Persist(SettingsKeys.HeartbeatSeconds, HeartbeatSeconds.ToString(), "Monitoring", token);
            await Persist(SettingsKeys.MetricsRetentionDays, MetricsRetentionDays.ToString(), "Monitoring", token);
            await Persist(SettingsKeys.AlertCooldownMinutes, AlertCooldownMinutes.ToString(), "Monitoring", token);
            await Persist(SettingsKeys.AlertCpuThreshold, CpuThreshold.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), "Monitoring", token);
            await Persist(SettingsKeys.AlertMemoryThreshold, MemoryThreshold.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), "Monitoring", token);
            await Persist(SettingsKeys.AlertDiskThreshold, DiskThreshold.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), "Monitoring", token);
            await Persist(SettingsKeys.AlertTemperatureThreshold, TemperatureThreshold.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), "Monitoring", token);

            var thresholdResult = await _monitoring.ApplyDefaultThresholdsAsync(
                Actor, CpuThreshold, MemoryThreshold, DiskThreshold, TemperatureThreshold, AlertCooldownMinutes, token);
            if (!thresholdResult.Success)
            {
                throw new InvalidOperationException(thresholdResult.Error ?? "Default alert thresholds could not be applied.");
            }

            await Persist(SettingsKeys.DiscoveryTimeoutMs, DiscoveryTimeoutMs.ToString(), "Discovery", token);
            await Persist(SettingsKeys.DiscoveryConcurrency, DiscoveryConcurrency.ToString(), "Discovery", token);
            await Persist(SettingsKeys.DiscoveryIntervalMinutes, DiscoveryIntervalMinutes.ToString(), "Discovery", token);
            await Persist(SettingsKeys.DiscoveryProbePorts, DiscoveryProbePorts.Trim(), "Discovery", token);

            await Persist(SettingsKeys.AgentAutoApprove, AgentAutoApprove.ToString(), "Agent", token);
            await Persist(SettingsKeys.AgentAllowCommands, AgentAllowCommands.ToString(), "Agent", token);
            await Persist(SettingsKeys.AgentAllowedRoots, string.Join(";", roots), "Agent", token);

            await Persist(SettingsKeys.AuditRetentionDays, AuditRetentionDays.ToString(), "Security", token);
            await Persist(SettingsKeys.SessionTimeoutMinutes, SessionTimeoutMinutes.ToString(), "Security", token);
            await Persist(SettingsKeys.RequireTls, RequireTls.ToString(), "Security", token);
            await Persist(SettingsKeys.CommandBlocklistEnabled, CommandBlocklistEnabled.ToString(), "Security", token);
            await Persist(SettingsKeys.CommandAllowlist, CommandAllowlist.Trim(), "Security", token);
            await Persist(SettingsKeys.CommandApprovalRequired, CommandApprovalRequired.ToString(), "Security", token);
            await Persist(SettingsKeys.RemoteConsentRequired, RemoteConsentRequired.ToString(), "Security", token);
            await Persist(SettingsKeys.AuditAnchorPath, anchorPath, "Security", token);

            if (restartHub)
            {
                await _hub.StopAsync(token);
                await _hub.StartAsync(token);
                IsHubRunning = _hub.IsRunning;
                BoundPort = _hub.BoundPort;
            }
        }, restartHub ? "Settings saved and the hub listener was restarted." : "Settings saved.");
    }

    [RelayCommand]
    private async Task StartHubAsync()
    {
        await RunAsync(async token =>
        {
            await _hub.StartAsync(token);
            IsHubRunning = _hub.IsRunning;
            BoundPort = _hub.BoundPort;
        }, "Hub listener started.");
    }

    [RelayCommand]
    private async Task StopHubAsync()
    {
        if (!_dialogs.Confirm("Stop hub listener",
                "Stop the hub HTTPS listener? Agents will be unable to send heartbeats until it is started again.",
                danger: true))
        {
            return;
        }

        await RunAsync(async token =>
        {
            await _hub.StopAsync(token);
            IsHubRunning = _hub.IsRunning;
            BoundPort = _hub.BoundPort;
        }, "Hub listener stopped.");
    }

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        await RunAsync(async token =>
        {
            var result = await _backup.CreateBackupAsync(Actor, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The backup could not be created.");
            }

            BackupStatus = $"Backup created: {result.Value}";
            await LoadBackupsAsync(token);
        }, "Database backup created.");
    }

    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        if (SelectedBackup is null)
        {
            Fail("Select a backup to restore.");
            return;
        }

        if (!_dialogs.Confirm("Restore database",
                $"Restore '{SelectedBackup.Name}'? The current database will be replaced. The application must be restarted afterwards.",
                danger: true))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _backup.RestoreBackupAsync(Actor, SelectedBackup.Name, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The backup could not be restored.");
            }

            BackupStatus = "Backup restored. Close and reopen RemoteDesk Hub to use the restored data.";
            _dialogs.Warning("Restart required", "The database was restored. Close and reopen RemoteDesk Hub before continuing.");
        }, "Backup restored.");
    }

    [RelayCommand]
    private async Task VerifyDatabaseAsync()
    {
        await RunAsync(async token =>
        {
            var result = await _backup.VerifyDatabaseAsync(token);
            BackupStatus = result.Success
                ? result.Message ?? "Database integrity check passed."
                : result.Error ?? "Database integrity check failed.";
            HasError = !result.Success;
        });
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.RootDirectory);
            Process.Start(new ProcessStartInfo(_paths.RootDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Fail($"The data folder could not be opened: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            Process.Start(new ProcessStartInfo(_paths.LogsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Fail($"The logs folder could not be opened: {ex.Message}");
        }
    }
}
