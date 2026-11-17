using System.Collections.Concurrent;
using System.Security.Cryptography;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Shared.Security;

/// <summary>
/// Shared recursive scanner used by both the agent and the hub-local shared folder. It only reports
/// relative paths, skips reparse points and the internal staging directory, and never follows paths
/// outside the supplied root.
/// </summary>
public sealed class SharedFolderScanner
{
    public const string StagingDirectoryName = ".rdh-sync";
    private readonly ConcurrentDictionary<string, CachedHash> _hashCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<SharedFolderManifestDto> ScanAsync(
        string root,
        int maxFiles,
        long maxFileBytes,
        CancellationToken cancellationToken = default)
    {
        if (!PathGuard.IsValidAbsolutePath(root, out var rootError))
        {
            return new SharedFolderManifestDto
            {
                Enabled = true,
                Root = root,
                Error = rootError,
                IsTruncated = true
            };
        }

        var fullRoot = Path.GetFullPath(root);
        var entries = new List<SharedFolderManifestEntryDto>();
        var skipped = new List<string>();
        var truncated = false;
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
            foreach (var path in Directory.EnumerateFiles(fullRoot, "*", enumeration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(fullRoot, path);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment.Equals(StagingDirectoryName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!PathGuard.TryResolveRelativeWithinRoot(fullRoot, relative, out _, out var normalized, out _))
                {
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                if (info.Length > maxFileBytes)
                {
                    skipped.Add(normalized);
                    continue;
                }

                if (entries.Count >= maxFiles)
                {
                    truncated = true;
                    break;
                }

                var sha = await ComputeHashAsync(path, info, cancellationToken).ConfigureAwait(false);
                if (sha is null)
                {
                    skipped.Add(normalized);
                    continue;
                }

                entries.Add(new SharedFolderManifestEntryDto
                {
                    Path = normalized,
                    SizeBytes = info.Length,
                    LastWriteUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    Sha256 = sha
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SharedFolderManifestDto
            {
                Enabled = true,
                Root = fullRoot,
                Error = ex.Message,
                IsTruncated = true
            };
        }

        return new SharedFolderManifestDto
        {
            Enabled = true,
            Root = fullRoot,
            ScannedUtc = DateTimeOffset.UtcNow,
            IsTruncated = truncated,
            Entries = entries,
            SkippedPaths = skipped
        };
    }

    public static async Task<string?> ComputeFileHashAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(digest);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<string?> ComputeHashAsync(string path, FileInfo info, CancellationToken cancellationToken)
    {
        var size = info.Length;
        var lastWriteTicks = info.LastWriteTimeUtc.Ticks;
        if (_hashCache.TryGetValue(path, out var cached) &&
            cached.SizeBytes == size &&
            cached.LastWriteTicks == lastWriteTicks)
        {
            return cached.Sha256;
        }

        var hash = await ComputeFileHashAsync(path, cancellationToken).ConfigureAwait(false);
        if (hash is null)
        {
            return null;
        }

        var after = new FileInfo(path);
        if (!after.Exists || after.Length != size || after.LastWriteTimeUtc.Ticks != lastWriteTicks)
        {
            // The file changed while it was being read. Do not publish an inconsistent hash;
            // the next scan retries once the write has completed.
            return null;
        }

        _hashCache[path] = new CachedHash(size, lastWriteTicks, hash);
        return hash;
    }

    private sealed record CachedHash(long SizeBytes, long LastWriteTicks, string Sha256);
}
