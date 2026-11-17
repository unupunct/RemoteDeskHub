namespace RemoteDeskHub.Shared.Protocol;

public sealed record CommandRequestDto
{
    public Guid CorrelationId { get; init; } = Guid.NewGuid();
    public required string Command { get; init; }
    public CommandShell Shell { get; init; } = CommandShell.PowerShell;
    public int TimeoutSeconds { get; init; } = HubConstants.DefaultCommandTimeoutSeconds;
    public bool RequireElevation { get; init; }
    public bool Confirmed { get; init; }
    public string? IssuedBy { get; init; }
    public DateTimeOffset IssuedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresUtc { get; init; } = DateTimeOffset.UtcNow.AddMinutes(2);
}

public sealed record CommandResultDto
{
    public Guid CorrelationId { get; init; }
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; init; }
    public bool TimedOut { get; init; }
    public bool Cancelled { get; init; }
    public string? Error { get; init; }
    public string? ExecutionIdentity { get; init; }
    public long DurationMs { get; init; }
    public bool OutputTruncated { get; init; }
    public Guid? ApprovalId { get; init; }
}

public sealed record FileEntryDto
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset ModifiedUtc { get; init; }
    public required string Attributes { get; init; }
    public string? Extension { get; init; }
}

public sealed record FileListDto
{
    public required string Path { get; init; }
    public string? ParentPath { get; init; }
    public IReadOnlyList<FileEntryDto> Entries { get; init; } = [];
    public bool IsTruncated { get; init; }
}

public sealed record FileStatDto
{
    public required string Path { get; init; }
    public bool Exists { get; init; }
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset? ModifiedUtc { get; init; }
    public bool CanRead { get; init; }
    public bool CanWrite { get; init; }
    public string? Sha256 { get; init; }
}

public sealed record FileChunkDto
{
    public required string Path { get; init; }
    public long Offset { get; init; }
    public required string DataBase64 { get; init; }
    public int Length { get; init; }
    public long TotalLength { get; init; }
}

public sealed record FileWriteRequestDto
{
    public required string Path { get; init; }
    public long Offset { get; init; }
    public required string DataBase64 { get; init; }
    public long TotalLength { get; init; }
    public bool FinalChunk { get; init; }
    public string? ExpectedSha256 { get; init; }
}

public sealed record FileWriteResultDto
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public long BytesWritten { get; init; }
    public long TotalLength { get; init; }
    public string? Sha256 { get; init; }
    public bool HashMismatch { get; init; }
}

public sealed record FileHashDto(string Path, long SizeBytes, string Sha256);

public sealed record FileFinalizeRequestDto(string TemporaryPath, string FinalPath);

public sealed record RemoteAssistanceLaunchInfo
{
    public Guid SessionId { get; init; }
    public RemoteToolKind Tool { get; init; }
    public required string ToolDisplayName { get; init; }
    public required string Target { get; init; }
    public required string Instructions { get; init; }
    public string? Arguments { get; init; }
    public string? ExecutableName { get; init; }
}
