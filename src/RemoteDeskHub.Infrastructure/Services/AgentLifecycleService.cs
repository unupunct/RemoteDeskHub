using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class AgentLifecycleService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    IMonitoringService monitoring,
    ISettingsService settings,
    IAppEvents events,
    ReplayGuard replay,
    IClock clock) : IAgentLifecycleService
{
    public async Task<HeartbeatResponseDto> ProcessHeartbeatAsync(HeartbeatRequestDto request, string? certificateThumbprint, string? remoteIp, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(certificateThumbprint))
        {
            throw new UnauthorizedAccessException("No agent certificate was presented.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.Include(a => a.Computer)
            .FirstOrDefaultAsync(a => a.CertificateThumbprint == certificateThumbprint, cancellationToken);
        if (agent is null || agent.Computer is null)
        {
            throw new UnauthorizedAccessException("The presented certificate is not registered to an approved agent.");
        }

        if (agent.Id != request.AgentId || agent.ComputerId != request.ComputerId)
        {
            throw new UnauthorizedAccessException("The agent identity in the heartbeat does not match the certificate.");
        }

        if (agent.State == AgentState.Revoked)
        {
            return new HeartbeatResponseDto
            {
                Approved = false,
                Revoked = true,
                Message = "This agent has been revoked by the administrator.",
                Config = new AgentRuntimeConfigDto { Revoked = true }
            };
        }

        if (!replay.TryAccept($"heartbeat:{agent.Id:N}", request.Nonce, request.TimestampUtc, out var replayError))
        {
            throw new ReplayDetectedException(replayError ?? "The heartbeat was rejected as a replay.");
        }

        var now = clock.UtcNow;
        agent.LastSeenUtc = now;
        agent.Version = request.AgentVersion;
        var machineId = EnrollmentService.NormalizeMachineId(request.MachineId);
        if (!string.IsNullOrWhiteSpace(machineId))
        {
            agent.MachineId = machineId;
        }

        agent.LastKnownIp = string.IsNullOrWhiteSpace(remoteIp) ? agent.LastKnownIp : EnrollmentService.NormalizeIp(remoteIp);
        agent.ListeningPort = request.ListeningPort is > 0 and < 65536 ? request.ListeningPort : agent.ListeningPort;
        if (agent.State == AgentState.PendingApproval)
        {
            agent.HealthStatus = "Awaiting administrator approval";
        }
        else if (agent.State == AgentState.Active)
        {
            agent.HealthStatus = "Healthy";
        }

        var computer = agent.Computer;
        if (agent.State == AgentState.Active)
        {
            computer.LastSeenUtc = now;
            computer.AgentVersion = request.AgentVersion;
            computer.MachineId = agent.MachineId ?? computer.MachineId;
            computer.UpdatedUtc = now;
            if (!string.IsNullOrWhiteSpace(agent.LastKnownIp))
            {
                computer.IpAddress = agent.LastKnownIp;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        if (agent.State == AgentState.Active && request.Metrics is not null)
        {
            await monitoring.IngestMetricsAsync(computer, request.Metrics, cancellationToken);
        }

        var config = await BuildRuntimeConfigAsync(cancellationToken);
        events.RaiseComputerChanged(computer.Id);

        return new HeartbeatResponseDto
        {
            Approved = agent.State == AgentState.Active,
            Revoked = false,
            Message = agent.State == AgentState.Active ? null : "Awaiting administrator approval.",
            Config = config
        };
    }

    public async Task<OperationResult> RecordTelemetryAsync(TelemetryBatchDto batch, string? certificateThumbprint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(certificateThumbprint))
        {
            return OperationResult.Fail("No agent certificate was presented.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.Include(a => a.Computer)
            .FirstOrDefaultAsync(a => a.CertificateThumbprint == certificateThumbprint, cancellationToken);
        if (agent?.Computer is null || agent.State != AgentState.Active || agent.Id != batch.AgentId)
        {
            return OperationResult.Fail("The agent is not approved for telemetry.");
        }

        if (!replay.TryAccept($"telemetry:{agent.Id:N}", batch.Nonce, batch.TimestampUtc, out var replayError))
        {
            return OperationResult.Fail(replayError ?? "The telemetry batch was rejected as a replay.");
        }

        var samples = batch.Samples
            .Where(s => s is not null)
            .OrderBy(s => s.TimestampUtc)
            .TakeLast(500)
            .ToList();
        if (samples.Count == 0)
        {
            return OperationResult.Ok("No samples to record.");
        }

        // Buffered agent samples are replayed oldest-first. The previous implementation kept only
        // the newest sample, which discarded the history an agent queued while offline.
        foreach (var sample in samples)
        {
            await monitoring.IngestMetricsAsync(agent.Computer, sample, cancellationToken);
        }

        return OperationResult.Ok($"{samples.Count} telemetry sample(s) recorded.");
    }

    public async Task<AgentInfoDto?> GetAgentInfoAsync(string? certificateThumbprint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(certificateThumbprint))
        {
            return null;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.CertificateThumbprint == certificateThumbprint, cancellationToken);
        if (agent is null)
        {
            return null;
        }

        var config = await BuildRuntimeConfigAsync(cancellationToken);
        return new AgentInfoDto
        {
            AgentId = agent.Id,
            ComputerId = agent.ComputerId,
            MachineName = agent.MachineName,
            MachineId = agent.MachineId,
            AgentVersion = agent.Version ?? "unknown",
            OperatingSystem = agent.OperatingSystem ?? string.Empty,
            ListeningPort = agent.ListeningPort,
            StartedUtc = agent.ApprovedUtc ?? agent.EnrolledUtc,
            Elevated = true,
            AllowedFileRoots = config.AllowedFileRoots,
            CommandsEnabled = config.AllowRemoteCommands,
            UptimeSeconds = 0
        };
    }

    public async Task<AgentRuntimeConfigDto> BuildRuntimeConfigAsync(CancellationToken cancellationToken = default)
    {
        var heartbeat = await settings.GetAsync(SettingsKeys.HeartbeatSeconds, HubConstants.DefaultHeartbeatSeconds, cancellationToken);
        var metrics = await settings.GetAsync(SettingsKeys.MetricsSeconds, HubConstants.DefaultMetricsSeconds, cancellationToken);
        var allowCommands = await settings.GetAsync(SettingsKeys.AgentAllowCommands, true, cancellationToken);
        var roots = await settings.GetAsync(SettingsKeys.AgentAllowedRoots, @"C:\Users;C:\ProgramData", cancellationToken);
        var sharedFolderEnabled = await settings.GetAsync(SettingsKeys.SharedFolderEnabled, true, cancellationToken);
        var sharedFolderPath = await settings.GetAsync(SettingsKeys.SharedFolderPath, @"C:\zzz", cancellationToken);
        var sharedFolderMaxFileBytes = await settings.GetAsync(SettingsKeys.SharedFolderMaxFileBytes, 268_435_456L, cancellationToken);
        var sharedFolderMaxFiles = await settings.GetAsync(SettingsKeys.SharedFolderMaxFiles, 20_000, cancellationToken);

        var allowedRoots = (roots ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (sharedFolderEnabled &&
            !string.IsNullOrWhiteSpace(sharedFolderPath) &&
            !allowedRoots.Contains(sharedFolderPath, StringComparer.OrdinalIgnoreCase))
        {
            allowedRoots.Add(sharedFolderPath);
        }

        return new AgentRuntimeConfigDto
        {
            HeartbeatSeconds = Math.Clamp(heartbeat, 3, 300),
            MetricsSeconds = Math.Clamp(metrics, 2, 300),
            AllowRemoteCommands = allowCommands,
            AllowedFileRoots = allowedRoots,
            MaxTransferChunkBytes = HubConstants.MaxTransferChunkBytes,
            MaxCommandTimeoutSeconds = HubConstants.MaxCommandTimeoutSeconds,
            SharedFolderEnabled = sharedFolderEnabled,
            SharedFolderPath = string.IsNullOrWhiteSpace(sharedFolderPath) ? @"C:\zzz" : sharedFolderPath,
            SharedFolderMaxFileBytes = Math.Clamp(sharedFolderMaxFileBytes, 1_048_576L, 4_294_967_296L),
            SharedFolderMaxFiles = Math.Clamp(sharedFolderMaxFiles, 100, 200_000)
        };
    }
}
