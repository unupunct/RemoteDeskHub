using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class ServiceManagerService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    IAgentTransport transport,
    ISessionContext session,
    IAuditService audit) : IServiceManagerService
{
    private static readonly HashSet<string> AllowedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "start", "stop", "restart", "pause", "resume", "startup"
    };

    private static readonly HashSet<string> StartupTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Automatic", "AutomaticDelayedStart", "Manual", "Disabled"
    };

    public async Task<IReadOnlyList<RemoteServiceDto>> GetServicesAsync(Guid computerId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewComputers);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computer = await db.Computers.AsNoTracking().Include(c => c.Agent)
            .FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken)
            ?? throw new InvalidOperationException("Computer not found.");
        return await transport.GetServicesAsync(computer, cancellationToken);
    }

    public async Task<OperationResult> ExecuteActionAsync(string actor, Guid computerId, string serviceName, string action, string? startupType, bool confirmed, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageServices, actor);
        action = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (!AllowedActions.Contains(action))
        {
            return OperationResult.Fail($"Unsupported service action '{action}'.");
        }

        if (action == "startup" && (string.IsNullOrWhiteSpace(startupType) || !StartupTypes.Contains(startupType)))
        {
            return OperationResult.Fail("Choose a valid startup type (Automatic, AutomaticDelayedStart, Manual or Disabled).");
        }

        if (CriticalServices.IsCritical(serviceName) && action is "stop" or "restart")
        {
            return OperationResult.Fail(
                $"'{serviceName}' is a critical Windows service and cannot be stopped or restarted through RemoteDesk Hub.");
        }

        if (action is "stop" or "restart" or "pause" && !confirmed)
        {
            return OperationResult.Fail("This service action was not confirmed.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computer = await db.Computers.Include(c => c.Agent).FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken);
        if (computer is null)
        {
            return OperationResult.Fail("Computer not found.");
        }

        try
        {
            var result = await transport.ServiceActionAsync(computer, serviceName, action, startupType, cancellationToken);
            await audit.LogAsync(actor, AuditOperations.ServiceChanged, computer.Name, computer.Id,
                result: result.Success ? "Success" : "Failed",
                error: result.Success ? null : result.Error,
                details: $"Service '{serviceName}': {action}{(startupType is null ? string.Empty : $" -> {startupType}")}.",
                cancellationToken: cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            await audit.LogAsync(actor, AuditOperations.ServiceChanged, computer.Name, computer.Id,
                result: "Failed", error: ex.Message, details: $"Service '{serviceName}': {action}.", cancellationToken: cancellationToken);
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task<IReadOnlyList<RemoteServiceDto>> GetDependenciesAsync(Guid computerId, string serviceName, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewComputers);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computer = await db.Computers.AsNoTracking().Include(c => c.Agent)
            .FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken)
            ?? throw new InvalidOperationException("Computer not found.");
        return await transport.GetServiceDependenciesAsync(computer, serviceName, cancellationToken);
    }
}
