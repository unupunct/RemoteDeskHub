using System.Security.Cryptography;
using RemoteDeskHub.Shared.Protocol;
using RemoteDeskHub.Shared.Security;

namespace RemoteDeskHub.Agent.Files;

/// <summary>
/// File-system API used by the hub. Every path is validated against the administrator-configured
/// permitted roots before any read, write, rename or delete occurs.
/// </summary>
public sealed class AgentFileService(AgentRuntimeState state, ILogger<AgentFileService> logger)
{
    private const int MaxEntries = 5000;

    public FileListDto List(string path)
    {
        var normalized = Validate(path);
        var info = new DirectoryInfo(normalized);
        if (!info.Exists)
        {
            throw new DirectoryNotFoundException($"The folder '{normalized}' does not exist on the agent computer.");
        }

        var entries = new List<FileEntryDto>();
        var truncated = false;
        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if (entries.Count >= MaxEntries)
            {
                truncated = true;
                break;
            }

            try
            {
                var isDirectory = entry is DirectoryInfo;
                entries.Add(new FileEntryDto
                {
                    Name = entry.Name,
                    FullPath = entry.FullName,
                    IsDirectory = isDirectory,
                    SizeBytes = isDirectory ? 0 : ((FileInfo)entry).Length,
                    ModifiedUtc = entry.LastWriteTimeUtc,
                    Attributes = entry.Attributes.ToString(),
                    Extension = isDirectory ? null : entry.Extension
                });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Skip entries that cannot be inspected.
            }
        }

        var parent = PathGuard.GetParent(normalized);
        var root = state.AllowedRoots.FirstOrDefault(r => PathGuard.IsWithinRoot(normalized, r));
        if (root is not null && string.Equals(Path.GetFullPath(root).TrimEnd('\\'), normalized.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            parent = null;
        }

        return new FileListDto
        {
            Path = normalized,
            ParentPath = parent,
            Entries = entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            IsTruncated = truncated
        };
    }

    public FileStatDto Stat(string path, bool includeHash)
    {
        var normalized = Validate(path);
        var file = new FileInfo(normalized);
        var directory = new DirectoryInfo(normalized);
        if (directory.Exists)
        {
            return new FileStatDto
            {
                Path = normalized,
                Exists = true,
                IsDirectory = true,
                ModifiedUtc = directory.LastWriteTimeUtc,
                CanRead = true,
                CanWrite = CanWrite(normalized)
            };
        }

        if (!file.Exists)
        {
            return new FileStatDto { Path = normalized, Exists = false };
        }

        return new FileStatDto
        {
            Path = normalized,
            Exists = true,
            IsDirectory = false,
            SizeBytes = file.Length,
            ModifiedUtc = file.LastWriteTimeUtc,
            CanRead = true,
            CanWrite = CanWrite(normalized),
            Sha256 = includeHash ? ComputeHash(normalized) : null
        };
    }

