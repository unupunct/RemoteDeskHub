using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class AutomationService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    IAgentTransport transport,
    IInventoryService inventory,
    ICommandService commands,
    IReportService reports,
    IAppPaths paths,
    ISessionContext session,
    IAppEvents events,
    IAuditService audit,
    IClock clock) : IAutomationService
{
    private static readonly SemaphoreSlim RunLock = new(1, 1);
    private static readonly SemaphoreSlim TickLock = new(1, 1);

    public async Task<IReadOnlyList<AutomationTaskView>> GetTasksAsync(CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageAutomation);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var tasks = await db.AutomationTasks.AsNoTracking().OrderBy(t => t.Name).ToListAsync(cancellationToken);
        var computers = await db.Computers.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var groups = await db.ComputerGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        return tasks.Select(t => new AutomationTaskView
        {
            Id = t.Id,
            Name = t.Name,
            Description = t.Description,
            Enabled = t.Enabled,
            TriggerType = t.TriggerType,
            ActionType = t.ActionType,
            TargetName = t.TargetComputerId.HasValue
                ? computers.GetValueOrDefault(t.TargetComputerId.Value)
                : t.TargetGroupId.HasValue ? $"Group: {groups.GetValueOrDefault(t.TargetGroupId.Value)}" : "All managed computers",
            IntervalMinutes = t.IntervalMinutes,
            DailyTime = t.DailyTime,
            LastRunUtc = t.LastRunUtc,
            NextRunUtc = t.NextRunUtc,
            LastResult = t.LastResult
        }).ToList();
    }

    public async Task<OperationResult> SaveTaskAsync(string actor, AutomationTask task, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageAutomation, actor);
        var validation = Validate(task);
        if (!validation.Success)
        {
            return validation;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (task.TargetComputerId.HasValue && !await db.Computers.AnyAsync(c => c.Id == task.TargetComputerId, cancellationToken))
        {
            return OperationResult.Fail("The selected target computer does not exist.");
        }

        if (task.TargetGroupId.HasValue && !await db.ComputerGroups.AnyAsync(g => g.Id == task.TargetGroupId, cancellationToken))
        {
            return OperationResult.Fail("The selected target group does not exist.");
        }

        AutomationTask entity;
        var isNew = task.Id == Guid.Empty;
        if (isNew)
        {
            entity = new AutomationTask { CreatedUtc = clock.UtcNow, CreatedBy = actor };
            db.AutomationTasks.Add(entity);
        }
        else
        {
            var existing = await db.AutomationTasks.FirstOrDefaultAsync(t => t.Id == task.Id, cancellationToken);
            if (existing is null)
            {
                return OperationResult.Fail("Automation task not found.");
            }

            entity = existing;
        }

        entity.Name = task.Name.Trim();
        entity.Description = string.IsNullOrWhiteSpace(task.Description) ? null : task.Description.Trim();
        entity.Enabled = task.Enabled;
        entity.TriggerType = task.TriggerType;
        entity.TriggerConfigJson = task.TriggerConfigJson;
        entity.ActionType = task.ActionType;
        entity.ActionConfigJson = task.ActionConfigJson;
        entity.TargetComputerId = task.TargetComputerId;
        entity.TargetGroupId = task.TargetGroupId;
        entity.IntervalMinutes = task.IntervalMinutes;
        entity.DailyTime = task.DailyTime;
        entity.NextRunUtc = entity.Enabled ? ComputeNextRun(entity, clock.UtcNow) : null;
        await db.SaveChangesAsync(cancellationToken);

        await audit.LogAsync(actor, isNew ? AuditOperations.AutomationCreated : AuditOperations.AutomationUpdated,
            details: $"Automation '{entity.Name}' ({entity.TriggerType} -> {entity.ActionType}).", cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok(isNew ? "Automation task created." : "Automation task updated.");
    }

    public async Task<OperationResult> DeleteTaskAsync(string actor, Guid taskId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageAutomation, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.AutomationTasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task is null)
        {
            return OperationResult.Fail("Automation task not found.");
        }

        db.AutomationTasks.Remove(task);
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, AuditOperations.AutomationDeleted, details: task.Name, cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok("Automation task deleted.");
    }

    public async Task<OperationResult> SetEnabledAsync(string actor, Guid taskId, bool enabled, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageAutomation, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.AutomationTasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task is null)
        {
            return OperationResult.Fail("Automation task not found.");
        }

        task.Enabled = enabled;
        task.NextRunUtc = enabled ? ComputeNextRun(task, clock.UtcNow) : null;
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, AuditOperations.AutomationUpdated, details: $"'{task.Name}' enabled={enabled}.", cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok(enabled ? "Automation enabled." : "Automation disabled.");
    }

    public async Task<AutomationExecutionView> RunNowAsync(string actor, Guid taskId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageAutomation, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.AutomationTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new InvalidOperationException("Automation task not found.");
        return await ExecuteTaskAsync(task, actor, cancellationToken);
    }

    public async Task<IReadOnlyList<AutomationExecutionView>> GetExecutionsAsync(Guid? taskId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageAutomation);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.AutomationExecutions.AsNoTracking().Include(e => e.Task).AsQueryable();
        if (taskId.HasValue)
        {
            query = query.Where(e => e.TaskId == taskId);
        }

        return await query.OrderByDescending(e => e.StartedUtc).Take(Math.Clamp(limit, 1, 1000))
            .Select(e => new AutomationExecutionView
            {
                Id = e.Id,
                TaskId = e.TaskId,
                TaskName = e.Task != null ? e.Task.Name : string.Empty,
                StartedUtc = e.StartedUtc,
                CompletedUtc = e.CompletedUtc,
                Status = e.Status,
                Succeeded = e.Succeeded,
                Failed = e.Failed,
                Log = e.Log,
                TriggeredBy = e.TriggeredBy
            }).ToListAsync(cancellationToken);
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        if (!await TickLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var tasks = await db.AutomationTasks.Where(t => t.Enabled).ToListAsync(cancellationToken);
            var now = clock.UtcNow;

            foreach (var task in tasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var due = task.NextRunUtc.HasValue && task.NextRunUtc.Value <= now;
                if (due && task.TriggerType is AutomationTriggerType.Interval or AutomationTriggerType.Daily)
                {
                    await ExecuteTaskAsync(task, HubConstants.AuditActorSystem, cancellationToken);
                    continue;
                }

                if (task.TriggerType is AutomationTriggerType.ComputerOffline or AutomationTriggerType.ServiceStopped or AutomationTriggerType.DiskSpaceLow)
                {
                    var lastRun = task.LastRunUtc ?? DateTime.MinValue;
                    var cooldown = task.TriggerType == AutomationTriggerType.ComputerOffline ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(30);
                    if (now - lastRun < cooldown)
                    {
                        continue;
                    }

                    var triggered = await EvaluateEventTriggerAsync(task, cancellationToken);
                    if (triggered)
                    {
                        await ExecuteTaskAsync(task, HubConstants.AuditActorSystem, cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            await audit.LogAsync(HubConstants.AuditActorSystem, "Automation.SchedulerError", result: "Failed", error: ex.Message, cancellationToken: CancellationToken.None);
        }
        finally
        {
            TickLock.Release();
        }
    }

    private async Task<bool> EvaluateEventTriggerAsync(AutomationTask task, CancellationToken cancellationToken)
    {
        var targets = await LoadTargetsAsync(task, cancellationToken);
        if (targets.Count == 0)
        {
            return false;
        }

        switch (task.TriggerType)
        {
            case AutomationTriggerType.ComputerOffline:
                return targets.Any(t => t.LastSeenUtc is null || clock.UtcNow - t.LastSeenUtc.Value > TimeSpan.FromSeconds(45));

            case AutomationTriggerType.ServiceStopped:
            {
                var serviceName = ReadConfig(task.TriggerConfigJson).GetValueOrDefault("serviceName")
                                  ?? ReadConfig(task.ActionConfigJson).GetValueOrDefault("serviceName");
                if (string.IsNullOrWhiteSpace(serviceName))
                {
                    return false;
                }

                foreach (var target in targets.Where(t => t.Agent?.State == AgentState.Active).Take(100))
                {
                    try
                    {
                        var services = await transport.GetServicesAsync(target, cancellationToken);
                        var service = services.FirstOrDefault(s => string.Equals(s.Name, serviceName, StringComparison.OrdinalIgnoreCase));
                        if (service is not null && !string.Equals(service.Status, "Running", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // Ignore unreachable targets during trigger evaluation.
                    }
                }

                return false;
            }

            case AutomationTriggerType.DiskSpaceLow:
            {
                var threshold = double.TryParse(ReadConfig(task.TriggerConfigJson).GetValueOrDefault("threshold"), out var parsed) ? parsed : 10;
                foreach (var target in targets.Where(t => t.Agent?.State == AgentState.Active).Take(100))
                {
                    try
                    {
                        var metrics = await transport.GetMetricsAsync(target, cancellationToken);
                        if (100 - metrics.DiskPercent < threshold)
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // Ignore unreachable targets.
                    }
                }

                return false;
            }

            default:
                return false;
        }
    }

    private async Task<AutomationExecutionView> ExecuteTaskAsync(AutomationTask task, string triggeredBy, CancellationToken cancellationToken)
    {
        await RunLock.WaitAsync(cancellationToken);
        try
        {
            var execution = new AutomationExecution
            {
                TaskId = task.Id,
                StartedUtc = clock.UtcNow,
                Status = AutomationRunStatus.Running,
                TriggeredBy = triggeredBy
            };
            await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
            {
                db.AutomationExecutions.Add(execution);
                await db.SaveChangesAsync(cancellationToken);
            }

            var log = new List<string>();
            var succeeded = 0;
            var failed = 0;
            var targets = task.ActionType == AutomationActionType.Notify
                ? []
                : await LoadTargetsAsync(task, cancellationToken);

            log.Add($"Automation '{task.Name}' started at {clock.UtcNow.ToLocalTime():g} by {triggeredBy}.");
            log.Add($"Action: {task.ActionType}; targets: {targets.Count}.");

            if (task.ActionType == AutomationActionType.Notify)
            {
                var configuration = ReadConfig(task.ActionConfigJson);
                var message = configuration.GetValueOrDefault("message") ?? $"Automation '{task.Name}' executed.";
                events.RaiseNotification(new AppNotification("RemoteDesk automation", message, AlertSeverity.Info));
                log.Add(message);
                succeeded = 1;
            }
            else
            {
                using var gate = new SemaphoreSlim(4);
                var results = new List<(string Name, bool Success, string Message)>();
                var sync = new object();
                var tasks = targets.Select(async target =>
                {
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        var result = await RunActionForComputerAsync(task, target, cancellationToken);
                        lock (sync)
                        {
                            results.Add((target.Name, result.Success, result.Message ?? result.Error ?? string.Empty));
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (sync)
                        {
                            results.Add((target.Name, false, ex.Message));
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                });
                await Task.WhenAll(tasks);

                foreach (var result in results.OrderBy(r => r.Name))
                {
                    log.Add($"{(result.Success ? "OK" : "FAIL")}  {result.Name}: {result.Message}");
                    if (result.Success)
                    {
                        succeeded++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }

            var status = failed == 0
                ? AutomationRunStatus.Succeeded
                : succeeded > 0 ? AutomationRunStatus.PartiallySucceeded : AutomationRunStatus.Failed;
            log.Add($"Completed with status {status}. Succeeded: {succeeded}, failed: {failed}.");

            var logText = string.Join(Environment.NewLine, log);
            if (logText.Length > 16000)
            {
                logText = logText[..16000] + Environment.NewLine + "[log truncated]";
            }

            execution.CompletedUtc = clock.UtcNow;
            execution.Status = status;
            execution.Succeeded = succeeded;
            execution.Failed = failed;
            execution.Log = logText;

            await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
            {
                db.AutomationExecutions.Update(execution);
                var storedTask = await db.AutomationTasks.FirstOrDefaultAsync(t => t.Id == task.Id, cancellationToken);
                if (storedTask is not null)
                {
                    storedTask.LastRunUtc = execution.StartedUtc;
                    storedTask.LastResult = status.ToString();
                    storedTask.NextRunUtc = storedTask.Enabled ? ComputeNextRun(storedTask, clock.UtcNow) : null;
                }

                await db.SaveChangesAsync(cancellationToken);
            }

            var resultText = failed == 0 ? "success" : $"{failed} target(s) failed";
            await audit.LogAsync(triggeredBy, AuditOperations.AutomationExecuted, result: status == AutomationRunStatus.Succeeded ? "Success" : "Failed",
                durationMs: (long)(execution.CompletedUtc.Value - execution.StartedUtc).TotalMilliseconds,
                details: $"Automation '{task.Name}' finished with {resultText}.");
            events.RaiseDataChanged();

            if (status is AutomationRunStatus.Failed or AutomationRunStatus.PartiallySucceeded)
            {
                events.RaiseNotification(new AppNotification("Automation failed",
                    $"'{task.Name}' finished with {resultText}. Review the execution log for details.",
                    AlertSeverity.Warning));
            }

            return new AutomationExecutionView
            {
                Id = execution.Id,
                TaskId = execution.TaskId,
                TaskName = task.Name,
                StartedUtc = execution.StartedUtc,
                CompletedUtc = execution.CompletedUtc,
                Status = execution.Status,
                Succeeded = execution.Succeeded,
                Failed = execution.Failed,
                Log = execution.Log,
                TriggeredBy = execution.TriggeredBy
            };
        }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task<OperationResult> RunActionForComputerAsync(AutomationTask task, Computer computer, CancellationToken cancellationToken)
    {
        if (computer.Agent is null || computer.Agent.State != AgentState.Active)
        {
            return OperationResult.Fail("No approved agent is available.");
        }

        var configuration = ReadConfig(task.ActionConfigJson);
        switch (task.ActionType)
        {
            case AutomationActionType.CheckOnline:
                return computer.LastSeenUtc is not null && clock.UtcNow - computer.LastSeenUtc.Value < TimeSpan.FromSeconds(45)
                    ? OperationResult.Ok("Computer is online.")
                    : OperationResult.Fail("Computer is offline.");

            case AutomationActionType.CheckDiskSpace:
            {
                var metrics = await transport.GetMetricsAsync(computer, cancellationToken);
                var free = 100 - metrics.DiskPercent;
                var threshold = double.TryParse(configuration.GetValueOrDefault("threshold"), out var t) ? t : 10;
                if (free < threshold)
                {
                    events.RaiseNotification(new AppNotification("Low disk space",
                        $"{computer.Name}: {free:F1}% free (threshold {threshold:F0}%).", AlertSeverity.Warning, computer.Id));
                    return OperationResult.Ok($"Low disk space: {free:F1}% free.");
                }

                return OperationResult.Ok($"{free:F1}% free.");
            }

            case AutomationActionType.CollectInventory:
            {
                var hardware = await inventory.RefreshHardwareAsync(HubConstants.AuditActorSystem, computer.Id, cancellationToken);
                var software = await inventory.RefreshSoftwareAsync(HubConstants.AuditActorSystem, computer.Id, cancellationToken);
                return hardware.Success && software.Success
                    ? OperationResult.Ok("Inventory refreshed.")
                    : OperationResult.Fail(string.Join(' ', new[] { hardware.Error, software.Error }.Where(e => !string.IsNullOrWhiteSpace(e))));
            }

            case AutomationActionType.CheckService:
            {
                var serviceName = configuration.GetValueOrDefault("serviceName");
                if (string.IsNullOrWhiteSpace(serviceName))
                {
                    return OperationResult.Fail("No service name is configured.");
                }

                var services = await transport.GetServicesAsync(computer, cancellationToken);
                var service = services.FirstOrDefault(s => string.Equals(s.Name, serviceName, StringComparison.OrdinalIgnoreCase));
                return service is null
                    ? OperationResult.Fail($"Service '{serviceName}' was not found.")
                    : string.Equals(service.Status, "Running", StringComparison.OrdinalIgnoreCase)
                        ? OperationResult.Ok($"'{service.DisplayName}' is running.")
                        : OperationResult.Fail($"'{service.DisplayName}' is {service.Status}.");
            }

            case AutomationActionType.GenerateReport:
            {
                var format = configuration.GetValueOrDefault("format") ?? "csv";
                var file = await reports.GenerateNetworkInventoryAsync(format, cancellationToken);
                Directory.CreateDirectory(paths.ExportsDirectory);
                var path = Path.Combine(paths.ExportsDirectory, file.FileName);
                await File.WriteAllBytesAsync(path, file.Content, cancellationToken);
                return OperationResult.Ok($"Report saved to {path}.");
            }

            case AutomationActionType.RunDiagnostic:
            {
                var result = await commands.ExecuteAsync(HubConstants.AuditActorSystem, computer,
                    "Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,LastBootUpTime | Format-List",
                    CommandShell.PowerShell, 60, false, true, true, cancellationToken);
                return result.ExitCode == 0 && result.Error is null
                    ? OperationResult.Ok("Diagnostic completed.")
                    : OperationResult.Fail(result.Error ?? result.StdErr ?? $"Exit code {result.ExitCode}.");
            }

            case AutomationActionType.RunCommand:
            {
                var command = configuration.GetValueOrDefault("command");
                if (string.IsNullOrWhiteSpace(command))
                {
                    return OperationResult.Fail("No command is configured.");
                }

                if (CommandSafety.IsPotentiallyDestructive(command))
                {
                    return OperationResult.Fail("Potentially destructive commands are not permitted in unattended automation.");
                }

                var shell = Enum.TryParse<CommandShell>(configuration.GetValueOrDefault("shell"), true, out var parsed) ? parsed : CommandShell.PowerShell;
                var timeout = int.TryParse(configuration.GetValueOrDefault("timeoutSeconds"), out var seconds) ? Math.Clamp(seconds, 5, HubConstants.MaxCommandTimeoutSeconds) : 120;
                var result = await commands.ExecuteAsync(HubConstants.AuditActorSystem, computer, command, shell, timeout, false, true, false, cancellationToken);
                return result.ExitCode == 0 && result.Error is null
                    ? OperationResult.Ok("Command completed successfully.")
                    : OperationResult.Fail(result.Error ?? result.StdErr ?? $"Exit code {result.ExitCode}.");
            }

            case AutomationActionType.Notify:
                return OperationResult.Ok("Notification sent.");

            default:
                return OperationResult.Fail($"Unsupported automation action {task.ActionType}.");
        }
    }

    private async Task<List<Computer>> LoadTargetsAsync(AutomationTask task, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Computers.Include(c => c.Agent).AsQueryable();
        if (task.TargetComputerId.HasValue)
        {
            query = query.Where(c => c.Id == task.TargetComputerId.Value);
        }
        else if (task.TargetGroupId.HasValue)
        {
            query = query.Where(c => c.GroupId == task.TargetGroupId.Value);
        }
        else
        {
            query = query.Where(c => c.IsManaged);
        }

        return await query.OrderBy(c => c.Name).Take(500).ToListAsync(cancellationToken);
    }

    private static OperationResult Validate(AutomationTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Name) || task.Name.Trim().Length > 128)
        {
            return OperationResult.Fail("Automation name must be between 1 and 128 characters.");
        }

        if (task.TriggerType == AutomationTriggerType.Interval && task.IntervalMinutes is null or < 1 or > 10080)
        {
            return OperationResult.Fail("Interval triggers require between 1 and 10,080 minutes.");
        }

        if (task.TriggerType == AutomationTriggerType.Daily)
        {
            if (!TimeOnly.TryParse(task.DailyTime, out _))
            {
                return OperationResult.Fail("Daily triggers require a time in HH:mm format.");
            }
        }

        var config = ReadConfig(task.ActionConfigJson);
        if (task.ActionType == AutomationActionType.CheckService && string.IsNullOrWhiteSpace(config.GetValueOrDefault("serviceName")))
        {
            return OperationResult.Fail("Select a Windows service to check.");
        }

        if (task.ActionType == AutomationActionType.RunCommand && string.IsNullOrWhiteSpace(config.GetValueOrDefault("command")))
        {
            return OperationResult.Fail("Enter the command to run.");
        }

        if (task.ActionType == AutomationActionType.Notify && string.IsNullOrWhiteSpace(config.GetValueOrDefault("message")))
        {
            return OperationResult.Fail("Enter the notification message.");
        }

        return OperationResult.Ok();
    }

    private static DateTime? ComputeNextRun(AutomationTask task, DateTime nowUtc)
    {
        switch (task.TriggerType)
        {
            case AutomationTriggerType.Interval when task.IntervalMinutes is > 0:
                return nowUtc.AddMinutes(task.IntervalMinutes.Value);

            case AutomationTriggerType.Daily when TimeOnly.TryParse(task.DailyTime, out var time):
            {
                var localNow = nowUtc.ToLocalTime();
                var nextLocal = localNow.Date.Add(time.ToTimeSpan());
                if (nextLocal <= localNow)
                {
                    nextLocal = nextLocal.AddDays(1);
                }

                return DateTime.SpecifyKind(nextLocal, DateTimeKind.Local).ToUniversalTime();
            }

            default:
                return null;
        }
    }

    private static Dictionary<string, string> ReadConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var values = JsonDefaults.Deserialize<Dictionary<string, string>>(json);
            return values is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
