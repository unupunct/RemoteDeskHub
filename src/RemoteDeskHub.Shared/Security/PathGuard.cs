using System.Text.RegularExpressions;

namespace RemoteDeskHub.Shared.Security;

/// <summary>
/// Central file-path validation used by the agent before any file-system operation.
/// Prevents traversal outside configured roots and blocks access to protected Windows locations.
/// </summary>
public static partial class PathGuard
{
    private static readonly string[] BlockedRootPatterns =
    [
        @"\System Volume Information",
        @"\$Recycle.Bin",
        @"\Recovery\",
        @"\Boot\",
        @"\EFI\"
    ];

    [GeneratedRegex(@"^[A-Za-z]:\\", RegexOptions.Compiled)]
    private static partial Regex DriveRootRegex();

    public static bool IsValidAbsolutePath(string? path, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Path is required.";
            return false;
        }

        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Contains('\0'))
        {
            error = "Path contains invalid characters.";
            return false;
        }

        // Reject NTFS alternate data streams (for example C:\file.txt:hidden) and drive-relative
        // forms. Only the drive separator at index 1 is allowed.
        if (path.IndexOf(':', 2) >= 0)
        {
            error = "Alternate data streams and drive-relative paths are not permitted.";
            return false;
        }

        if (!Path.IsPathFullyQualified(path) || !DriveRootRegex().IsMatch(path))
        {
            error = "An absolute local drive path is required.";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            error = $"Path is invalid: {ex.Message}";
            return false;
        }

        foreach (var blocked in BlockedRootPatterns)
        {
            if (full.Contains(blocked, StringComparison.OrdinalIgnoreCase))
            {
                error = "This protected system location cannot be accessed remotely.";
                return false;
            }
        }

        if (full.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
        {
            error = "UNC and network paths are not permitted.";
            return false;
        }

        return true;
    }

    public static bool TryNormalizeWithinRoots(string path, IEnumerable<string> allowedRoots, out string normalized, out string error)
    {
        normalized = string.Empty;
        if (!IsValidAbsolutePath(path, out error))
        {
            return false;
        }

        string full = Path.GetFullPath(path);
        var roots = allowedRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToList();

        if (roots.Count == 0)
        {
            error = "No permitted file roots are configured on the agent.";
            return false;
        }

        bool inside = roots.Any(root => (full + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase));
        if (!inside)
        {
            error = "Path is outside the permitted file roots configured on the agent.";
            return false;
        }

        // Refuse to follow a reparse point (junction/symlink) anywhere in the path. Checking only
        // the final component is insufficient: a junction in an intermediate directory can escape
        // the permitted root while the final path still looks normal.
        if (ContainsReparsePoint(full, out error))
        {
            return false;
        }

        normalized = full;
        return true;
    }

    private static bool ContainsReparsePoint(string fullPath, out string error)
    {
        error = string.Empty;
        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                error = "The path has no local drive root.";
                return true;
            }

            var current = root;
            var remainder = fullPath[root.Length..];
            foreach (var segment in remainder.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                FileSystemInfo info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);
                if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    error = "Reparse points (junctions or symbolic links) are not permitted anywhere in the path.";
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            // Fail closed: if the agent cannot prove that no reparse point is present, do not
            // perform the requested file operation.
            error = $"The path could not be validated: {ex.Message}";
            return true;
        }
    }

    public static bool IsWithinRoot(string path, string root)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return (full + Path.DirectorySeparatorChar).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validates a relative shared-folder path and resolves it under a fixed root. The method rejects
    /// traversal, rooted paths, alternate data streams, reserved device names, reparse-point escapes
    /// and the internal synchronization staging directory.
    /// </summary>
    public static bool TryResolveRelativeWithinRoot(
        string root,
        string? relativePath,
        out string fullPath,
        out string normalizedRelativePath,
        out string error)
    {
        fullPath = string.Empty;
        normalizedRelativePath = string.Empty;

        if (!IsValidAbsolutePath(root, out error))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "A relative shared-folder path is required.";
            return false;
        }

        if (relativePath.Contains('\0') || relativePath.Length > 1000)
        {
            error = "The shared-folder path is invalid or too long.";
            return false;
        }

        var candidate = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (candidate.StartsWith(Path.DirectorySeparatorChar) ||
            Path.IsPathRooted(candidate) ||
            candidate.Contains(':'))
        {
            error = "Only relative paths are permitted in the shared folder.";
            return false;
        }

        var segments = candidate.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            error = "A relative shared-folder path is required.";
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                error = "Path traversal is not permitted in the shared folder.";
                return false;
            }

            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error = "The shared-folder path contains invalid characters.";
                return false;
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                error = "Shared-folder file and folder names cannot end with a space or period.";
                return false;
            }

            if (IsReservedDeviceName(segment))
            {
                error = "Windows reserved device names are not permitted in the shared folder.";
                return false;
            }
        }

        if (string.Equals(segments[0], ".rdh-sync", StringComparison.OrdinalIgnoreCase))
        {
            error = "The internal shared-folder staging directory is reserved.";
            return false;
        }

        normalizedRelativePath = string.Join(Path.DirectorySeparatorChar, segments);
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(Path.GetFullPath(root), normalizedRelativePath));
        }
        catch (Exception ex)
        {
            error = $"The shared-folder path is invalid: {ex.Message}";
            return false;
        }

        if (!IsWithinRoot(fullPath, root))
        {
            error = "The shared-folder path is outside the configured root.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var name = segment;
        var dot = name.IndexOf('.');
        if (dot >= 0)
        {
            name = name[..dot];
        }

        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Length == 4 &&
            (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            name[3] is >= '1' and <= '9')
        {
            return true;
        }

        return false;
    }

    public static string GetParent(string path)
    {
        var parent = Directory.GetParent(path);
        return parent?.FullName ?? path;
    }
}
