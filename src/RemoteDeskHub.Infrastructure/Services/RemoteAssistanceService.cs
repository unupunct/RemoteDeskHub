using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class RemoteAssistanceService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    ISessionContext session,
    IAuditService audit,
    ISettingsService settings,
    IClock clock) : IRemoteAssistanceService
{
    public async Task<IReadOnlyList<RemoteAssistanceTarget>> GetTargetsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computers = await db.Computers.AsNoTracking()
            .Where(c => c.IsRegistered || c.IsManaged)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);

        return computers.Select(c => new RemoteAssistanceTarget
        {
            ComputerId = c.Id,
            ComputerName = c.Name,
            IpAddress = c.IpAddress,
            OperatingSystem = c.OperatingSystem,
            TeamViewerId = c.TeamViewerId,
            AnyDeskId = c.AnyDeskId,
            LmViewerId = c.LmViewerId,
            RustDeskId = c.RustDeskId,
            Online = c.LastSeenUtc.HasValue && clock.UtcNow - c.LastSeenUtc.Value < TimeSpan.FromSeconds(45)
        }).ToList();
    }

    public async Task<OperationResult<RemoteAssistanceLaunchInfo>> PrepareLaunchAsync(
        string actor,
        Guid computerId,
        RemoteToolKind tool,
        string? connectionTarget,
        bool consentConfirmed,
        CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.LaunchRemoteAssistance, actor);

        var consentRequired = await settings.GetAsync(SettingsKeys.RemoteConsentRequired, false, cancellationToken);
        if (consentRequired && !consentConfirmed)
        {
            return OperationResult<RemoteAssistanceLaunchInfo>.Fail(
                "Explicit consent from the person at the target computer is required before a remote session can be prepared.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computer = await db.Computers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken);
        if (computer is null)
        {
            return OperationResult<RemoteAssistanceLaunchInfo>.Fail("Computer not found.");
        }

        var definition = RemoteToolCatalog.Get(tool);
        if ((tool is RemoteToolKind.Rdp or RemoteToolKind.WindowsRemoteAssistance) &&
            string.IsNullOrWhiteSpace(computer.IpAddress))
        {
            return OperationResult<RemoteAssistanceLaunchInfo>.Fail(
                "This computer has no recorded IP address. Refresh discovery or wait for an agent heartbeat.");
        }

        var target = RemoteToolCatalog.ResolveTarget(tool, computer.IpAddress, connectionTarget);
        if (definition.RequiresConnectionTarget && string.IsNullOrWhiteSpace(connectionTarget))
        {
            return OperationResult<RemoteAssistanceLaunchInfo>.Fail(
                $"{definition.DisplayName} requires a connection ID or address.");
        }

        if (tool != RemoteToolKind.QuickAssist && string.IsNullOrWhiteSpace(target))
        {
            return OperationResult<RemoteAssistanceLaunchInfo>.Fail(
                "A connection target is required for this tool.");
        }

        if (target is not null && !RemoteToolCatalog.IsValidTarget(target))
        {
            return OperationResult<RemoteAssistanceLaunchInfo>.Fail(
                "The connection target contains invalid characters or is too long.");
        }

        var arguments = RemoteToolCatalog.BuildArguments(tool, target, null);
        var displayTarget = target ?? "this computer";
        var remoteSession = new RemoteSession
        {
            ComputerId = computer.Id,
            ComputerName = computer.Name,
            Actor = actor,
            Tool = tool,
            ConnectionInfo = target,
            StartedUtc = clock.UtcNow,
            Status = RemoteSessionStatus.Prepared,
            Notes = consentRequired
                ? "Prepared with explicit operator consent. Waiting for the operator-side tool to start."
                : "Prepared under the trusted-network policy (consent confirmation disabled). Waiting for the operator-side tool to start."
        };
        db.RemoteSessions.Add(remoteSession);
        await db.SaveChangesAsync(cancellationToken);

        var consentDetail = consentRequired
            ? "Explicit target-consent confirmation recorded by the operator."
            : "Consent confirmation disabled by trusted-network policy; launch recorded without a prompt.";
        await audit.LogAsync(
            actor,
            AuditOperations.RemoteSessionPrepared,
            computer.Name,
            computer.Id,
            details: $"{definition.DisplayName} targeting {displayTarget}. {consentDetail}",
            cancellationToken: cancellationToken);

        return OperationResult<RemoteAssistanceLaunchInfo>.Ok(new RemoteAssistanceLaunchInfo
        {
            SessionId = remoteSession.Id,
            Tool = tool,
            ToolDisplayName = definition.DisplayName,
            Target = displayTarget,
            Instructions = definition.Instructions,
            Arguments = arguments,
            ExecutableName = definition.ExecutableName
        });
    }

    public async Task<OperationResult> CompleteLaunchAsync(
        string actor,
        Guid sessionId,
        bool launched,
        string? details,
        int? processId = null,
        CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.LaunchRemoteAssistance, actor);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var remoteSession = await db.RemoteSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (remoteSession is null)
        {
            return OperationResult.Fail("Remote session not found.");
        }

        if (remoteSession.Status is RemoteSessionStatus.Ended or RemoteSessionStatus.Failed)
        {
            return OperationResult.Fail("This remote session is already closed.");
        }

        var sanitizedDetails = string.IsNullOrWhiteSpace(details) ? null : details.Trim();
        if (sanitizedDetails?.Length > 2000)
        {
            sanitizedDetails = sanitizedDetails[..2000];
        }

        if (launched)
        {
            remoteSession.Status = RemoteSessionStatus.Started;
            remoteSession.StartedUtc = clock.UtcNow;
            remoteSession.ProcessId = processId is > 0 ? processId : remoteSession.ProcessId;
            remoteSession.Notes = sanitizedDetails is null
                ? "Launched from RemoteDesk Hub."
                : $"Launched from RemoteDesk Hub. {sanitizedDetails}";
            await db.SaveChangesAsync(cancellationToken);

            await audit.LogAsync(
                actor,
                AuditOperations.RemoteSessionLaunched,
                remoteSession.ComputerName,
                remoteSession.ComputerId,
                details: sanitizedDetails ?? "Operator-side tool started.",
                cancellationToken: cancellationToken);

            return OperationResult.Ok("Remote session launched.");
        }

        remoteSession.Status = RemoteSessionStatus.Failed;
        remoteSession.EndedUtc = clock.UtcNow;
        remoteSession.Notes = sanitizedDetails is null
            ? "Operator-side tool could not be started."
            : $"Operator-side tool could not be started. {sanitizedDetails}";
        await db.SaveChangesAsync(cancellationToken);

        await audit.LogAsync(
            actor,
            AuditOperations.RemoteSessionLaunchFailed,
            remoteSession.ComputerName,
            remoteSession.ComputerId,
            result: "Failed",
            error: sanitizedDetails,
            details: sanitizedDetails,
            cancellationToken: cancellationToken);

        return OperationResult.Ok("Remote session launch failure recorded.");
    }

    public async Task<OperationResult> MarkSessionEndedAsync(
        string actor,
        Guid sessionId,
        string? details = null,
        CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.LaunchRemoteAssistance, actor);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var remoteSession = await db.RemoteSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (remoteSession is null)
        {
            return OperationResult.Fail("Remote session not found.");
        }

        if (remoteSession.Status == RemoteSessionStatus.Ended)
        {
            return OperationResult.Ok("Session is already marked as ended.");
        }

        remoteSession.Status = RemoteSessionStatus.Ended;
        remoteSession.EndedUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var duration = remoteSession.DurationText;
        var reason = string.IsNullOrWhiteSpace(details) ? "marked ended by the operator" : details.Trim();
        await audit.LogAsync(
            actor,
            AuditOperations.RemoteSessionEnded,
            remoteSession.ComputerName,
            remoteSession.ComputerId,
            details: $"{remoteSession.Tool} session ended ({reason}); duration {duration}.",
            cancellationToken: cancellationToken);

        return OperationResult.Ok("Session marked as ended.");
    }

    public async Task<IReadOnlyList<RemoteSession>> GetSessionHistoryAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.RemoteSessions.AsNoTracking().OrderByDescending(s => s.StartedUtc)
            .Take(Math.Clamp(limit, 1, 1000)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RemoteAssistanceProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.RemoteAssistanceProfiles.AsNoTracking().OrderBy(p => p.Name).ToListAsync(cancellationToken);
    }

    public async Task<OperationResult> SaveProfileAsync(string actor, RemoteAssistanceProfile profile, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageSettings, actor);
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            return OperationResult.Fail("Profile name is required.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        RemoteAssistanceProfile entity;
        if (profile.Id == Guid.Empty)
        {
            entity = new RemoteAssistanceProfile { CreatedUtc = clock.UtcNow };
            db.RemoteAssistanceProfiles.Add(entity);
        }
        else
        {
            var existing = await db.RemoteAssistanceProfiles.FirstOrDefaultAsync(p => p.Id == profile.Id, cancellationToken);
            if (existing is null)
            {
                return OperationResult.Fail("Profile not found.");
            }

            entity = existing;
        }

        entity.Name = profile.Name.Trim();
        entity.Tool = profile.Tool;
        entity.TargetComputerId = profile.TargetComputerId;
        entity.Arguments = profile.Arguments;
        entity.Instructions = profile.Instructions;
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, "RemoteAssistance.ProfileSaved", details: entity.Name, cancellationToken: cancellationToken);
        return OperationResult.Ok("Remote assistance profile saved.");
    }

    public async Task<OperationResult> DeleteProfileAsync(string actor, Guid profileId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageSettings, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var profile = await db.RemoteAssistanceProfiles.FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);
        if (profile is null)
        {
            return OperationResult.Fail("Profile not found.");
        }

        db.RemoteAssistanceProfiles.Remove(profile);
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, "RemoteAssistance.ProfileDeleted", details: profile.Name, cancellationToken: cancellationToken);
        return OperationResult.Ok("Profile deleted.");
    }

    public async Task<OperationResult> SaveRemoteAccessIdsAsync(
        string actor,
        Guid computerId,
        string? teamViewerId,
        string? anyDeskId,
        string? lmViewerId,
        string? rustDeskId,
        CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageComputers, actor);

        if (!TryNormalizeId(teamViewerId, out var teamViewer, out var error) ||
            !TryNormalizeId(anyDeskId, out var anyDesk, out error) ||
            !TryNormalizeId(lmViewerId, out var lmViewer, out error) ||
            !TryNormalizeId(rustDeskId, out var rustDesk, out error))
        {
            return OperationResult.Fail(error ?? "A remote-access ID is invalid.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computer = await db.Computers.FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken);
        if (computer is null)
        {
            return OperationResult.Fail("Computer not found.");
        }

        computer.TeamViewerId = teamViewer;
        computer.AnyDeskId = anyDesk;
        computer.LmViewerId = lmViewer;
        computer.RustDeskId = rustDesk;
        computer.UpdatedUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var saved = new[]
        {
            teamViewer is null ? null : "TeamViewer",
            anyDesk is null ? null : "AnyDesk",
            lmViewer is null ? null : "LM Viewer",
            rustDesk is null ? null : "RustDesk"
        }.Where(x => x is not null).ToArray();

        await audit.LogAsync(
            actor,
            AuditOperations.RemoteAccessIdsUpdated,
            computer.Name,
            computer.Id,
            details: saved.Length == 0
                ? "All saved remote-access IDs were cleared."
                : $"Saved remote-access IDs for {string.Join(", ", saved)}. No passwords were stored.",
            cancellationToken: cancellationToken);

        return OperationResult.Ok("Remote-access IDs saved.");
    }

    private static bool TryNormalizeId(string? value, out string? normalized, out string? error)
    {
        error = null;
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var candidate = value.Trim();
        if (candidate.Length > 128 || !RemoteToolCatalog.IsValidTarget(candidate) || candidate.Contains(':'))
        {
            error = "Remote-access IDs must be 1-128 printable characters and cannot contain quotes, control characters or a colon.";
            return false;
        }

        normalized = candidate;
        return true;
    }
}
