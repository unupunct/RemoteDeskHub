using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class DatabaseInitializer(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    IAppPaths paths,
    IAuditService audit,
    IClock clock) : IDatabaseInitializer
{
    private static readonly string[] DefaultGroups = ["Workstations", "Servers", "Laptops", "Virtual Machines", "Test Machines"];

    public bool IsFirstRun { get; private set; }

    public async Task<bool> IsFirstRunAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return !await db.UserAccounts.AnyAsync(cancellationToken);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // The desktop console and the headless hub host can start at the same time. Serialise
        // staged-restore application, migrations and seeding so a restore never races an open
        // database connection from the other process.
        await using var processLock = await ProcessFileLock.AcquireAsync(
            paths.RootDirectory,
            "startup.lock",
            cancellationToken,
            timeoutMilliseconds: 30000);

        var restoreApplied = await ApplyPendingRestoreAsync(cancellationToken);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);

        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", cancellationToken);

        if (restoreApplied)
        {
            // The restore itself happened outside the restored database's timeline. Record it now
            // that the replacement schema is open, so the audit chain in the live database shows
            // that a staged restore was applied.
            await audit.LogAsync(
                HubConstants.AuditActorSystem,
                "Database.RestoreApplied",
                details: "Staged database restore applied before the hub opened the database.",
                cancellationToken: cancellationToken);
        }

        IsFirstRun = !await db.UserAccounts.AnyAsync(cancellationToken);

        if (!await db.SchemaVersions.AnyAsync(cancellationToken))
        {
            db.SchemaVersions.Add(new SchemaVersion { Version = HubConstants.ProductVersion, AppliedUtc = clock.UtcNow });
        }

        if (!await db.ComputerGroups.AnyAsync(cancellationToken))
        {
            foreach (var name in DefaultGroups)
            {
                db.ComputerGroups.Add(new ComputerGroup
                {
                    Name = name,
                    Kind = name.Replace(" ", string.Empty, StringComparison.Ordinal),
                    IsSystem = true,
                    Description = $"Built-in {name.ToLowerInvariant()} group",
                    CreatedUtc = clock.UtcNow
                });
            }
        }

        if (!await db.AlertRules.AnyAsync(cancellationToken))
        {
            db.AlertRules.AddRange(
                new AlertRule
                {
                    Name = "High CPU utilisation",
                    MetricType = AlertMetricType.CpuUsage,
                    Threshold = 90,
                    Severity = AlertSeverity.Warning,
                    CooldownMinutes = 30,
                    CreatedUtc = clock.UtcNow
                },
                new AlertRule
                {
                    Name = "High memory utilisation",
                    MetricType = AlertMetricType.MemoryUsage,
                    Threshold = 92,
                    Severity = AlertSeverity.Warning,
                    CooldownMinutes = 30,
                    CreatedUtc = clock.UtcNow
                },
                new AlertRule
                {
                    Name = "Low free disk space",
                    MetricType = AlertMetricType.DiskFree,
                    Threshold = 10,
                    Severity = AlertSeverity.Warning,
                    CooldownMinutes = 60,
                    CreatedUtc = clock.UtcNow
                },
                new AlertRule
                {
                    Name = "High CPU temperature",
                    MetricType = AlertMetricType.Temperature,
                    Threshold = 85,
                    Severity = AlertSeverity.Warning,
                    CooldownMinutes = 30,
                    CreatedUtc = clock.UtcNow
                },
                new AlertRule
                {
                    Name = "Computer offline",
                    MetricType = AlertMetricType.ComputerOffline,
                    Threshold = 0,
                    Severity = AlertSeverity.Warning,
                    CooldownMinutes = 15,
                    CreatedUtc = clock.UtcNow
                });
        }

        var defaults = GetDefaultSettings();
        var existing = await db.ApplicationSettings.Select(s => s.Key).ToListAsync(cancellationToken);
        foreach (var (key, value, category) in defaults)
        {
            if (!existing.Contains(key))
            {
                db.ApplicationSettings.Add(new ApplicationSetting { Key = key, Value = value, Category = category, UpdatedUtc = clock.UtcNow });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> ApplyPendingRestoreAsync(CancellationToken cancellationToken)
    {
        var staging = paths.DatabasePath + ".restore-pending";
        var marker = paths.DatabasePath + ".restore-pending.marker";
        if (!File.Exists(marker))
        {
            return false;
        }

        if (!File.Exists(staging))
        {
            throw new InvalidOperationException(
                "A pending database restore was requested but the staged database file is missing. " +
                "Remove the .restore-pending.marker file to start with the current database.");
        }

        var integrity = await CheckIntegrityAsync(staging, cancellationToken);
        if (!integrity.Success)
        {
            throw new InvalidOperationException(
                $"The staged database restore failed integrity verification and was not applied: {integrity.Error}");
        }

        // WAL/SHM files belong to the current database. They must not be paired with the
        // replacement database or SQLite could recover stale pages into it.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = paths.DatabasePath + suffix;
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }

        var applyPath = paths.DatabasePath + ".restore-apply";
        File.Copy(staging, applyPath, true);
        File.Move(applyPath, paths.DatabasePath, true);
        File.Delete(staging);
        File.Delete(marker);
        return true;
    }

    private static async Task<OperationResult> CheckIntegrityAsync(string databasePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var result = (string?)await command.ExecuteScalarAsync(cancellationToken);
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase)
                ? OperationResult.Ok("Database integrity check passed.")
                : OperationResult.Fail(result ?? "Unknown integrity check result.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    public static IReadOnlyList<(string Key, string? Value, string Category)> GetDefaultSettings() =>
    [
        (SettingsKeys.ApplicationTheme, "Dark", "General"),
        (SettingsKeys.ApplicationStartWithWindows, "false", "General"),
        (SettingsKeys.ApplicationMinimizeToTray, "true", "General"),
        (SettingsKeys.EnableWindowsNotifications, "true", "General"),
        (SettingsKeys.HubUrl, $"https://localhost:{HubConstants.DefaultHubPort}", "Network"),
        (SettingsKeys.HubPort, HubConstants.DefaultHubPort.ToString(), "Network"),
        (SettingsKeys.AgentPort, HubConstants.DefaultAgentPort.ToString(), "Network"),
        (SettingsKeys.BindAddress, "0.0.0.0", "Network"),
        (SettingsKeys.MetricsSeconds, HubConstants.DefaultMetricsSeconds.ToString(), "Monitoring"),
        (SettingsKeys.HeartbeatSeconds, HubConstants.DefaultHeartbeatSeconds.ToString(), "Monitoring"),
        (SettingsKeys.InventoryMinutes, HubConstants.DefaultInventoryMinutes.ToString(), "Monitoring"),
        (SettingsKeys.MetricsRetentionDays, "30", "Monitoring"),
        (SettingsKeys.AlertCooldownMinutes, "30", "Monitoring"),
        (SettingsKeys.AlertCpuThreshold, "90", "Monitoring"),
        (SettingsKeys.AlertMemoryThreshold, "92", "Monitoring"),
        (SettingsKeys.AlertDiskThreshold, "10", "Monitoring"),
        (SettingsKeys.AlertTemperatureThreshold, "85", "Monitoring"),
        (SettingsKeys.DiscoveryTimeoutMs, "800", "Discovery"),
        (SettingsKeys.DiscoveryConcurrency, "24", "Discovery"),
        (SettingsKeys.DiscoveryIntervalMinutes, "0", "Discovery"),
        (SettingsKeys.DiscoveryProbePorts, $"{HubConstants.DefaultAgentPort},445,3389", "Discovery"),
        (SettingsKeys.AgentAutoApprove, "false", "Agent"),
        (SettingsKeys.AgentAllowCommands, "true", "Agent"),
        (SettingsKeys.AgentAllowedRoots, @"C:\Users;C:\ProgramData", "Agent"),
        (SettingsKeys.SharedFolderEnabled, "true", "Shared folder"),
        (SettingsKeys.SharedFolderPath, @"C:\zzz", "Shared folder"),
        (SettingsKeys.SharedFolderIntervalSeconds, "15", "Shared folder"),
        (SettingsKeys.SharedFolderMaxFileBytes, "268435456", "Shared folder"),
        (SettingsKeys.SharedFolderMaxFiles, "20000", "Shared folder"),
        (SettingsKeys.AuditRetentionDays, "365", "Security"),
        (SettingsKeys.SessionTimeoutMinutes, "60", "Security"),
        (SettingsKeys.RequireTls, "true", "Security"),
        (SettingsKeys.CommandBlocklistEnabled, "true", "Security"),
        (SettingsKeys.CommandAllowlist, string.Empty, "Security"),
        (SettingsKeys.CommandApprovalRequired, "false", "Security"),
        (SettingsKeys.RemoteConsentRequired, "false", "Security"),
        (SettingsKeys.AuditAnchorPath, string.Empty, "Security")
    ];
}
