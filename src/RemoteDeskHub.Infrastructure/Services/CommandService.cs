using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class CommandService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    IAgentTransport transport,
    ISessionContext session,
    IAuditService audit,
    ISettingsService settings,
    IClock clock) : ICommandService
{
    public async Task<CommandResultDto> ExecuteAsync(
        string actor,
        Computer computer,
        string command,
        CommandShell shell,
        int timeoutSeconds,
        bool requireElevation,
        bool confirmed,
        bool isBulk,
        CancellationToken cancellationToken = default,
        Guid? approvalId = null)
    {
        AccessGuard.Require(session, Permission.RunCommands, actor);
        var started = clock.UtcNow;
        var correlationId = Guid.NewGuid();
        command = (command ?? string.Empty).Trim();
        if (command.Length == 0)
        {
            return FailedResult(correlationId, "Enter a command to execute.", started);
        }

        if (command.Length > 8000)
        {
            return FailedResult(correlationId, "The command is too long (maximum 8,000 characters).", started);
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, 5, HubConstants.MaxCommandTimeoutSeconds);

        var blocklistEnabled = await settings.GetAsync(
            SettingsKeys.CommandBlocklistEnabled, true, cancellationToken);
        var criticalCommand = CommandSafety.IsCritical(command);
        if (blocklistEnabled && criticalCommand)
        {
            var allowlist = await settings.GetAsync(
                SettingsKeys.CommandAllowlist, string.Empty, cancellationToken);
            if (!CommandSafety.IsAllowlisted(command, allowlist))
            {
                await audit.LogAsync(
                    actor,
                    AuditOperations.CommandBlocked,
                    computer.Name,
                    computer.Id,
                    result: "Blocked",
                    error: "The command matches the hub's critical-command blocklist.",
                    details: $"{shell} command blocked by policy. Add the exact command to security.commandAllowlist to permit it.",
                    cancellationToken: cancellationToken);
                return FailedResult(
                    correlationId,
                    "This command is blocked by the hub command policy. An administrator must add the exact command to the allowlist in Settings > Security before it can run.",
                    started);
            }
        }

        var destructive = CommandSafety.IsPotentiallyDestructive(command);
        if (destructive && !confirmed && approvalId is null)
        {
            return FailedResult(correlationId, "This command appears potentially destructive. It was not executed because it was not explicitly confirmed.", started);
        }

        if (approvalId is { } requestedApprovalId)
        {
            var (approval, approvalError) = await LoadApprovalAsync(
                actor, requestedApprovalId, computer, command, shell, timeoutSeconds, requireElevation, cancellationToken);
            if (approval is null)
            {
                return FailedResult(correlationId, approvalError ?? "The command approval is invalid.", started);
            }

            confirmed = true;
        }
        else
        {
            var approvalRequired = await settings.GetAsync(
                SettingsKeys.CommandApprovalRequired, false, cancellationToken);
            if (approvalRequired && (destructive || criticalCommand))
            {
                var approval = new CommandApproval
                {
                    ComputerId = computer.Id,
                    ComputerName = computer.Name,
                    RequestedBy = actor,
                    Command = command,
                    Shell = shell,
                    TimeoutSeconds = timeoutSeconds,
                    RequireElevation = requireElevation,
                    RequestedUtc = started,
                    ExpiresUtc = started.AddMinutes(15),
                    Status = CommandApprovalStatus.Pending
                };
                await using var approvalDb = await contextFactory.CreateDbContextAsync(cancellationToken);
                approvalDb.CommandApprovals.Add(approval);
                await approvalDb.SaveChangesAsync(cancellationToken);
                await audit.LogAsync(
                    actor,
                    AuditOperations.CommandApprovalRequested,
                    computer.Name,
                    computer.Id,
                    details: $"{shell} command requires approval. Approval {approval.Id}; expires {approval.ExpiresUtc:O}.",
                    cancellationToken: cancellationToken);
                return FailedResult(
                    correlationId,
                    $"This command requires approval from a second administrator. Approval request {approval.Id} was created and expires in 15 minutes.",
                    started,
                    approval.Id);
            }
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var target = await db.Computers.Include(c => c.Agent).FirstOrDefaultAsync(c => c.Id == computer.Id, cancellationToken);
        if (target is null)
        {
            if (approvalId is { } missingApprovalId)
            {
                await using var approvalDb = await contextFactory.CreateDbContextAsync(cancellationToken);
                var approval = await approvalDb.CommandApprovals.FirstOrDefaultAsync(a => a.Id == missingApprovalId, cancellationToken);
                if (approval is not null)
                {
                    approval.Status = CommandApprovalStatus.Failed;
                    approval.ExecutedUtc = clock.UtcNow;
                    await approvalDb.SaveChangesAsync(cancellationToken);
                }
            }

            return FailedResult(correlationId, "Computer not found.", started);
        }

        var history = new CommandHistory
        {
            Id = correlationId,
            ComputerId = target.Id,
            ComputerName = target.Name,
            Actor = actor,
            Shell = shell,
            Command = command,
            StartedUtc = started,
            Status = CommandStatus.Running,
            RequireElevation = requireElevation,
            IsBulk = isBulk
        };
        db.CommandHistory.Add(history);
        await db.SaveChangesAsync(cancellationToken);

        var request = new CommandRequestDto
        {
            CorrelationId = correlationId,
            Command = command,
            Shell = shell,
            TimeoutSeconds = timeoutSeconds,
            RequireElevation = requireElevation,
            Confirmed = confirmed,
            IssuedBy = actor,
            IssuedUtc = started,
            ExpiresUtc = started.AddMinutes(2)
        };

        CommandResultDto result;
        try
        {
            result = await transport.ExecuteCommandAsync(target, request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await transport.CancelCommandAsync(target, correlationId, CancellationToken.None);
            }
            catch
            {
                // Best effort: the request may already have completed.
            }

            result = new CommandResultDto
            {
                CorrelationId = correlationId,
                ExitCode = -1,
                StartedUtc = started,
                CompletedUtc = clock.UtcNow,
                Cancelled = true,
                Error = "Command cancelled.",
                StdErr = "Command cancelled by the administrator."
            };
        }
        catch (Exception ex)
        {
            result = new CommandResultDto
            {
                CorrelationId = correlationId,
                ExitCode = -1,
                StartedUtc = started,
                CompletedUtc = clock.UtcNow,
                Error = ex.Message,
                StdErr = ex.Message
            };
        }

        var completed = clock.UtcNow;
        history.CompletedUtc = completed;
        history.Status = result.Cancelled
            ? CommandStatus.Cancelled
            : result.TimedOut
                ? CommandStatus.TimedOut
                : result.Error is not null && result.ExitCode != 0
                    ? CommandStatus.Failed
                    : result.ExitCode == 0 ? CommandStatus.Succeeded : CommandStatus.Failed;
        history.ExitCode = result.ExitCode;
        history.StdOut = Truncate(result.StdOut, HubConstants.MaxCommandOutputChars);
        history.StdErr = Truncate(result.StdErr, HubConstants.MaxCommandOutputChars);
        history.DurationMs = (long)(completed - started).TotalMilliseconds;
        history.Error = Truncate(result.Error, 4000);
        await db.SaveChangesAsync(cancellationToken);

        await audit.LogAsync(actor, AuditOperations.CommandExecuted, target.Name, target.Id,
            result: history.Status == CommandStatus.Succeeded ? "Success" : "Failed",
            durationMs: history.DurationMs ?? 0,
            error: history.Error ?? (result.ExitCode != 0 ? $"Exit code {result.ExitCode}." : null),
            details: $"{shell} command executed{(isBulk ? " (bulk)" : string.Empty)}. Exit code {result.ExitCode}. Status {history.Status}. Transcript SHA-256 stdout {HashOutput(history.StdOut)} ({history.StdOut?.Length ?? 0} chars), stderr {HashOutput(history.StdErr)} ({history.StdErr?.Length ?? 0} chars).",
            cancellationToken: cancellationToken);

        if (approvalId is { } usedApprovalId)
        {
            await using var approvalDb = await contextFactory.CreateDbContextAsync(cancellationToken);
            var approval = await approvalDb.CommandApprovals.FirstOrDefaultAsync(a => a.Id == usedApprovalId, cancellationToken);
            if (approval is not null)
            {
                approval.Status = history.Status == CommandStatus.Succeeded
                    ? CommandApprovalStatus.Executed
                    : CommandApprovalStatus.Failed;
                approval.ExecutedUtc = completed;
                await approvalDb.SaveChangesAsync(cancellationToken);
                await audit.LogAsync(
                    actor,
                    AuditOperations.CommandApprovalExecuted,
                    target.Name,
                    target.Id,
                    result: approval.Status == CommandApprovalStatus.Executed ? "Success" : "Failed",
                    details: $"Approval {approval.Id} executed with exit code {result.ExitCode}.",
                    cancellationToken: cancellationToken);
            }
        }

        return result;
    }

    public async Task<OperationResult> CancelAsync(string actor, Guid computerId, Guid correlationId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.RunCommands, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computer = await db.Computers.Include(c => c.Agent).FirstOrDefaultAsync(c => c.Id == computerId, cancellationToken);
        if (computer is null)
        {
            return OperationResult.Fail("Computer not found.");
        }

        try
        {
            var result = await transport.CancelCommandAsync(computer, correlationId, cancellationToken);
            if (result.Success)
            {
                await audit.LogAsync(actor, "Remote.CommandCancelled", computer.Name, computer.Id,
                    details: $"Correlation {correlationId}.", cancellationToken: cancellationToken);
            }

            return result;
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task<IReadOnlyList<CommandHistoryView>> GetHistoryAsync(Guid? computerId = null, int limit = 200, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.RunCommands);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.CommandHistory.AsNoTracking().AsQueryable();
        if (computerId.HasValue)
        {
            query = query.Where(h => h.ComputerId == computerId);
        }

        return await query.OrderByDescending(h => h.StartedUtc).Take(Math.Clamp(limit, 1, 2000))
            .Select(h => new CommandHistoryView
            {
                Id = h.Id,
                ComputerName = h.ComputerName,
                Actor = h.Actor,
                Shell = h.Shell,
                Command = h.Command,
                StartedUtc = h.StartedUtc,
                CompletedUtc = h.CompletedUtc,
                Status = h.Status,
                ExitCode = h.ExitCode,
                StdOut = h.StdOut,
                StdErr = h.StdErr,
                DurationMs = h.DurationMs,
                Error = h.Error
            }).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CommandApprovalView>> GetApprovalsAsync(
        bool pendingOnly = true,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.RunCommands);
        var actor = session.CurrentUser?.UserName ?? HubConstants.AuditActorSystem;
        var canApprove = session.CurrentUser?.Can(Permission.ApproveCommands) == true;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.UtcNow;

        if (pendingOnly)
        {
            var expired = await db.CommandApprovals
                .Where(a => (a.Status == CommandApprovalStatus.Pending || a.Status == CommandApprovalStatus.Approved)
                    && a.ExpiresUtc < now)
                .ToListAsync(cancellationToken);
            if (expired.Count > 0)
            {
                foreach (var approval in expired)
                {
                    approval.Status = CommandApprovalStatus.Expired;
                }

                await db.SaveChangesAsync(cancellationToken);
            }
        }

        var query = db.CommandApprovals.AsNoTracking().AsQueryable();
        if (!canApprove)
        {
            query = query.Where(a => a.RequestedBy == actor);
        }

        if (pendingOnly)
        {
            query = query.Where(a =>
                a.Status == CommandApprovalStatus.Pending || a.Status == CommandApprovalStatus.Approved);
        }

        return await query
            .OrderByDescending(a => a.RequestedUtc)
            .Take(Math.Clamp(limit, 1, 1000))
            .Select(a => new CommandApprovalView
            {
                Id = a.Id,
                ComputerId = a.ComputerId,
                ComputerName = a.ComputerName,
                RequestedBy = a.RequestedBy,
                Command = a.Command,
                Shell = a.Shell,
                TimeoutSeconds = a.TimeoutSeconds,
                RequireElevation = a.RequireElevation,
                Status = a.Status,
                RequestedUtc = a.RequestedUtc,
                ExpiresUtc = a.ExpiresUtc,
                ReviewedBy = a.ReviewedBy,
                ReviewedUtc = a.ReviewedUtc,
                ReviewNote = a.ReviewNote,
                ExecutedUtc = a.ExecutedUtc
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<OperationResult> ApproveCommandAsync(
        string actor,
        Guid approvalId,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ApproveCommands, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var approval = await db.CommandApprovals.FirstOrDefaultAsync(a => a.Id == approvalId, cancellationToken);
        if (approval is null)
        {
            return OperationResult.Fail("The command approval was not found.");
        }

        if (string.Equals(approval.RequestedBy, actor, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail("A different administrator must approve this command.");
        }

        if (approval.Status != CommandApprovalStatus.Pending)
        {
            return OperationResult.Fail($"This command approval is already {approval.Status}.");
        }

        if (approval.ExpiresUtc < clock.UtcNow)
        {
            approval.Status = CommandApprovalStatus.Expired;
            await db.SaveChangesAsync(cancellationToken);
            return OperationResult.Fail("This command approval has expired.");
        }

        approval.Status = CommandApprovalStatus.Approved;
        approval.ReviewedBy = actor;
        approval.ReviewedUtc = clock.UtcNow;
        approval.ReviewNote = Truncate(note, 2000);
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(
            actor,
            AuditOperations.CommandApprovalApproved,
            approval.ComputerName,
            approval.ComputerId,
            details: $"Approval {approval.Id} for {approval.RequestedBy} approved. {approval.Shell} command: {approval.Command}",
            cancellationToken: cancellationToken);
        return OperationResult.Ok("The command approval was approved.");
    }

    public async Task<OperationResult> RejectCommandAsync(
        string actor,
        Guid approvalId,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ApproveCommands, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var approval = await db.CommandApprovals.FirstOrDefaultAsync(a => a.Id == approvalId, cancellationToken);
        if (approval is null)
        {
            return OperationResult.Fail("The command approval was not found.");
        }

        if (approval.Status != CommandApprovalStatus.Pending)
        {
            return OperationResult.Fail($"This command approval is already {approval.Status}.");
        }

        approval.Status = CommandApprovalStatus.Rejected;
        approval.ReviewedBy = actor;
        approval.ReviewedUtc = clock.UtcNow;
        approval.ReviewNote = Truncate(note, 2000);
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(
            actor,
            AuditOperations.CommandApprovalRejected,
            approval.ComputerName,
            approval.ComputerId,
            result: "Rejected",
            details: $"Approval {approval.Id} for {approval.RequestedBy} rejected. {approval.ReviewNote}",
            cancellationToken: cancellationToken);
        return OperationResult.Ok("The command approval was rejected.");
    }

    public async Task<CommandResultDto> ExecuteApprovedAsync(
        string actor,
        Guid approvalId,
        CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.RunCommands, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var approval = await db.CommandApprovals.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == approvalId, cancellationToken);
        if (approval is null)
        {
            return FailedResult(Guid.NewGuid(), "The command approval was not found.", clock.UtcNow);
        }

        var computer = new Computer
        {
            Id = approval.ComputerId ?? Guid.Empty,
            Name = approval.ComputerName
        };
        return await ExecuteAsync(
            actor,
            computer,
            approval.Command,
            approval.Shell,
            approval.TimeoutSeconds,
            approval.RequireElevation,
            confirmed: true,
            isBulk: false,
            cancellationToken,
            approval.Id);
    }

    private async Task<(CommandApproval? Approval, string? Error)> LoadApprovalAsync(
        string actor,
        Guid approvalId,
        Computer computer,
        string command,
        CommandShell shell,
        int timeoutSeconds,
        bool requireElevation,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var approval = await db.CommandApprovals.FirstOrDefaultAsync(a => a.Id == approvalId, cancellationToken);
        if (approval is null)
        {
            return (null, "The command approval was not found.");
        }

        if (!string.Equals(approval.RequestedBy, actor, StringComparison.OrdinalIgnoreCase))
        {
            return (null, "Only the administrator who requested this command can execute it.");
        }

        if (approval.ComputerId != computer.Id ||
            !string.Equals(approval.Command, command, StringComparison.Ordinal) ||
            approval.Shell != shell ||
            approval.TimeoutSeconds != timeoutSeconds ||
            approval.RequireElevation != requireElevation)
        {
            return (null, "The command approval does not match this command, computer, shell, timeout or elevation setting.");
        }

        if (approval.Status != CommandApprovalStatus.Approved)
        {
            return (null, $"This command approval is {approval.Status}; it must be approved before execution.");
        }

        if (approval.ExpiresUtc < clock.UtcNow)
        {
            approval.Status = CommandApprovalStatus.Expired;
            await db.SaveChangesAsync(cancellationToken);
            return (null, "This command approval has expired.");
        }

        // Claim the approval atomically so two console processes cannot execute the same approval.
        var claimed = await db.CommandApprovals
            .Where(a => a.Id == approvalId && a.Status == CommandApprovalStatus.Approved)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(a => a.Status, CommandApprovalStatus.Executing),
                cancellationToken);
        if (claimed != 1)
        {
            return (null, "This command approval is already being executed or has been used.");
        }

        return (approval, null);
    }

    public async Task<ReportFile> ExportHistoryAsync(string format, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ExportData);
        var history = await GetHistoryAsync(null, 5000, cancellationToken);
        var rows = history.Select(h => new[]
        {
            h.ComputerName, h.Actor, h.Shell.ToString(), h.Command, h.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            h.Status.ToString(), h.ExitCode?.ToString() ?? string.Empty, h.DurationMs?.ToString() ?? string.Empty, h.Error ?? string.Empty
        }).ToList();

        return ReportWriter.Create("command-history", format, "Remote Command History",
            ["Computer", "Actor", "Shell", "Command", "Started", "Status", "Exit code", "Duration (ms)", "Error"], rows);
    }

    private static CommandResultDto FailedResult(
        Guid correlationId,
        string error,
        DateTime started,
        Guid? approvalId = null) => new()
    {
        CorrelationId = correlationId,
        ExitCode = -1,
        Error = error,
        StdErr = error,
        StartedUtc = started,
        CompletedUtc = DateTime.UtcNow,
        ApprovalId = approvalId
    };

    private static string HashOutput(string? value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max] + "\n[output truncated]";
}

public static class CommandSafety
{
    private static readonly string[] DestructivePatterns =
    [
        "remove-item", "rmdir", " rd ", "rd /s", "del ", "erase ", "format-volume", "format c:",
        "clear-disk", "initialize-disk", "remove-partition", "shutdown", "stop-computer", "restart-computer",
        "sc delete", "reg delete", "net user", "net localgroup", "takeown", "icacls", "cipher /w",
        "disable-", "uninstall-", "remove-service", "stop-service", "taskkill", "kill ", "bcdedit",
        "set-executionpolicy", "invoke-expression", "iex ", "remove-ad", "dism /", "wmic"
    ];

    // These operations can destroy the boot volume, the recovery environment, the local account
    // database or the event log. They are blocked unless an administrator has explicitly listed
    // the exact command in security.commandAllowlist.
    private static readonly string[] CriticalPatterns =
    [
        "format ", "format-volume", "format c:", "clear-disk", "initialize-disk", "remove-partition",
        "diskpart", "bcdedit", "vssadmin delete", "wevtutil cl", "cipher /w", "cipher.exe /w",
        "sc delete", "reg delete hklm", "reg delete hkcu\\software\\microsoft\\windows\\currentversion\\run",
        "remove-item -recurse c:\\", "del /f /s /q c:\\", "rd /s /q c:\\",
        "takeown /f c:\\windows", "icacls c:\\windows",
        "net user administrator /active:no", "net localgroup administrators"
    ];

    public static bool IsPotentiallyDestructive(string command)
    {
        var normalized = Normalize(command);
        return DestructivePatterns.Any(normalized.Contains);
    }

    public static bool IsCritical(string command)
    {
        var normalized = Normalize(command);
        return CriticalPatterns.Any(normalized.Contains);
    }

    public static bool IsAllowlisted(string command, string? allowlist)
    {
        if (string.IsNullOrWhiteSpace(allowlist))
        {
            return false;
        }

        var normalized = Normalize(command);
        var entries = allowlist.Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return entries.Any(entry =>
            !entry.StartsWith('#') &&
            string.Equals(Normalize(entry), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static bool RequiresElevatedIdentity(string command)
    {
        var normalized = Normalize(command);
        return new[] { "service", "install", "uninstall", "firewall", "netsh", "driver", "registry", "machinepolicy" }
            .Any(normalized.Contains);
    }

    private static string Normalize(string? command)
    {
        var collapsed = string.Join(' ', (command ?? string.Empty)
            .Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return " " + collapsed.ToLowerInvariant() + " ";
    }
}
