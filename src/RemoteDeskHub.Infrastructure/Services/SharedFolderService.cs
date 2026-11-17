using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Security;

namespace RemoteDeskHub.Infrastructure.Services;

/// <summary>Read-only projections used by the desktop console's Shared folder tab.</summary>
public sealed class SharedFolderService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    ISettingsService settings,
    ISessionContext session,
    IClock clock,
    IAuditService audit,
    SharedFolderRootManager rootManager,
    ILogger<SharedFolderService> logger) : ISharedFolderService
{
    public async Task<SharedFolderStatusView> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.TransferFiles);
        var enabled = await settings.GetAsync(SettingsKeys.SharedFolderEnabled, true, cancellationToken);
        var root = await settings.GetAsync(SettingsKeys.SharedFolderPath, @"C:\zzz", cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var state = await db.SharedFolderSyncState.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == 1, cancellationToken);
        var active = await db.SharedFolderFiles.AsNoTracking()
            .Where(x => !x.IsDeleted)
            .Select(x => new { x.SizeBytes, x.Path })
            .ToListAsync(cancellationToken);
        var agentCutoff = clock.UtcNow.AddMinutes(-5);
        var localMachine = Environment.MachineName;
        var agentCount = await db.Agents.AsNoTracking()
            .CountAsync(x => x.State == AgentState.Active && x.LastSeenUtc >= agentCutoff &&
                             x.MachineName != localMachine, cancellationToken);

        return new SharedFolderStatusView
        {
            Enabled = enabled,
            RootPath = string.IsNullOrWhiteSpace(root) ? @"C:\zzz" : root,
            LastScanUtc = state?.LastScanUtc,
            LastCompletedUtc = state?.LastCompletedUtc,
            LastError = state?.LastError,
            LastErrorUtc = state?.LastErrorUtc,
            FileCount = active.Count,
            TotalBytes = active.Sum(x => x.SizeBytes),
            AgentCount = agentCount,
            ConflictCopyCount = active.Count(x => x.Path.Contains(" (conflict from ", StringComparison.OrdinalIgnoreCase))
        };
    }

    public async Task<OperationResult> AddLocalFilesAsync(
        string actor,
        IReadOnlyList<string> sourceFiles,
        CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.TransferFiles, actor);
        if (sourceFiles.Count == 0)
        {
            return OperationResult.Fail("Select at least one file to add to the shared folder.");
        }

        var (root, maxFileBytes, maxFiles) = await GetLimitsAsync(cancellationToken);
        if (!rootManager.TryEnsureRoot(root, out root, out var rootError))
        {
            return OperationResult.Fail(rootError ?? "The shared folder is not available.");
        }

        var copied = 0;
        var skipped = new List<string>();
        foreach (var source in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(source))
            {
                skipped.Add($"{Path.GetFileName(source)} (not found)");
                continue;
            }

            var info = new FileInfo(source);
            if (info.Length > maxFileBytes)
            {
                skipped.Add($"{info.Name} (larger than {maxFileBytes:N0} bytes)");
                continue;
            }

            if (copied >= maxFiles)
            {
                skipped.Add($"{info.Name} (file limit reached)");
                continue;
            }

            var destination = UniquePath(Path.Combine(root, info.Name));
            try
            {
                File.Copy(source, destination, false);
                copied++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not add {Source} to the shared folder.", source);
                skipped.Add($"{info.Name} ({ex.Message})");
            }
        }

        await audit.LogAsync(
            actor,
            AuditOperations.SharedFolderFileSynced,
            "Hub local folder",
            result: skipped.Count == 0 ? "Success" : "Partial",
            error: skipped.Count == 0 ? null : string.Join("; ", skipped.Take(4)),
            details: $"Added {copied} file(s) to {root}.",
            cancellationToken: cancellationToken);

        return copied > 0
            ? OperationResult.Ok($"{copied} file(s) added to {root}. Synchronization starts within 15 seconds.")
            : OperationResult.Fail(skipped.Count > 0 ? string.Join("; ", skipped.Take(4)) : "No files were added.");
    }

    public async Task<OperationResult> AddLocalFolderAsync(
        string actor,
        string sourceFolder,
        CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.TransferFiles, actor);
        if (!Directory.Exists(sourceFolder))
        {
            return OperationResult.Fail("Select an existing folder to add to the shared folder.");
        }

        var (root, maxFileBytes, maxFiles) = await GetLimitsAsync(cancellationToken);
        if (!rootManager.TryEnsureRoot(root, out root, out var rootError))
        {
            return OperationResult.Fail(rootError ?? "The shared folder is not available.");
        }

        if (PathGuard.IsWithinRoot(sourceFolder, root) || PathGuard.IsWithinRoot(root, sourceFolder))
        {
            return OperationResult.Fail("The selected folder overlaps the shared folder. Choose a folder outside it.");
        }

        var sourceName = new DirectoryInfo(sourceFolder).Name;
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return OperationResult.Fail("The selected folder has no usable name.");
        }

        var destinationRoot = UniquePath(Path.Combine(root, sourceName));
        Directory.CreateDirectory(destinationRoot);
        var copied = 0;
        var skipped = new List<string>();
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchType = MatchType.Simple
        };

        try
        {
            foreach (var source in Directory.EnumerateFiles(sourceFolder, "*", enumeration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (copied >= maxFiles)
                {
                    skipped.Add("file limit reached");
                    break;
                }

                var relative = Path.GetRelativePath(sourceFolder, source);
                var destination = Path.Combine(destinationRoot, relative);
                if (!PathGuard.IsWithinRoot(destination, destinationRoot))
                {
                    skipped.Add($"{relative} (invalid path)");
                    continue;
                }

                var info = new FileInfo(source);
                if (info.Length > maxFileBytes)
                {
                    skipped.Add($"{relative} (too large)");
                    continue;
                }

                try
                {
                    var directory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.Copy(source, UniquePath(destination), false);
                    copied++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not add {Source} to the shared folder.", source);
                    skipped.Add($"{relative} ({ex.Message})");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not copy folder {Source} to the shared folder.", sourceFolder);
            skipped.Add(ex.Message);
        }

        await audit.LogAsync(
            actor,
            AuditOperations.SharedFolderFileSynced,
            "Hub local folder",
            result: skipped.Count == 0 ? "Success" : "Partial",
            error: skipped.Count == 0 ? null : string.Join("; ", skipped.Take(4)),
            details: $"Added folder {destinationRoot} with {copied} file(s).",
            cancellationToken: cancellationToken);

        return copied > 0
            ? OperationResult.Ok($"{copied} file(s) from '{sourceName}' added to {root}. Synchronization starts within 15 seconds.")
            : OperationResult.Fail(skipped.Count > 0 ? string.Join("; ", skipped.Take(4)) : "The folder contained no files to add.");
    }

    private async Task<(string Root, long MaxFileBytes, int MaxFiles)> GetLimitsAsync(CancellationToken cancellationToken)
    {
        var root = await settings.GetAsync(SettingsKeys.SharedFolderPath, @"C:\zzz", cancellationToken);
        var maxFileBytes = await settings.GetAsync(SettingsKeys.SharedFolderMaxFileBytes, 268_435_456L, cancellationToken);
        var maxFiles = await settings.GetAsync(SettingsKeys.SharedFolderMaxFiles, 20_000, cancellationToken);
        return (string.IsNullOrWhiteSpace(root) ? @"C:\zzz" : root,
            Math.Clamp(maxFileBytes, 1_048_576L, 4_294_967_296L),
            Math.Clamp(maxFiles, 100, 200_000));
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name} ({Guid.NewGuid():N}){extension}");
    }

    public async Task<IReadOnlyList<SharedFolderFileView>> GetFilesAsync(int limit = 500, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.TransferFiles);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.SharedFolderFiles.AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Path)
            .Take(Math.Clamp(limit, 1, 5000))
            .Select(x => new SharedFolderFileView
            {
                Path = x.Path,
                Sha256 = x.Sha256,
                SizeBytes = x.SizeBytes,
                Version = x.Version,
                SourceComputerName = x.SourceComputerName,
                UpdatedUtc = x.UpdatedUtc
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SharedFolderAgentView>> GetAgentsAsync(CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.TransferFiles);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cutoff = clock.UtcNow.AddMinutes(-5);
        var localMachine = Environment.MachineName;
        var agents = await db.Agents.AsNoTracking()
            .Where(x => x.State == AgentState.Active && x.MachineName != localMachine)
            .Select(x => new
            {
                x.ComputerId,
                x.MachineName,
                x.LastSeenUtc,
                ComputerName = x.Computer != null ? x.Computer.Name : x.MachineName
            })
            .ToListAsync(cancellationToken);
        var counts = await db.SharedFolderAgentFiles.AsNoTracking()
            .GroupBy(x => x.ComputerId)
            .Select(g => new { ComputerId = g.Key, Count = g.Count(), LastObserved = g.Max(x => x.ObservedUtc) })
            .ToListAsync(cancellationToken);
        var byComputer = counts.ToDictionary(x => x.ComputerId);

        return agents
            .Select(agent =>
            {
                byComputer.TryGetValue(agent.ComputerId, out var count);
                return new SharedFolderAgentView
                {
                    ComputerId = agent.ComputerId,
                    ComputerName = agent.ComputerName,
                    FileCount = count?.Count ?? 0,
                    LastObservedUtc = count?.LastObserved,
                    Online = agent.LastSeenUtc >= cutoff
                };
            })
            .OrderBy(x => x.ComputerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
