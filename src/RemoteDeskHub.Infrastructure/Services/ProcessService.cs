using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class ProcessService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    IAgentTransport transport,
    ISessionContext session,
    IAuditService audit) : IProcessService
{
    public async Task<IReadOnlyList<RemoteProcessDto>> GetProcessesAsync(Guid computerId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewComputers);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computer = await db.Computers.AsNoTracking().Include(c => c.Agent)
            .FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken);
        if (computer is null)
        {
            throw new InvalidOperationException("Computer not found.");
        }

        return await transport.GetProcessesAsync(computer, cancellationToken);
    }

    public async Task<ProcessTerminateOutcome> TerminateAsync(string actor, Guid computerId, int pid, string processName, bool confirmed, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageProcesses, actor);
        if (ProtectedProcesses.IsProtected(processName))
        {
            await audit.LogAsync(actor, AuditOperations.ProcessTerminated, result: "Blocked",
                error: $"Protected process '{processName}' termination blocked.", cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ProcessTerminateOutcome
            {
                ComputerId = computerId,
                Pid = pid,
                Success = false,
                Message = $"'{processName}' is a protected Windows process and cannot be terminated through RemoteDesk Hub."
            };
        }

        if (!confirmed)
        {
            return new ProcessTerminateOutcome
            {
                ComputerId = computerId,
                Pid = pid,
                Success = false,
                Message = "Process termination was not confirmed."
            };
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computer = await db.Computers.Include(c => c.Agent).FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken);
        if (computer is null)
        {
            return new ProcessTerminateOutcome { ComputerId = computerId, Pid = pid, Success = false, Message = "Computer not found." };
        }

        try
        {
            var result = await transport.TerminateProcessAsync(computer, pid, processName, cancellationToken);
            await audit.LogAsync(actor, AuditOperations.ProcessTerminated, computer.Name, computer.Id,
                result: result.Success ? "Success" : "Failed",
                error: result.Success ? null : result.Error,
                details: $"PID {pid} ({processName}).", cancellationToken: cancellationToken);
            return new ProcessTerminateOutcome
            {
                ComputerId = computerId,
                Pid = pid,
                Success = result.Success,
                Message = result.Success ? $"Process {processName} ({pid}) terminated." : result.Error ?? "Termination failed."
            };
        }
        catch (Exception ex)
        {
            await audit.LogAsync(actor, AuditOperations.ProcessTerminated, computer.Name, computer.Id,
                result: "Failed", error: ex.Message, details: $"PID {pid} ({processName}).", cancellationToken: cancellationToken);
            return new ProcessTerminateOutcome { ComputerId = computerId, Pid = pid, Success = false, Message = ex.Message };
        }
    }

    public async Task<ReportFile> ExportAsync(Guid computerId, string format, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ExportData);
        var processes = await GetProcessesAsync(computerId, cancellationToken);
        var rows = processes.Select(p => new[]
        {
            p.Name, p.Pid.ToString(), p.CpuPercent.ToString("F1"), (p.WorkingSetBytes / 1024 / 1024).ToString(),
            p.UserName ?? string.Empty, p.StartTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty,
            p.ExecutablePath ?? string.Empty, p.Status
        }).ToList();

        return ReportWriter.Create("processes", format, "Process Inventory",
            ["Process", "PID", "CPU %", "Memory (MB)", "User", "Start time", "Executable path", "Status"], rows);
    }
}
