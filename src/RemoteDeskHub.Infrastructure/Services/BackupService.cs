using Microsoft.Data.Sqlite;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class BackupService(
    IAppPaths paths,
    ISettingsService settings,
    ISessionContext session,
    IAuditService audit,
    IClock clock) : IBackupService
{
    public async Task<OperationResult<string>> CreateBackupAsync(string actor, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.BackupDatabase, actor);
        try
        {
            Directory.CreateDirectory(paths.BackupsDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var fileName = $"remotedeskhub-{stamp}.db";
            var target = Path.Combine(paths.BackupsDirectory, fileName);
            for (var suffix = 1; File.Exists(target); suffix++)
            {
                fileName = $"remotedeskhub-{stamp}-{suffix}.db";
                target = Path.Combine(paths.BackupsDirectory, fileName);
            }

            await using (var source = new SqliteConnection($"Data Source={paths.DatabasePath};Mode=ReadOnly;Pooling=False"))
            await using (var destination = new SqliteConnection($"Data Source={target};Pooling=False"))
            {
                await source.OpenAsync(cancellationToken);
                await destination.OpenAsync(cancellationToken);
                source.BackupDatabase(destination);
            }

            var integrity = await CheckIntegrityAsync(target, cancellationToken);
            if (!integrity.Success)
            {
                File.Delete(target);
                return OperationResult<string>.Fail($"Backup verification failed: {integrity.Error}");
            }

            await settings.SetAsync(SettingsKeys.LastBackupUtc, clock.UtcNow.ToString("O"), "General", cancellationToken);
            await audit.LogAsync(actor, AuditOperations.BackupCreated, details: target, cancellationToken: cancellationToken);
            return OperationResult<string>.Ok(target, $"Backup created: {fileName}");
        }
        catch (Exception ex)
        {
            return OperationResult<string>.Fail($"Database backup failed: {ex.Message}");
        }
    }

    public Task<IReadOnlyList<FileInfo>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.BackupDatabase);
        if (!Directory.Exists(paths.BackupsDirectory))
        {
            return Task.FromResult<IReadOnlyList<FileInfo>>([]);
        }

        IReadOnlyList<FileInfo> files = new DirectoryInfo(paths.BackupsDirectory)
            .GetFiles("remotedeskhub-*.db")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        return Task.FromResult(files);
    }

    public async Task<OperationResult> RestoreBackupAsync(string actor, string backupFileName, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.BackupDatabase, actor);
        var safeName = Path.GetFileName(backupFileName);
        if (!string.Equals(safeName, backupFileName, StringComparison.Ordinal))
        {
            return OperationResult.Fail("Invalid backup file name.");
        }

        var source = Path.Combine(paths.BackupsDirectory, safeName);
        if (!File.Exists(source))
        {
            return OperationResult.Fail("Backup file not found.");
        }

        var sourceIntegrity = await CheckIntegrityAsync(source, cancellationToken);
        if (!sourceIntegrity.Success)
        {
            return OperationResult.Fail($"The selected backup failed integrity verification: {sourceIntegrity.Error}");
        }

        var safety = await CreateBackupAsync(actor, cancellationToken);
        if (!safety.Success)
        {
            return OperationResult.Fail($"Restore was aborted because a safety backup could not be created: {safety.Error}");
        }

        try
        {
            // Never overwrite a live SQLite database while background services may still hold
            // connections. Stage the restore and apply it at the next startup, before any
            // DbContext is created.
            var staging = paths.DatabasePath + ".restore-pending";
            var marker = paths.DatabasePath + ".restore-pending.marker";
            File.Copy(source, staging, true);
            await File.WriteAllTextAsync(marker, safeName, cancellationToken);

            await audit.LogAsync(actor, "Database.RestoreStaged",
                details: $"Restore from {safeName} staged. Safety backup: {safety.Value}",
                cancellationToken: cancellationToken);
            return OperationResult.Ok("Database restore staged. Close and restart RemoteDesk Hub to apply it.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Restore could not be staged: {ex.Message}");
        }
    }

    public async Task<OperationResult> VerifyDatabaseAsync(CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.BackupDatabase);
        return await CheckIntegrityAsync(paths.DatabasePath, cancellationToken);
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
}
