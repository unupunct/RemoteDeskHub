using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;
using RemoteDeskHub.Shared.Security;

namespace RemoteDeskHub.Infrastructure.Services;

/// <summary>
/// Hub-side shared-folder coordinator. The hub-local folder and every online agent folder are peers:
/// new or changed files are pulled into the hub's content-addressed store, then pushed to the other
/// peers. Deletions are persistent tombstones. When two peers edit the same path at once, the hub
/// copy wins and the losing local version is kept as a conflict copy on that peer.
/// </summary>
public sealed class SharedFolderSyncService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    IAgentTransport transport,
    IAppPaths paths,
    ISettingsService settings,
    IAuditService audit,
    IAppEvents events,
    IClock clock,
    SharedFolderRootManager rootManager,
    ILogger<SharedFolderSyncService> logger) : BackgroundService
{
    private const int ChunkSize = 1024 * 1024;
    private readonly SharedFolderScanner _scanner = new();
    private string? _lastError;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(15);
            try
            {
                delay = await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                logger.LogError(ex, "The shared-folder synchronization cycle failed.");
                await TryWriteStateAsync(stoppingToken);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Runs one synchronization cycle immediately. The background loop calls this on its configured
    /// interval; diagnostics and tests can use it without waiting for the timer.
    /// </summary>
    public Task<TimeSpan> RunOnceAsync(CancellationToken cancellationToken = default) =>
        RunCycleAsync(cancellationToken);

    private async Task<TimeSpan> RunCycleAsync(CancellationToken cancellationToken)
    {        var enabled = await settings.GetAsync(SettingsKeys.SharedFolderEnabled, true, cancellationToken);
        var root = await settings.GetAsync(SettingsKeys.SharedFolderPath, @"C:\zzz", cancellationToken);
        var interval = await settings.GetAsync(SettingsKeys.SharedFolderIntervalSeconds, 15, cancellationToken);
        var maxFileBytes = await settings.GetAsync(SettingsKeys.SharedFolderMaxFileBytes, 268_435_456L, cancellationToken);
        var maxFiles = await settings.GetAsync(SettingsKeys.SharedFolderMaxFiles, 20_000, cancellationToken);
        var delay = TimeSpan.FromSeconds(Math.Clamp(interval, 5, 300));

        if (!enabled)
        {
            _lastError = null;
            await TryWriteStateAsync(cancellationToken);
            return delay;
        }

        if (!rootManager.TryEnsureRoot(root, out var normalizedRoot, out var rootError))
        {
            _lastError = rootError;
            logger.LogWarning("Shared-folder sync skipped: {Error}", rootError);
            await TryWriteStateAsync(cancellationToken);
            return delay;
        }

        var errors = new List<string>();
        var peerPaths = new List<string>();

        try
        {
            var local = await SyncLocalPeerAsync(normalizedRoot, maxFileBytes, maxFiles, cancellationToken);
            AppendResult(local, "hub folder", errors, peerPaths);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"Hub folder: {ex.Message}");
            logger.LogWarning(ex, "The hub-local shared folder could not be synchronized.");
        }

        List<Agent> agents;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var cutoff = clock.UtcNow.AddMinutes(-5);
            agents = await db.Agents.AsNoTracking()
                .Include(a => a.Computer)
                .Where(a => a.State == AgentState.Active && a.LastSeenUtc != null && a.LastSeenUtc >= cutoff)
                .ToListAsync(cancellationToken);
        }

        foreach (var agent in agents)
        {
            if (agent.Computer is null)
            {
                continue;
            }

            // The hub machine's local folder is handled directly above. If an agent is also
            // installed on the hub, letting both peers manage the same directory would make every
            // write loop back through the other peer.
            if (string.Equals(agent.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            agent.Computer.Agent = agent;
            try
            {
                var result = await SyncAgentAsync(agent.Computer, normalizedRoot, maxFileBytes, maxFiles, cancellationToken);
                AppendResult(result, agent.Computer.Name, errors, peerPaths);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{agent.Computer.Name}: {ex.Message}");
                logger.LogWarning(ex, "Shared-folder sync with {Computer} failed.", agent.Computer.Name);
            }
        }

        // A second local pass makes files pulled from an agent visible in the hub's own folder in
        // the same cycle instead of waiting for the next poll.
        try
        {
            var localAfterAgents = await SyncLocalPeerAsync(normalizedRoot, maxFileBytes, maxFiles, cancellationToken);
            AppendResult(localAfterAgents, "hub folder", errors, peerPaths);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"Hub folder: {ex.Message}");
            logger.LogWarning(ex, "The hub-local shared folder could not be refreshed after agent sync.");
        }

        _lastError = errors.Count == 0 ? null : string.Join(" | ", errors.Take(4));
        await TryWriteStateAsync(cancellationToken);
        events.RaiseDataChanged();

        if (errors.Count > 0)
        {
            logger.LogWarning("Shared-folder cycle completed with {Count} peer error(s): {Error}", errors.Count, _lastError);
        }

        return delay;
    }

    private async Task<SyncPeerResult> SyncLocalPeerAsync(
        string root,
        long maxFileBytes,
        int maxFiles,
        CancellationToken cancellationToken)
    {
        var manifest = await _scanner.ScanAsync(root, maxFiles, maxFileBytes, cancellationToken);
        if (!string.IsNullOrWhiteSpace(manifest.Error))
        {
            return new SyncPeerResult(0, 0, 0, 0, [], manifest.Error);
        }

        if (manifest.IsTruncated)
        {
            return new SyncPeerResult(0, 0, 0, 0, [], "The local shared folder exceeds the configured file limit.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var central = await db.SharedFolderFiles.AsNoTracking().ToListAsync(cancellationToken);
        var local = await db.SharedFolderLocalFiles.AsNoTracking().ToListAsync(cancellationToken);
        var plan = SharedFolderPlanner.Plan(
            central.Select(MapCentral).ToList(),
            local.Select(MapLocal).ToList(),
            manifest.Entries,
            Environment.MachineName,
            new DateTimeOffset(clock.UtcNow, TimeSpan.Zero),
            manifest.SkippedPaths);

        var result = new SyncPeerResult(0, 0, 0, 0, [], null);
        foreach (var operation in plan.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                switch (operation.Action)
                {
                    case SharedFolderAction.NoChange:
                        await UpsertLocalRowAsync(db, operation.Path, operation.Entry!, operation.Central!.Sha256, cancellationToken);
                        break;

                    case SharedFolderAction.PullFromAgent:
                        await CopyLocalFileToBlobAsync(root, operation.Entry!, cancellationToken);
                        await CommitCentralAsync(db, operation.Path, operation.Entry!, "Hub local folder", cancellationToken);
                        await UpsertLocalRowAsync(db, operation.Path, operation.Entry!, operation.Entry!.Sha256, cancellationToken);
                        result = result with { Pulled = result.Pulled + 1 };
                        result.Paths.Add(operation.Path);
                        break;

                    case SharedFolderAction.PushToAgent:
                        await WriteBlobToLocalAsync(root, operation.Path, operation.Central!, cancellationToken);
                        await UpsertLocalRowAsync(
                            db,
                            operation.Path,
                            new SharedFolderManifestEntryDto
                            {
                                Path = operation.Path,
                                SizeBytes = operation.Central!.SizeBytes,
                                LastWriteUtc = new DateTimeOffset(operation.Central!.LastWriteUtc, TimeSpan.Zero),
                                Sha256 = operation.Central!.Sha256
                            },
                            operation.Central!.Sha256,
                            cancellationToken);
                        result = result with { Pushed = result.Pushed + 1 };
                        result.Paths.Add(operation.Path);
                        break;

                    case SharedFolderAction.ConflictPushToAgent:
                        await RenameLocalAsync(root, operation.Path, operation.ConflictPath!, cancellationToken);
                        await WriteBlobToLocalAsync(root, operation.Path, operation.Central!, cancellationToken);
                        await UpsertLocalRowAsync(
                            db,
                            operation.Path,
                            new SharedFolderManifestEntryDto
                            {
                                Path = operation.Path,
                                SizeBytes = operation.Central!.SizeBytes,
                                LastWriteUtc = new DateTimeOffset(operation.Central!.LastWriteUtc, TimeSpan.Zero),
                                Sha256 = operation.Central!.Sha256
                            },
                            operation.Central!.Sha256,
                            cancellationToken);
                        result = result with { Conflicts = result.Conflicts + 1, Pushed = result.Pushed + 1 };
                        result.Paths.Add(operation.ConflictPath!);
                        await audit.LogAsync(
                            "SYSTEM",
                            AuditOperations.SharedFolderConflict,
                            "Hub local folder",
                            details: $"{operation.Path} -> {operation.ConflictPath}",
                            cancellationToken: cancellationToken);
                        break;

                    case SharedFolderAction.DeleteOnAgent:
                        DeleteLocalFile(root, operation.Path);
                        await RemoveLocalRowAsync(db, operation.Path, cancellationToken);
                        result = result with { Deleted = result.Deleted + 1 };
                        result.Paths.Add(operation.Path);
                        break;

                    case SharedFolderAction.PreserveConflictAndDelete:
                        await RenameLocalAsync(root, operation.Path, operation.ConflictPath!, cancellationToken);
                        await RemoveLocalRowAsync(db, operation.Path, cancellationToken);
                        result = result with { Conflicts = result.Conflicts + 1, Deleted = result.Deleted + 1 };
                        result.Paths.Add(operation.ConflictPath!);
                        await audit.LogAsync(
                            "SYSTEM",
                            AuditOperations.SharedFolderConflict,
                            "Hub local folder",
                            details: $"{operation.Path} -> {operation.ConflictPath}",
                            cancellationToken: cancellationToken);
                        break;

                    case SharedFolderAction.MarkDeleted:
                        await MarkCentralDeletedAsync(db, operation.Path, cancellationToken);
                        await RemoveLocalRowAsync(db, operation.Path, cancellationToken);
                        result = result with { Deleted = result.Deleted + 1 };
                        result.Paths.Add(operation.Path);
                        break;

                    case SharedFolderAction.RemoveAgentRow:
                        await RemoveLocalRowAsync(db, operation.Path, cancellationToken);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = result with { Error = $"{operation.Path}: {ex.Message}" };
                logger.LogWarning(ex, "Shared-folder local operation {Action} failed for {Path}.", operation.Action, operation.Path);
            }
        }

        if (result.Pulled + result.Pushed + result.Deleted + result.Conflicts > 0)
        {
            await AuditPeerAsync("Hub local folder", null, result, cancellationToken);
        }

        return result;
    }

    private async Task<SyncPeerResult> SyncAgentAsync(
        Computer computer,
        string root,
        long maxFileBytes,
        int maxFiles,
        CancellationToken cancellationToken)
    {
        var manifest = await transport.GetSharedFolderManifestAsync(computer, cancellationToken);
        if (!manifest.Enabled)
        {
            return new SyncPeerResult(0, 0, 0, 0, [], "Shared-folder sync is disabled on the agent.");
        }

        if (!string.IsNullOrWhiteSpace(manifest.Error) || manifest.IsTruncated)
        {
            return new SyncPeerResult(0, 0, 0, 0, [], manifest.Error ?? "The agent's shared folder is truncated.");
        }

        if (!string.IsNullOrWhiteSpace(manifest.Root) &&
            !string.Equals(Path.GetFullPath(manifest.Root), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
        {
            return new SyncPeerResult(0, 0, 0, 0, [],
                $"The agent shared-folder root '{manifest.Root}' does not match the hub root '{root}'.");
        }

        if (manifest.Entries.Count > maxFiles)
        {
            return new SyncPeerResult(0, 0, 0, 0, [], "The agent shared folder exceeds the configured file limit.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var central = await db.SharedFolderFiles.AsNoTracking().ToListAsync(cancellationToken);
        var previous = await db.SharedFolderAgentFiles.AsNoTracking()
            .Where(x => x.ComputerId == computer.Id)
            .ToListAsync(cancellationToken);
        var plan = SharedFolderPlanner.Plan(
            central.Select(MapCentral).ToList(),
            previous.Select(MapAgent).ToList(),
            manifest.Entries,
            computer.Name,
            new DateTimeOffset(clock.UtcNow, TimeSpan.Zero),
            manifest.SkippedPaths);

        var result = new SyncPeerResult(0, 0, 0, 0, [], null);
        foreach (var operation in plan.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                switch (operation.Action)
                {
                    case SharedFolderAction.NoChange:
                        await UpsertAgentRowAsync(db, computer.Id, operation.Path, operation.Entry!, operation.Central!.Sha256, cancellationToken);
                        break;

                    case SharedFolderAction.PullFromAgent:
                        await CopyAgentFileToBlobAsync(computer, root, operation.Entry!, cancellationToken);
                        await CommitCentralAsync(db, operation.Path, operation.Entry!, computer.Name, cancellationToken);
                        await UpsertAgentRowAsync(db, computer.Id, operation.Path, operation.Entry!, operation.Entry!.Sha256, cancellationToken);
                        result = result with { Pulled = result.Pulled + 1 };
                        result.Paths.Add(operation.Path);
                        break;

                    case SharedFolderAction.PushToAgent:
                        await WriteBlobToAgentAsync(computer, root, operation.Path, operation.Central!, cancellationToken);
                        await UpsertAgentRowAsync(
                            db,
                            computer.Id,
                            operation.Path,
                            new SharedFolderManifestEntryDto
                            {
                                Path = operation.Path,
                                SizeBytes = operation.Central!.SizeBytes,
                                LastWriteUtc = new DateTimeOffset(operation.Central!.LastWriteUtc, TimeSpan.Zero),
                                Sha256 = operation.Central!.Sha256
                            },
                            operation.Central!.Sha256,
                            cancellationToken);
                        result = result with { Pushed = result.Pushed + 1 };
                        result.Paths.Add(operation.Path);
                        break;

                    case SharedFolderAction.ConflictPushToAgent:
                        await RenameAgentFileAsync(computer, root, operation.Path, operation.ConflictPath!, cancellationToken);
                        await WriteBlobToAgentAsync(computer, root, operation.Path, operation.Central!, cancellationToken);
                        await UpsertAgentRowAsync(
                            db,
                            computer.Id,
                            operation.Path,
                            new SharedFolderManifestEntryDto
                            {
                                Path = operation.Path,
                                SizeBytes = operation.Central!.SizeBytes,
                                LastWriteUtc = new DateTimeOffset(operation.Central!.LastWriteUtc, TimeSpan.Zero),
                                Sha256 = operation.Central!.Sha256
                            },
                            operation.Central!.Sha256,
                            cancellationToken);
                        result = result with { Conflicts = result.Conflicts + 1, Pushed = result.Pushed + 1 };
                        result.Paths.Add(operation.ConflictPath!);
                        await audit.LogAsync(
                            "SYSTEM",
                            AuditOperations.SharedFolderConflict,
                            computer.Name,
                            computer.Id,
                            details: $"{operation.Path} -> {operation.ConflictPath}",
                            cancellationToken: cancellationToken);
                        break;

                    case SharedFolderAction.DeleteOnAgent:
                        await DeleteAgentFileAsync(computer, root, operation.Path, cancellationToken);
                        await RemoveAgentRowAsync(db, computer.Id, operation.Path, cancellationToken);
                        result = result with { Deleted = result.Deleted + 1 };
                        result.Paths.Add(operation.Path);
                        break;

                    case SharedFolderAction.PreserveConflictAndDelete:
                        await RenameAgentFileAsync(computer, root, operation.Path, operation.ConflictPath!, cancellationToken);
                        await RemoveAgentRowAsync(db, computer.Id, operation.Path, cancellationToken);
                        result = result with { Conflicts = result.Conflicts + 1, Deleted = result.Deleted + 1 };
                        result.Paths.Add(operation.ConflictPath!);
                        await audit.LogAsync(
                            "SYSTEM",
                            AuditOperations.SharedFolderConflict,
                            computer.Name,
                            computer.Id,
                            details: $"{operation.Path} -> {operation.ConflictPath}",
                            cancellationToken: cancellationToken);
                        break;

                    case SharedFolderAction.MarkDeleted:
                        await MarkCentralDeletedAsync(db, operation.Path, cancellationToken);
                        await RemoveAgentRowAsync(db, computer.Id, operation.Path, cancellationToken);
                        result = result with { Deleted = result.Deleted + 1 };
                        result.Paths.Add(operation.Path);
                        break;

                    case SharedFolderAction.RemoveAgentRow:
                        await RemoveAgentRowAsync(db, computer.Id, operation.Path, cancellationToken);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = result with { Error = $"{operation.Path}: {ex.Message}" };
                logger.LogWarning(ex, "Shared-folder agent operation {Action} failed for {Computer} {Path}.",
                    operation.Action, computer.Name, operation.Path);
            }
        }

        if (result.Pulled + result.Pushed + result.Deleted + result.Conflicts > 0)
        {
            await AuditPeerAsync(computer.Name, computer.Id, result, cancellationToken);
        }

        return result;
    }

    private async Task CopyLocalFileToBlobAsync(
        string root,
        SharedFolderManifestEntryDto entry,
        CancellationToken cancellationToken)
    {
        if (!PathGuard.TryResolveRelativeWithinRoot(root, entry.Path, out var full, out _, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var staging = Path.Combine(paths.SharedFolderStagingDirectory, $"local-{Guid.NewGuid():N}.part");
        try
        {
            await using (var source = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var target = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            var hash = await ComputeHashAsync(staging, cancellationToken);
            if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The local shared file changed while it was being read.");
            }

            MoveToBlob(staging, entry.Sha256);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private async Task CopyAgentFileToBlobAsync(
        Computer computer,
        string root,
        SharedFolderManifestEntryDto entry,
        CancellationToken cancellationToken)
    {
        if (!PathGuard.TryResolveRelativeWithinRoot(root, entry.Path, out var full, out _, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var staging = Path.Combine(paths.SharedFolderStagingDirectory, $"agent-{Guid.NewGuid():N}.part");
        try
        {
            await using (var target = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                long offset = 0;
                while (offset < entry.SizeBytes)
                {
                    var length = (int)Math.Min(ChunkSize, entry.SizeBytes - offset);
                    var chunk = await transport.ReadFileChunkAsync(computer, full, offset, length, cancellationToken);
                    var bytes = Convert.FromBase64String(chunk.DataBase64);
                    if (bytes.Length == 0 || bytes.Length > length)
                    {
                        throw new IOException("The agent returned an invalid shared-file chunk.");
                    }

                    await target.WriteAsync(bytes, cancellationToken);
                    offset += bytes.Length;
                }
            }

            var info = new FileInfo(staging);
            if (info.Length != entry.SizeBytes)
            {
                throw new IOException($"The downloaded size {info.Length} does not match the manifest size {entry.SizeBytes}.");
            }

            var hash = await ComputeHashAsync(staging, cancellationToken);
            if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The downloaded shared file failed SHA-256 verification.");
            }

            MoveToBlob(staging, entry.Sha256);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private async Task WriteBlobToLocalAsync(
        string root,
        string relativePath,
        CentralSharedFile central,
        CancellationToken cancellationToken)
    {
        if (!PathGuard.TryResolveRelativeWithinRoot(root, relativePath, out var target, out _, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var stagingDirectory = Path.Combine(root, SharedFolderScanner.StagingDirectoryName);
        Directory.CreateDirectory(stagingDirectory);
        var temporary = Path.Combine(stagingDirectory, $"hub-{Guid.NewGuid():N}.part");
        if (!PathGuard.TryNormalizeWithinRoots(temporary, [root], out temporary, out error))
        {
            throw new InvalidOperationException(error);
        }

        try
        {
            var blob = BlobPath(central.Sha256);
            if (!File.Exists(blob))
            {
                throw new FileNotFoundException("The hub copy of the shared file is missing from the blob store.", blob);
            }

            await using (var source = new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var targetStream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(targetStream, cancellationToken);
            }

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Move(temporary, target, true);
            File.SetLastWriteTimeUtc(target, central.LastWriteUtc);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task WriteBlobToAgentAsync(
        Computer computer,
        string root,
        string relativePath,
        CentralSharedFile central,
        CancellationToken cancellationToken)
    {
        if (!PathGuard.TryResolveRelativeWithinRoot(root, relativePath, out var target, out _, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var temporary = Path.Combine(root, SharedFolderScanner.StagingDirectoryName, $"push-{Guid.NewGuid():N}.part");
        var blob = BlobPath(central.Sha256);
        if (!File.Exists(blob))
        {
            throw new FileNotFoundException("The hub copy of the shared file is missing from the blob store.", blob);
        }

        await using var source = new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long offset = 0;
        if (central.SizeBytes == 0)
        {
            var empty = await transport.WriteFileChunkAsync(computer, new FileWriteRequestDto
            {
                Path = temporary,
                Offset = 0,
                DataBase64 = string.Empty,
                TotalLength = 0,
                FinalChunk = true,
                ExpectedSha256 = central.Sha256
            }, cancellationToken);
            if (!empty.Success || empty.HashMismatch)
            {
                throw new IOException(empty.Error ?? "The agent rejected the empty shared file.");
            }
        }

        while (offset < central.SizeBytes)
        {
            var length = (int)Math.Min(ChunkSize, central.SizeBytes - offset);
            var buffer = new byte[length];
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                throw new IOException("The hub shared-file blob ended before the expected length.");
            }

            var isFinal = offset + read >= central.SizeBytes;
            var result = await transport.WriteFileChunkAsync(computer, new FileWriteRequestDto
            {
                Path = temporary,
                Offset = offset,
                DataBase64 = Convert.ToBase64String(buffer.AsSpan(0, read)),
                TotalLength = central.SizeBytes,
                FinalChunk = isFinal,
                ExpectedSha256 = central.Sha256
            }, cancellationToken);

            if (!result.Success || result.HashMismatch)
            {
                throw new IOException(result.Error ?? "The agent rejected the shared-file chunk.");
            }

            offset += read;
        }

        var finalize = await transport.FinalizeUploadAsync(computer, temporary, target, cancellationToken);
        if (!finalize.Success)
        {
            throw new IOException(finalize.Error ?? "The agent could not finalize the shared-file upload.");
        }
    }

    private async Task RenameLocalAsync(
        string root,
        string relativePath,
        string conflictRelativePath,
        CancellationToken cancellationToken)
    {
        if (!PathGuard.TryResolveRelativeWithinRoot(root, relativePath, out var source, out _, out var error) ||
            !PathGuard.TryResolveRelativeWithinRoot(root, conflictRelativePath, out var destination, out _, out error))
        {
            throw new InvalidOperationException(error);
        }

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Move(source, destination, true);
        await Task.CompletedTask;
    }

    private async Task RenameAgentFileAsync(
        Computer computer,
        string root,
        string relativePath,
        string conflictRelativePath,
        CancellationToken cancellationToken)
    {
        if (!PathGuard.TryResolveRelativeWithinRoot(root, relativePath, out var source, out _, out var error) ||
            !PathGuard.TryResolveRelativeWithinRoot(root, conflictRelativePath, out _, out var conflict, out error))
        {
            throw new InvalidOperationException(error);
        }

        var result = await transport.RenameFileAsync(
            computer, source, Path.GetFileName(conflict), true, cancellationToken);
        if (!result.Success)
        {
            throw new IOException(result.Error ?? "The agent could not preserve the conflicting shared file.");
        }
    }

    private static void DeleteLocalFile(string root, string relativePath)
    {
        if (!PathGuard.TryResolveRelativeWithinRoot(root, relativePath, out var full, out _, out var error))
        {
            throw new InvalidOperationException(error);
        }

        if (File.Exists(full))
        {
            File.Delete(full);
        }
    }

    private async Task DeleteAgentFileAsync(
        Computer computer,
        string root,
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (!PathGuard.TryResolveRelativeWithinRoot(root, relativePath, out var full, out _, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var result = await transport.DeleteFileAsync(computer, full, false, true, cancellationToken);
        if (!result.Success &&
            !(result.Error?.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ?? false) &&
            !(result.Error?.Contains("not found", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            throw new IOException(result.Error ?? "The agent could not delete the shared file.");
        }
    }

    private async Task CommitCentralAsync(
        RemoteDeskDbContext db,
        string relativePath,
        SharedFolderManifestEntryDto entry,
        string source,
        CancellationToken cancellationToken)
    {
        var record = await db.SharedFolderFiles.FindAsync([relativePath], cancellationToken);
        if (record is null)
        {
            record = new SharedFolderFile { Path = relativePath, Version = 0 };
            db.SharedFolderFiles.Add(record);
        }

        record.Sha256 = entry.Sha256;
        record.SizeBytes = entry.SizeBytes;
        record.LastWriteUtc = entry.LastWriteUtc.UtcDateTime;
        record.IsDeleted = false;
        record.Version++;
        record.SourceComputerName = source;
        record.UpdatedUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkCentralDeletedAsync(RemoteDeskDbContext db, string relativePath, CancellationToken cancellationToken)
    {
        var record = await db.SharedFolderFiles.FindAsync([relativePath], cancellationToken);
        if (record is null || record.IsDeleted)
        {
            return;
        }

        record.IsDeleted = true;
        record.Version++;
        record.UpdatedUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(
            "SYSTEM",
            AuditOperations.SharedFolderDeleted,
            details: relativePath,
            cancellationToken: cancellationToken);
    }

    private static async Task UpsertLocalRowAsync(
        RemoteDeskDbContext db,
        string relativePath,
        SharedFolderManifestEntryDto entry,
        string centralSha,
        CancellationToken cancellationToken)
    {
        var row = await db.SharedFolderLocalFiles.FindAsync([relativePath], cancellationToken);
        if (row is null)
        {
            row = new SharedFolderLocalFile { Path = relativePath };
            db.SharedFolderLocalFiles.Add(row);
        }

        row.Sha256 = entry.Sha256;
        row.CentralSha256 = centralSha;
        row.SizeBytes = entry.SizeBytes;
        row.LastWriteUtc = entry.LastWriteUtc.UtcDateTime;
        row.ObservedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task RemoveLocalRowAsync(RemoteDeskDbContext db, string relativePath, CancellationToken cancellationToken)
    {
        var row = await db.SharedFolderLocalFiles.FindAsync([relativePath], cancellationToken);
        if (row is not null)
        {
            db.SharedFolderLocalFiles.Remove(row);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task UpsertAgentRowAsync(
        RemoteDeskDbContext db,
        Guid computerId,
        string relativePath,
        SharedFolderManifestEntryDto entry,
        string centralSha,
        CancellationToken cancellationToken)
    {
        var row = await db.SharedFolderAgentFiles.FindAsync([computerId, relativePath], cancellationToken);
        if (row is null)
        {
            row = new SharedFolderAgentFile { ComputerId = computerId, Path = relativePath };
            db.SharedFolderAgentFiles.Add(row);
        }

        row.Sha256 = entry.Sha256;
        row.CentralSha256 = centralSha;
        row.SizeBytes = entry.SizeBytes;
        row.LastWriteUtc = entry.LastWriteUtc.UtcDateTime;
        row.ObservedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task RemoveAgentRowAsync(
        RemoteDeskDbContext db,
        Guid computerId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var row = await db.SharedFolderAgentFiles.FindAsync([computerId, relativePath], cancellationToken);
        if (row is not null)
        {
            db.SharedFolderAgentFiles.Remove(row);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task AuditPeerAsync(
        string peerName,
        Guid? computerId,
        SyncPeerResult result,
        CancellationToken cancellationToken)
    {
        var details = $"Pulled {result.Pulled}, pushed {result.Pushed}, deleted {result.Deleted}, conflicts {result.Conflicts}.";
        if (result.Paths.Count > 0)
        {
            details += " Paths: " + string.Join(", ", result.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(12));
        }

        await audit.LogAsync(
            "SYSTEM",
            AuditOperations.SharedFolderFileSynced,
            peerName,
            computerId,
            result: string.IsNullOrWhiteSpace(result.Error) ? "Success" : "Partial",
            error: result.Error,
            details: details,
            cancellationToken: cancellationToken);
    }

    private async Task TryWriteStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var state = await db.SharedFolderSyncState.FindAsync([1], cancellationToken);
            if (state is null)
            {
                state = new SharedFolderSyncState { Id = 1 };
                db.SharedFolderSyncState.Add(state);
            }

            var active = await db.SharedFolderFiles.AsNoTracking()
                .Where(x => !x.IsDeleted)
                .Select(x => new { x.SizeBytes })
                .ToListAsync(cancellationToken);
            var cutoff = clock.UtcNow.AddMinutes(-5);
            state.LastScanUtc = clock.UtcNow;
            state.LastCompletedUtc = clock.UtcNow;
            state.Files = active.Count;
            state.TotalBytes = active.Sum(x => x.SizeBytes);
            state.Agents = await db.Agents.AsNoTracking()
                .CountAsync(x => x.State == AgentState.Active && x.LastSeenUtc >= cutoff &&
                                 x.MachineName != Environment.MachineName, cancellationToken);
            state.LastError = _lastError;
            state.LastErrorUtc = string.IsNullOrWhiteSpace(_lastError) ? state.LastErrorUtc : clock.UtcNow;
            state.UpdatedUtc = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not persist shared-folder synchronization status.");
        }
    }

    private static CentralSharedFile MapCentral(SharedFolderFile file) =>
        new(file.Path, file.Sha256, file.SizeBytes, file.LastWriteUtc, file.Version, file.IsDeleted, file.SourceComputerName);

    private static AgentSharedFile MapLocal(SharedFolderLocalFile file) =>
        new(file.Path, file.Sha256, file.CentralSha256, file.SizeBytes, file.LastWriteUtc);

    private static AgentSharedFile MapAgent(SharedFolderAgentFile file) =>
        new(file.Path, file.Sha256, file.CentralSha256, file.SizeBytes, file.LastWriteUtc);

    private string BlobPath(string sha256) =>
        Path.Combine(paths.SharedFolderBlobsDirectory, sha256.ToUpperInvariant());

    private void MoveToBlob(string staging, string sha256)
    {
        var blob = BlobPath(sha256);
        if (File.Exists(blob))
        {
            TryDelete(staging);
            return;
        }

        File.Move(staging, blob, true);
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup; staging files are not part of the shared folder and are ignored.
        }
    }

    private static void AppendResult(SyncPeerResult result, string peer, List<string> errors, List<string> paths)
    {
        paths.Add($"{peer}: pulled {result.Pulled}, pushed {result.Pushed}, deleted {result.Deleted}, conflicts {result.Conflicts}");
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            errors.Add($"{peer}: {result.Error}");
        }
    }

    private sealed record SyncPeerResult(
        int Pulled,
        int Pushed,
        int Deleted,
        int Conflicts,
        List<string> Paths,
        string? Error);
}
