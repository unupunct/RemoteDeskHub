using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class FileTransferService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    IAgentTransport transport,
    ISessionContext session,
    IAuditService audit,
    IAppEvents events,
    IClock clock) : IFileTransferService
{
    private const int ChunkSize = 1024 * 1024;

    public async Task<FileListDto> ListRemoteAsync(Guid computerId, string path, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.TransferFiles);
        var computer = await LoadComputerAsync(computerId, cancellationToken);
        return await transport.ListFilesAsync(computer, path, cancellationToken);
    }

    public async Task<OperationResult> CreateRemoteDirectoryAsync(string actor, Guid computerId, string path, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.TransferFiles, actor);
        var computer = await LoadComputerAsync(computerId, cancellationToken);
        try
        {
            var result = await transport.CreateDirectoryAsync(computer, path, cancellationToken);
            await audit.LogAsync(actor, AuditOperations.DirectoryCreated, computer.Name, computer.Id,
                result: result.Success ? "Success" : "Failed", error: result.Error, details: path, cancellationToken: cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task<OperationResult> RenameRemoteAsync(string actor, Guid computerId, string path, string newName, bool confirmed, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.TransferFiles, actor);
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return OperationResult.Fail("Enter a valid file name without path separators.");
        }

        if (!confirmed)
        {
            return OperationResult.Fail("The rename operation was not confirmed.");
        }

        var computer = await LoadComputerAsync(computerId, cancellationToken);
        try
        {
            var result = await transport.RenameFileAsync(computer, path, newName, true, cancellationToken);
            await audit.LogAsync(actor, AuditOperations.FileRenamed, computer.Name, computer.Id,
                result: result.Success ? "Success" : "Failed", error: result.Error,
                details: $"{path} -> {newName}", cancellationToken: cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task<OperationResult> DeleteRemoteAsync(string actor, Guid computerId, string path, bool recursive, bool confirmed, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.DeleteRemoteFiles, actor);
        if (!confirmed)
        {
            return OperationResult.Fail("Delete was not confirmed.");
        }

        if (recursive && !confirmed)
        {
            return OperationResult.Fail("Recursive deletion requires an additional confirmation.");
        }

        var computer = await LoadComputerAsync(computerId, cancellationToken);
        try
        {
            var result = await transport.DeleteFileAsync(computer, path, recursive, true, cancellationToken);
            await audit.LogAsync(actor, AuditOperations.FileDeleted, computer.Name, computer.Id,
                result: result.Success ? "Success" : "Failed", error: result.Error,
                details: $"{(recursive ? "Recursive delete" : "Delete")} {path}", cancellationToken: cancellationToken);
            events.RaiseDataChanged();
            return result;
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task<FileTransferView> DownloadAsync(string actor, Guid computerId, string remotePath, string localPath, IProgress<TransferProgress>? progress, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.TransferFiles, actor);
        if (string.IsNullOrWhiteSpace(localPath) || !Path.IsPathFullyQualified(localPath))
        {
            throw new InvalidOperationException("Choose a local destination file.");
        }

        var localDirectory = Path.GetDirectoryName(localPath);
        if (string.IsNullOrWhiteSpace(localDirectory) || !Directory.Exists(localDirectory))
        {
            throw new InvalidOperationException("The local destination folder does not exist.");
        }

        var computer = await LoadComputerAsync(computerId, cancellationToken);
        var stat = await transport.StatFileAsync(computer, remotePath, false, cancellationToken);
        if (!stat.Exists)
        {
            throw new InvalidOperationException("The remote file does not exist.");
        }

        if (stat.IsDirectory)
        {
            throw new InvalidOperationException("Select a file, not a folder, to download.");
        }

        var transfer = new FileTransfer
        {
            ComputerId = computer.Id,
            ComputerName = computer.Name,
            Actor = actor,
            Direction = TransferDirection.Download,
            LocalPath = localPath,
            RemotePath = remotePath,
            SizeBytes = stat.SizeBytes,
            Status = TransferStatus.Running,
            StartedUtc = clock.UtcNow
        };
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.FileTransfers.Add(transfer);
        await db.SaveChangesAsync(cancellationToken);
        events.RaiseDataChanged();

        var tempPath = localPath + ".rdpart";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, ChunkSize, useAsync: true))
            {
                if (stream.Length > stat.SizeBytes)
                {
                    stream.SetLength(0);
                }

                var offset = stream.Length;
                stream.Position = offset;
                while (offset < stat.SizeBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = await transport.ReadFileChunkAsync(computer, remotePath, offset, ChunkSize, cancellationToken);
                    if (chunk.Length <= 0)
                    {
                        break;
                    }

                    var bytes = Convert.FromBase64String(chunk.DataBase64);
                    await stream.WriteAsync(bytes, cancellationToken);
                    offset += bytes.Length;
                    transfer.TransferredBytes = offset;
                    progress?.Report(new TransferProgress(offset, stat.SizeBytes,
                        offset / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds), Path.GetFileName(remotePath)));
                }

                await stream.FlushAsync(cancellationToken);
            }

            var remoteHash = await transport.GetFileHashAsync(computer, remotePath, cancellationToken);
            var localHash = await ComputeFileHashAsync(tempPath, cancellationToken);
            if (!string.Equals(remoteHash.Sha256, localHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Integrity verification failed: the downloaded file does not match the remote SHA-256 hash.");
            }

            File.Move(tempPath, localPath, true);
            transfer.Sha256 = localHash;
            transfer.TransferredBytes = stat.SizeBytes;
            transfer.Status = TransferStatus.Completed;
            transfer.CompletedUtc = clock.UtcNow;
            transfer.SpeedBps = stat.SizeBytes / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            await db.SaveChangesAsync(cancellationToken);
            await audit.LogAsync(actor, AuditOperations.FileDownloaded, computer.Name, computer.Id,
                durationMs: (long)stopwatch.Elapsed.TotalMilliseconds,
                details: $"{remotePath} -> {localPath} ({stat.SizeBytes:N0} bytes, SHA-256 {localHash}).", cancellationToken: cancellationToken);
            progress?.Report(new TransferProgress(stat.SizeBytes, stat.SizeBytes, transfer.SpeedBps ?? 0, Path.GetFileName(remotePath)));
            events.RaiseDataChanged();
            return Map(transfer);
        }
        catch (OperationCanceledException)
        {
            transfer.Status = TransferStatus.Interrupted;
            transfer.CompletedUtc = clock.UtcNow;
            transfer.Error = "Cancelled. The partial file was kept so the download can resume.";
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            transfer.Status = TransferStatus.Failed;
            transfer.CompletedUtc = clock.UtcNow;
            transfer.Error = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            await audit.LogAsync(actor, AuditOperations.FileDownloaded, computer.Name, computer.Id,
                result: "Failed", error: ex.Message, details: $"{remotePath} -> {localPath}", cancellationToken: CancellationToken.None);
            events.RaiseDataChanged();
            return Map(transfer);
        }
    }

    public async Task<FileTransferView> UploadAsync(string actor, Guid computerId, string localPath, string remotePath, IProgress<TransferProgress>? progress, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.TransferFiles, actor);
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            throw new InvalidOperationException("The local source file does not exist.");
        }

        var computer = await LoadComputerAsync(computerId, cancellationToken);
        var info = new FileInfo(localPath);
        var total = info.Length;
        var localHash = await ComputeFileHashAsync(localPath, cancellationToken);
        var transfer = new FileTransfer
        {
            ComputerId = computer.Id,
            ComputerName = computer.Name,
            Actor = actor,
            Direction = TransferDirection.Upload,
            LocalPath = localPath,
            RemotePath = remotePath,
            SizeBytes = total,
            Sha256 = localHash,
            Status = TransferStatus.Running,
            StartedUtc = clock.UtcNow
        };
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.FileTransfers.Add(transfer);
        await db.SaveChangesAsync(cancellationToken);
        events.RaiseDataChanged();

        var temporaryRemotePath = remotePath.TrimEnd('\\') + ".rdpart";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var existing = await transport.StatFileAsync(computer, temporaryRemotePath, false, cancellationToken);
            long offset = existing is { Exists: true, SizeBytes: > 0 } && existing.SizeBytes < total ? existing.SizeBytes : 0;

            await using (var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true))
            {
                stream.Position = offset;
                var buffer = new byte[ChunkSize];
                while (offset < total)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var toRead = (int)Math.Min(buffer.Length, total - offset);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                    if (read <= 0)
                    {
                        break;
                    }

                    var isFinal = offset + read >= total;
                    var response = await transport.WriteFileChunkAsync(computer, new FileWriteRequestDto
                    {
                        Path = temporaryRemotePath,
                        Offset = offset,
                        DataBase64 = Convert.ToBase64String(buffer, 0, read),
                        TotalLength = total,
                        FinalChunk = isFinal,
                        ExpectedSha256 = isFinal ? localHash : null
                    }, cancellationToken);

                    if (!response.Success)
                    {
                        throw new InvalidOperationException(response.HashMismatch
                            ? "Integrity verification failed: the remote SHA-256 hash does not match the local file."
                            : response.Error ?? "The remote agent rejected the file chunk.");
                    }

                    offset += read;
                    transfer.TransferredBytes = offset;
                    progress?.Report(new TransferProgress(offset, total,
                        offset / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds), info.Name));
                }
            }

            if (total == 0)
            {
                var response = await transport.WriteFileChunkAsync(computer, new FileWriteRequestDto
                {
                    Path = temporaryRemotePath,
                    Offset = 0,
                    DataBase64 = string.Empty,
                    TotalLength = 0,
                    FinalChunk = true,
                    ExpectedSha256 = localHash
                }, cancellationToken);
                if (!response.Success)
                {
                    throw new InvalidOperationException(response.Error ?? "The remote agent rejected the empty file.");
                }
            }

            var finalize = await transport.FinalizeUploadAsync(computer, temporaryRemotePath, remotePath, cancellationToken);
            if (!finalize.Success)
            {
                throw new InvalidOperationException(finalize.Error ?? "The upload could not be finalized.");
            }

            transfer.Status = TransferStatus.Completed;
            transfer.CompletedUtc = clock.UtcNow;
            transfer.TransferredBytes = total;
            transfer.SpeedBps = total / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            await db.SaveChangesAsync(cancellationToken);
            await audit.LogAsync(actor, AuditOperations.FileUploaded, computer.Name, computer.Id,
                durationMs: (long)stopwatch.Elapsed.TotalMilliseconds,
                details: $"{localPath} -> {remotePath} ({total:N0} bytes, SHA-256 {localHash}).", cancellationToken: cancellationToken);
            progress?.Report(new TransferProgress(total, total, transfer.SpeedBps ?? 0, info.Name));
            events.RaiseDataChanged();
            return Map(transfer);
        }
        catch (OperationCanceledException)
        {
            transfer.Status = TransferStatus.Interrupted;
            transfer.CompletedUtc = clock.UtcNow;
            transfer.Error = "Cancelled. The partial remote file was kept so the upload can resume.";
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            transfer.Status = TransferStatus.Failed;
            transfer.CompletedUtc = clock.UtcNow;
            transfer.Error = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            await audit.LogAsync(actor, AuditOperations.FileUploaded, computer.Name, computer.Id,
                result: "Failed", error: ex.Message, details: $"{localPath} -> {remotePath}", cancellationToken: CancellationToken.None);
            events.RaiseDataChanged();
            return Map(transfer);
        }
    }

    public async Task<IReadOnlyList<FileTransferView>> GetHistoryAsync(Guid? computerId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.FileTransfers.AsNoTracking().AsQueryable();
        if (computerId.HasValue)
        {
            query = query.Where(t => t.ComputerId == computerId);
        }

        return await query.OrderByDescending(t => t.StartedUtc).Take(Math.Clamp(limit, 1, 1000)).Select(t => new FileTransferView
        {
            Id = t.Id,
            ComputerName = t.ComputerName,
            Actor = t.Actor,
            Direction = t.Direction,
            LocalPath = t.LocalPath,
            RemotePath = t.RemotePath,
            SizeBytes = t.SizeBytes,
            TransferredBytes = t.TransferredBytes,
            Sha256 = t.Sha256,
            Status = t.Status,
            StartedUtc = t.StartedUtc,
            CompletedUtc = t.CompletedUtc,
            Error = t.Error,
            SpeedBps = t.SpeedBps
        }).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetAllowedRootsAsync(Guid computerId, CancellationToken cancellationToken = default)
    {
        var computer = await LoadComputerAsync(computerId, cancellationToken);
        var info = await transport.GetInfoAsync(computer, cancellationToken);
        return info.AllowedFileRoots;
    }

    private async Task<Computer> LoadComputerAsync(Guid computerId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Computers.AsNoTracking().Include(c => c.Agent)
            .FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken)
            ?? throw new InvalidOperationException("Computer not found.");
    }

    private static FileTransferView Map(FileTransfer t) => new()
    {
        Id = t.Id,
        ComputerName = t.ComputerName,
        Actor = t.Actor,
        Direction = t.Direction,
        LocalPath = t.LocalPath,
        RemotePath = t.RemotePath,
        SizeBytes = t.SizeBytes,
        TransferredBytes = t.TransferredBytes,
        Sha256 = t.Sha256,
        Status = t.Status,
        StartedUtc = t.StartedUtc,
        CompletedUtc = t.CompletedUtc,
        Error = t.Error,
        SpeedBps = t.SpeedBps
    };

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}
