namespace RemoteDeskHub.Shared.Protocol;

// ---------------------------------------------------------------------------
// Enrollment protocol
// ---------------------------------------------------------------------------

public sealed record EnrollmentRequestDto
{
    public required string Token { get; init; }
    public required string MachineName { get; init; }
    public required string AgentVersion { get; init; }
    public required string OperatingSystem { get; init; }
    public required string PublicKeyPem { get; init; }
    public required string CsrPem { get; init; }
    public required string Nonce { get; init; }
    public Guid InstallationId { get; init; }
    /// <summary>Stable machine identity (Windows MachineGuid); never an IP address.</summary>
    public string? MachineId { get; init; }
    public int ManagementPort { get; init; }
    public DateTimeOffset RequestedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record EnrollmentResponseDto(Guid EnrollmentId, string Status, string Message, int PollIntervalSeconds = 5);

public sealed record EnrollmentStatusDto
{
    public required string Status { get; init; }
    public required string Message { get; init; }
    public string? AgentCertificatePem { get; init; }
    public string? CaCertificatePem { get; init; }
    public string? HubCertificatePem { get; init; }
    public string? HubServerCertificateThumbprint { get; init; }
    public string? HubClientCertificatePem { get; init; }
    public Guid? AgentId { get; init; }
    public Guid? ComputerId { get; init; }
    public int ManagementPort { get; init; }
}

/// <summary>Authenticated agent request to replace its expiring client certificate.</summary>
public sealed record AgentCertificateRenewalRequestDto
{
    public Guid AgentId { get; init; }
    public required string MachineName { get; init; }
    public required string CsrPem { get; init; }
    public required string Nonce { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? AgentVersion { get; init; }
}

public sealed record AgentCertificateRenewalResponseDto
{
    public required string AgentCertificatePem { get; init; }
    public required string CaCertificatePem { get; init; }
    public required string HubServerCertificateThumbprint { get; init; }
    public string? HubClientCertificateThumbprint { get; init; }
    public DateTimeOffset RenewedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? Message { get; init; }
}

/// <summary>
/// Portable enrollment package (.rdhenroll) exported by the administrator and imported by the agent installer.
/// It contains a short-lived, single-use token and the public CA certificate only - never a private key.
/// </summary>
public sealed record PairingPackageDto
{
    public int FormatVersion { get; init; } = 2;
    public required string HubUrl { get; init; }
    public required string EnrollmentToken { get; init; }
    public required string CaCertificatePem { get; init; }

    /// <summary>Thumbprint of the hub console client certificate used to authenticate hub-to-agent calls.</summary>
    public required string HubCertificateThumbprint { get; init; }

    /// <summary>
    /// Thumbprint of the hub TLS server certificate. Agents pin this value so a different
    /// CA-issued certificate (for example another agent certificate) cannot impersonate the hub.
    /// </summary>
    public string? HubServerCertificateThumbprint { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
    public string? Note { get; init; }
    public int AgentPort { get; init; } = HubConstants.DefaultAgentPort;
}

// ---------------------------------------------------------------------------
// Heartbeat / configuration
// ---------------------------------------------------------------------------

public sealed record HeartbeatRequestDto
{
    public Guid AgentId { get; init; }
    public Guid ComputerId { get; init; }
    public required string MachineName { get; init; }
    public required string AgentVersion { get; init; }
    public string? MachineId { get; init; }
    public int ListeningPort { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string Nonce { get; init; }
    public AgentMetricsDto? Metrics { get; init; }
}

public sealed record HeartbeatResponseDto
{
    public bool Approved { get; init; }
    public bool Revoked { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset ServerTimeUtc { get; init; } = DateTimeOffset.UtcNow;
    public AgentRuntimeConfigDto Config { get; init; } = new();
}

public sealed record AgentRuntimeConfigDto
{
    public int HeartbeatSeconds { get; init; } = HubConstants.DefaultHeartbeatSeconds;
    public int MetricsSeconds { get; init; } = HubConstants.DefaultMetricsSeconds;
    public bool AllowRemoteCommands { get; init; } = true;
    public IReadOnlyList<string> AllowedFileRoots { get; init; } = [];
    public int MaxTransferChunkBytes { get; init; } = HubConstants.MaxTransferChunkBytes;
    public int MaxCommandTimeoutSeconds { get; init; } = HubConstants.MaxCommandTimeoutSeconds;
    public bool SharedFolderEnabled { get; init; } = true;
    public string SharedFolderPath { get; init; } = @"C:\zzz";
    public long SharedFolderMaxFileBytes { get; init; } = 268_435_456;
    public int SharedFolderMaxFiles { get; init; } = 20_000;
    public bool Revoked { get; init; }
    public string? UpdateUrl { get; init; }
}

public sealed record TelemetryBatchDto
{
    public Guid AgentId { get; init; }
    public required IReadOnlyList<AgentMetricsDto> Samples { get; init; }
    public required string Nonce { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record AgentInfoDto
{
    public Guid AgentId { get; init; }
    public Guid ComputerId { get; init; }
    public required string MachineName { get; init; }
    public string? MachineId { get; init; }
    public required string AgentVersion { get; init; }
    public required string OperatingSystem { get; init; }
    public int ListeningPort { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public bool Elevated { get; init; }
    public IReadOnlyList<string> AllowedFileRoots { get; init; } = [];
    public bool CommandsEnabled { get; init; }
    public long UptimeSeconds { get; init; }
}

public sealed record ApiErrorDto(string Code, string Message, string? Detail = null);
