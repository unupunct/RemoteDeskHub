using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Infrastructure.Services;

public enum SharedFolderAction
{
    NoChange = 0,
    PullFromAgent = 1,
    PushToAgent = 2,
    ConflictPushToAgent = 3,
    DeleteOnAgent = 4,
    PreserveConflictAndDelete = 5,
    MarkDeleted = 6,
    RemoveAgentRow = 7
}

public sealed record CentralSharedFile(
    string Path,
    string Sha256,
    long SizeBytes,
    DateTime LastWriteUtc,
    long Version,
    bool IsDeleted,
    string? SourceComputerName);

public sealed record AgentSharedFile(
    string Path,
    string Sha256,
    string? CentralSha256,
    long SizeBytes,
    DateTime LastWriteUtc);

public sealed record SharedFolderPlannedOperation(
    SharedFolderAction Action,
    string Path,
    string? ConflictPath = null,
    SharedFolderManifestEntryDto? Entry = null,
    CentralSharedFile? Central = null,
    AgentSharedFile? Previous = null);

public sealed record SharedFolderPlan(IReadOnlyList<SharedFolderPlannedOperation> Operations);

/// <summary>
/// Pure reconciliation rules for the shared folder. The hub is the coordinator: a local edit is
/// accepted when the central copy has not changed since that agent last saw it. Simultaneous edits
/// keep the central version and preserve the agent's version as a conflict copy. Deletions are
/// tombstones and lose to a central edit the agent had not yet observed.
/// </summary>
public static class SharedFolderPlanner
{
    public static SharedFolderPlan Plan(
        IReadOnlyCollection<CentralSharedFile> centralFiles,
        IReadOnlyCollection<AgentSharedFile> agentFiles,
        IReadOnlyCollection<SharedFolderManifestEntryDto> manifest,
        string computerName,
        DateTimeOffset now,
        IReadOnlyCollection<string>? skippedPaths = null)
    {
        var central = centralFiles
            .Select(file => file with { Path = NormalizePath(file.Path) })
            .ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var previous = agentFiles
            .Select(file => file with { Path = NormalizePath(file.Path) })
            .ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var current = manifest
            .Select(entry => entry with { Path = NormalizePath(entry.Path) })
            .ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var skipped = new HashSet<string>(
            (skippedPaths ?? []).Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);
        var occupied = new HashSet<string>(central.Keys, StringComparer.OrdinalIgnoreCase);
        occupied.UnionWith(previous.Keys);
        occupied.UnionWith(current.Keys);

        var operations = new List<SharedFolderPlannedOperation>();
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in current.Values)
        {
            handled.Add(entry.Path);
            previous.TryGetValue(entry.Path, out var previousFile);
            central.TryGetValue(entry.Path, out var centralFile);

            if (centralFile is null)
            {
                operations.Add(new SharedFolderPlannedOperation(
                    SharedFolderAction.PullFromAgent, entry.Path, Entry: entry, Previous: previousFile));
                continue;
            }

            if (centralFile.IsDeleted)
            {
                var localChanged = previousFile is null ||
                                   !string.Equals(previousFile.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase);
                if (localChanged)
                {
                    // The file was created or edited after the central deletion, or this peer had
                    // never observed the deleted version. Preserve the local bytes instead of
                    // silently deleting a file that may be the only copy.
                    operations.Add(new SharedFolderPlannedOperation(
                        SharedFolderAction.PreserveConflictAndDelete,
                        entry.Path,
                        BuildConflictPath(entry.Path, computerName, now, occupied),
                        Entry: entry,
                        Central: centralFile,
                        Previous: previousFile));
                }
                else
                {
                    operations.Add(new SharedFolderPlannedOperation(
                        SharedFolderAction.DeleteOnAgent,
                        entry.Path,
                        Entry: entry,
                        Central: centralFile,
                        Previous: previousFile));
                }

                continue;
            }

            if (string.Equals(entry.Sha256, centralFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                operations.Add(new SharedFolderPlannedOperation(
                    SharedFolderAction.NoChange, entry.Path, Entry: entry, Central: centralFile, Previous: previousFile));
                continue;
            }

            var localEdit = previousFile is null ||
                            !string.Equals(previousFile.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase);
            var centralEdit = previousFile is null ||
                              !string.Equals(previousFile.CentralSha256, centralFile.Sha256, StringComparison.OrdinalIgnoreCase);

            if (!localEdit)
            {
                operations.Add(new SharedFolderPlannedOperation(
                    SharedFolderAction.PushToAgent, entry.Path, Entry: entry, Central: centralFile, Previous: previousFile));
            }
            else if (!centralEdit)
            {
                operations.Add(new SharedFolderPlannedOperation(
                    SharedFolderAction.PullFromAgent, entry.Path, Entry: entry, Central: centralFile, Previous: previousFile));
            }
            else
            {
                operations.Add(new SharedFolderPlannedOperation(
                    SharedFolderAction.ConflictPushToAgent,
                    entry.Path,
                    BuildConflictPath(entry.Path, computerName, now, occupied),
                    Entry: entry,
                    Central: centralFile,
                    Previous: previousFile));
            }
        }

        // A path that was present in the last observed agent snapshot but is now absent is a local
        // deletion. It only wins when the central file has not changed since that agent last saw it.
        foreach (var previousFile in previous.Values)
        {
            if (handled.Contains(previousFile.Path) || skipped.Contains(previousFile.Path))
            {
                continue;
            }

            handled.Add(previousFile.Path);
            central.TryGetValue(previousFile.Path, out var centralFile);
            if (centralFile is null || centralFile.IsDeleted)
            {
                operations.Add(new SharedFolderPlannedOperation(
                    SharedFolderAction.RemoveAgentRow, previousFile.Path, Previous: previousFile, Central: centralFile));
                continue;
            }

            if (string.Equals(previousFile.CentralSha256, centralFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                operations.Add(new SharedFolderPlannedOperation(
                    SharedFolderAction.MarkDeleted, previousFile.Path, Previous: previousFile, Central: centralFile));
            }
            else
            {
                operations.Add(new SharedFolderPlannedOperation(
                    SharedFolderAction.PushToAgent, previousFile.Path, Previous: previousFile, Central: centralFile));
            }
        }

        // Central files that this agent has never observed are downloaded. This is the first-sync
        // path and also covers files created on another machine while this agent was offline.
        foreach (var centralFile in central.Values)
        {
            if (centralFile.IsDeleted || handled.Contains(centralFile.Path) || skipped.Contains(centralFile.Path))
            {
                continue;
            }

            operations.Add(new SharedFolderPlannedOperation(
                SharedFolderAction.PushToAgent, centralFile.Path, Central: centralFile));
        }

        return new SharedFolderPlan(operations);
    }

    public static string BuildConflictPath(
        string relativePath,
        string computerName,
        DateTimeOffset timestamp,
        ISet<string> occupied)
    {
        var directory = Path.GetDirectoryName(relativePath);
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var extension = Path.GetExtension(relativePath);
        var safeComputer = SanitizeComputerName(computerName);
        if (name.Length > 140)
        {
            name = name[..140];
        }

        var suffix = $" (conflict from {safeComputer} {timestamp:yyyyMMdd-HHmmss})";
        for (var attempt = 1; attempt <= 999; attempt++)
        {
            var candidateName = attempt == 1 ? suffix : $"{suffix}-{attempt}";
            var candidate = string.IsNullOrEmpty(directory)
                ? name + candidateName + extension
                : Path.Combine(directory, name + candidateName + extension);
            if (!occupied.Contains(candidate))
            {
                occupied.Add(candidate);
                return candidate;
            }
        }

        var fallback = string.IsNullOrEmpty(directory)
            ? $"{name} (conflict {Guid.NewGuid():N}){extension}"
            : Path.Combine(directory, $"{name} (conflict {Guid.NewGuid():N}){extension}");
        occupied.Add(fallback);
        return fallback;
    }

    private static string NormalizePath(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar);

    private static string SanitizeComputerName(string computerName)
    {
        if (string.IsNullOrWhiteSpace(computerName))
        {
            return "unknown";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = computerName
            .Where(c => !invalid.Contains(c) && c != Path.DirectorySeparatorChar && c != Path.AltDirectorySeparatorChar)
            .Take(40)
            .ToArray();
        var clean = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "unknown" : clean;
    }
}
