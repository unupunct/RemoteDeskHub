namespace RemoteDeskHub.Shared.Protocol;

/// <summary>
/// A single file discovered under the shared folder on an agent. Paths are always relative to the
/// configured shared-folder root and use backslashes; absolute paths are never sent over the wire.
/// </summary>
public sealed record SharedFolderManifestEntryDto
{
    public required string Path { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset LastWriteUtc { get; init; }
    public required string Sha256 { get; init; }
}

/// <summary>Complete snapshot of an agent's shared folder, produced by the agent's pinned API.</summary>
public sealed record SharedFolderManifestDto
{
    public bool Enabled { get; init; }
    public string? Root { get; init; }
    public DateTimeOffset ScannedUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool IsTruncated { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<SharedFolderManifestEntryDto> Entries { get; init; } = [];
    /// <summary>Paths that exist but could not be read or exceed the size limit this scan.</summary>
    public IReadOnlyList<string> SkippedPaths { get; init; } = [];
}