    public FileChunkDto ReadChunk(string path, long offset, int length)
    {
        var normalized = Validate(path);
        var file = new FileInfo(normalized);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"The file '{normalized}' does not exist on the agent computer.");
        }

        if (offset < 0 || offset > file.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The offset is outside the file.");
        }

        var toRead = (int)Math.Min(Math.Clamp(length, 1, state.MaxTransferChunkBytes), file.Length - offset);
        var buffer = new byte[toRead];
        using (var stream = new FileStream(normalized, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024,
                   FileOptions.SequentialScan))
        {
            stream.Position = offset;
            var read = 0;
            while (read < toRead)
            {
                var chunk = stream.Read(buffer, read, toRead - read);
                if (chunk <= 0)
                {
                    break;
                }

                read += chunk;
            }

            if (read != toRead)
            {
                Array.Resize(ref buffer, read);
            }
        }

        return new FileChunkDto
        {
            Path = normalized,
            Offset = offset,
            DataBase64 = Convert.ToBase64String(buffer),
            Length = buffer.Length,
            TotalLength = file.Length
        };
    }

    public FileWriteResultDto WriteChunk(FileWriteRequestDto request)
    {
        var normalized = Validate(request.Path);
        var bytes = string.IsNullOrEmpty(request.DataBase64) ? [] : Convert.FromBase64String(request.DataBase64);
        if (bytes.Length > state.MaxTransferChunkBytes)
        {
            return new FileWriteResultDto { Success = false, Error = "The chunk exceeds the maximum permitted size." };
        }

        var directory = Path.GetDirectoryName(normalized);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (var stream = new FileStream(normalized, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 64 * 1024,
                   FileOptions.SequentialScan))
        {
            if (request.Offset == 0)
            {
                stream.SetLength(0);
            }
            else if (request.Offset > stream.Length)
            {
                return new FileWriteResultDto
                {
                    Success = false,
                    Error = $"The chunk offset {request.Offset} is beyond the current file length {stream.Length}."
                };
            }

            stream.Position = request.Offset;
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        var length = new FileInfo(normalized).Length;
        string? hash = null;
        var mismatch = false;
        if (request.FinalChunk)
        {
            hash = ComputeHash(normalized);
            mismatch = !string.IsNullOrWhiteSpace(request.ExpectedSha256) &&
                       !string.Equals(hash, request.ExpectedSha256, StringComparison.OrdinalIgnoreCase);
            if (mismatch)
            {
                logger.LogWarning("Upload integrity check failed for {Path}.", normalized);
                return new FileWriteResultDto
                {
                    Success = false,
                    Error = "Integrity verification failed: the SHA-256 hash does not match the uploaded content.",
                    BytesWritten = bytes.Length,
                    TotalLength = length,
                    Sha256 = hash,
                    HashMismatch = true
                };
            }
        }

        return new FileWriteResultDto
        {
            Success = true,
            BytesWritten = bytes.Length,
            TotalLength = length,
            Sha256 = hash,
            HashMismatch = false
        };
    }

    public void CreateDirectory(string path)
    {
        var normalized = Validate(path);
        Directory.CreateDirectory(normalized);
    }

    public void Rename(string path, string newName, bool confirmed)
    {
        if (!confirmed)
        {
            throw new InvalidOperationException("The rename operation was not confirmed.");
        }

        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            newName.Contains('\\') || newName.Contains('/') || newName is "." or "..")
        {
            throw new ArgumentException("Enter a valid file name without path separators.", nameof(newName));
        }

        var normalized = Validate(path);
        var parent = Path.GetDirectoryName(normalized)
                     ?? throw new InvalidOperationException("The item has no parent folder.");
        var target = Path.Combine(parent, newName);
        target = Validate(target);

        if (File.Exists(normalized))
        {
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new IOException("A file or folder with that name already exists.");
            }

            File.Move(normalized, target);
            return;
        }

        if (Directory.Exists(normalized))
        {
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new IOException("A file or folder with that name already exists.");
            }

            Directory.Move(normalized, target);
            return;
        }

        throw new FileNotFoundException($"The item '{normalized}' does not exist on the agent computer.");
    }

    public void Delete(string path, bool recursive, bool confirmed)
    {
        if (!confirmed)
        {
            throw new InvalidOperationException("The delete operation was not confirmed.");
        }

        var normalized = Validate(path);
        EnsureNotRoot(normalized);

        if (Directory.Exists(normalized))
        {
            if (!recursive && Directory.EnumerateFileSystemEntries(normalized).Any())
            {
                throw new IOException("The folder is not empty. Confirm recursive deletion to remove it.");
            }

            Directory.Delete(normalized, recursive);
            return;
        }

        if (File.Exists(normalized))
        {
            File.Delete(normalized);
            return;
        }

        throw new FileNotFoundException($"The item '{normalized}' does not exist on the agent computer.");
    }

    public FileHashDto Hash(string path)
    {
        var normalized = Validate(path);
        var file = new FileInfo(normalized);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"The file '{normalized}' does not exist on the agent computer.");
        }

        return new FileHashDto(normalized, file.Length, ComputeHash(normalized));
    }

    public void FinalizeUpload(string temporaryPath, string finalPath)
    {
        var temp = Validate(temporaryPath);
        var final = Validate(finalPath);
        if (!File.Exists(temp))
        {
            throw new FileNotFoundException("The temporary upload file no longer exists.");
        }

        if (Directory.Exists(final))
        {
            throw new IOException("The destination path is a folder.");
        }

        var directory = Path.GetDirectoryName(final);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Move(temp, final, true);
    }

    private string Validate(string path)
    {
        if (!PathGuard.TryNormalizeWithinRoots(path, state.AllowedRoots, out var normalized, out var error))
        {
            throw new UnauthorizedAccessException(error);
        }

        return normalized;
    }

    private void EnsureNotRoot(string normalized)
    {
        foreach (var root in state.AllowedRoots)
        {
            if (string.Equals(Path.GetFullPath(root).TrimEnd('\\'), normalized.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("A permitted file root cannot itself be deleted or renamed.");
            }
        }
    }

    private static bool CanWrite(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var probe = Path.Combine(path, $".rdh-probe-{Guid.NewGuid():N}");
                using (File.Create(probe, 1, FileOptions.DeleteOnClose))
                {
                }

                return true;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return stream.CanWrite;
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
