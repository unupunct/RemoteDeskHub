using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Desktop.Services;
using RemoteDeskHub.Infrastructure.Services;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Desktop.ViewModels;

public sealed partial class CommandsViewModel : PageViewModel
{
    private readonly ICommandService _commands;
    private readonly IComputerService _computers;
    private readonly ISessionContext _session;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private ObservableCollection<ComputerSummary> computers = [];

    [ObservableProperty]
    private ComputerSummary? selectedComputer;

    [ObservableProperty]
    private CommandShell shell = CommandShell.PowerShell;

    [ObservableProperty]
    private string commandText = "Get-Process | Sort-Object CPU -Descending | Select-Object -First 10 Name, Id, CPU";

    [ObservableProperty]
    private int timeoutSeconds = HubConstants.DefaultCommandTimeoutSeconds;

    [ObservableProperty]
    private string stdOut = string.Empty;

    [ObservableProperty]
    private string stdErr = string.Empty;

    [ObservableProperty]
    private string resultSummary = "No command has been run in this session.";

    [ObservableProperty]
    private ObservableCollection<CommandHistoryView> history = [];

    [ObservableProperty]
    private CommandHistoryView? selectedHistory;

    [ObservableProperty]
    private ObservableCollection<CommandApprovalView> approvals = [];

    [ObservableProperty]
    private CommandApprovalView? selectedApproval;

    [ObservableProperty]
    private int? lastExitCode;

    [ObservableProperty]
    private bool lastTimedOut;

    public CommandsViewModel(ICommandService commands, IComputerService computers,
        ISessionContext session, IDialogService dialogs)
    {
        _commands = commands;
        _computers = computers;
        _session = session;
        _dialogs = dialogs;
    }

    public override string Title => "Remote Commands";
    public override string Subtitle => "Auditable PowerShell and command-prompt execution";

    public string Actor => _session.CurrentUser?.UserName ?? HubConstants.AuditActorSystem;
    public bool CanRun => _session.CurrentUser?.Can(Permission.RunCommands) == true;
    public bool CanApprove => _session.CurrentUser?.Can(Permission.ApproveCommands) == true;
    public bool CanRunSelectedApproval =>
        SelectedApproval is { Status: CommandApprovalStatus.Approved } approval &&
        string.Equals(approval.RequestedBy, Actor, StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<CommandShell> Shells { get; } = Enum.GetValues<CommandShell>();

    public override async Task LoadAsync()
    {
        await RunAsync(async token =>
        {
            var data = await _computers.GetComputersAsync(cancellationToken: token);
            Computers = new ObservableCollection<ComputerSummary>(data);
            SelectedComputer ??= Computers.FirstOrDefault(c => c.IsOnline) ?? Computers.FirstOrDefault();
            await LoadHistoryAsync(token);
            await LoadApprovalsAsync(token);
        });
    }

    partial void OnSelectedHistoryChanged(CommandHistoryView? value)
    {
        if (value is null)
        {
            return;
        }

        StdOut = value.StdOut ?? string.Empty;
        StdErr = value.StdErr ?? string.Empty;
        LastExitCode = value.ExitCode;
        LastTimedOut = value.Status == CommandStatus.TimedOut;
        ResultSummary = $"{value.ComputerName} · {value.Shell} · {value.Status} · exit code {value.ExitCode?.ToString() ?? "n/a"} · {value.StartedUtc.ToLocalTime():HH:mm:ss}";
    }

    partial void OnSelectedApprovalChanged(CommandApprovalView? value)
    {
        OnPropertyChanged(nameof(CanRunSelectedApproval));
    }

    private async Task LoadHistoryAsync(CancellationToken token)
    {
        var data = await _commands.GetHistoryAsync(SelectedComputer?.Id, 150, token);
        History = new ObservableCollection<CommandHistoryView>(data);
    }

    private async Task LoadApprovalsAsync(CancellationToken token)
    {
        var selectedId = SelectedApproval?.Id;
        var data = await _commands.GetApprovalsAsync(pendingOnly: true, 100, token);
        Approvals = new ObservableCollection<CommandApprovalView>(data);
        SelectedApproval = selectedId is { } id
            ? Approvals.FirstOrDefault(a => a.Id == id)
            : null;
    }

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (SelectedComputer is null)
        {
            Fail("Select a computer first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(CommandText))
        {
            Fail("Enter a command to run.");
            return;
        }

        if (TimeoutSeconds is < 5 or > HubConstants.MaxCommandTimeoutSeconds)
        {
            Fail($"Timeout must be between 5 and {HubConstants.MaxCommandTimeoutSeconds} seconds.");
            return;
        }

        var preview = CommandText.Trim();
        if (preview.Length > 180)
        {
            preview = preview[..180] + "…";
        }

        var destructive = CommandSafety.IsPotentiallyDestructive(preview);
        var critical = CommandSafety.IsCritical(preview);
        var warning = destructive
            ? "\n\nThis command looks potentially destructive. It is recorded in the audit log and may be blocked by the command policy."
            : "\n\nThe command runs as the agent service identity and is recorded in the audit log.";

        if (!_dialogs.Confirm("Run remote command",
                $"Run this {Shell} command on {SelectedComputer.Name}?\n\n{preview}{warning}" +
                (critical ? "\n\nCritical commands are refused unless an administrator has added the exact command to the allowlist." : string.Empty),
                danger: destructive || critical))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _commands.ExecuteAsync(Actor, ToComputer(SelectedComputer), CommandText.Trim(),
                Shell, TimeoutSeconds, requireElevation: true, confirmed: true, isBulk: false, token);

            StdOut = result.StdOut;
            StdErr = result.StdErr;
            LastExitCode = result.ExitCode;
            LastTimedOut = result.TimedOut;
            ResultSummary = result.Cancelled
                ? "The command was cancelled."
                : result.TimedOut
                    ? $"Timed out after {TimeSpan.FromMilliseconds(result.DurationMs).TotalSeconds:0.0}s."
                    : $"Exit code {result.ExitCode} · {result.DurationMs} ms" +
                      (result.OutputTruncated ? " · output truncated" : string.Empty);

            await LoadHistoryAsync(token);
            await LoadApprovalsAsync(token);
            if (result.ApprovalId is { } approvalId)
            {
                ResultSummary = $"Approval request {approvalId} is waiting for a second administrator.";
            }

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                throw new InvalidOperationException(result.Error);
            }
        }, "Command completed.");
    }

    [RelayCommand]
    private async Task CancelCommandAsync()
    {
        if (SelectedComputer is null || SelectedHistory is null)
        {
            Fail("Select a command in the history to cancel.");
            return;
        }

        if (SelectedHistory.Status is not (CommandStatus.Running or CommandStatus.Queued))
        {
            Fail("Only queued or running commands can be cancelled.");
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _commands.CancelAsync(Actor, SelectedComputer.Id, SelectedHistory.Id, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The command could not be cancelled.");
            }

            await LoadHistoryAsync(token);
        }, "Cancellation requested.");
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        await RunAsync(token => LoadHistoryAsync(token), "Command history refreshed.");
    }

    [RelayCommand]
    private async Task ExportHistoryAsync()
    {
        await RunAsync(async token =>
        {
            var report = await _commands.ExportHistoryAsync("csv", token);
            _dialogs.SaveReport(report);
        }, "Command history exported.");
    }

    [RelayCommand]
    private async Task RefreshApprovalsAsync()
    {
        await RunAsync(token => LoadApprovalsAsync(token), "Approval queue refreshed.");
    }

    [RelayCommand]
    private async Task ApproveSelectedAsync()
    {
        if (!CanApprove)
        {
            Fail("You do not have permission to approve commands.");
            return;
        }

        if (SelectedApproval is not { } approval)
        {
            Fail("Select a pending approval first.");
            return;
        }

        if (!approval.IsPending)
        {
            Fail("Only pending approvals can be approved.");
            return;
        }

        if (string.Equals(approval.RequestedBy, Actor, StringComparison.OrdinalIgnoreCase))
        {
            Fail("A different administrator must approve this request.");
            return;
        }

        if (!_dialogs.Confirm("Approve remote command",
                $"Approve {approval.RequestedBy}'s {approval.Shell} command on {approval.ComputerName}?\n\n{approval.Command}\n\nThe requester must still run the approved command before it expires.",
                danger: true))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _commands.ApproveCommandAsync(Actor, approval.Id, null, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The approval could not be recorded.");
            }

            await LoadApprovalsAsync(token);
        }, "Command approval recorded.");
    }

    [RelayCommand]
    private async Task RejectSelectedAsync()
    {
        if (!CanApprove)
        {
            Fail("You do not have permission to reject commands.");
            return;
        }

        if (SelectedApproval is not { } approval || !approval.IsPending)
        {
            Fail("Select a pending approval first.");
            return;
        }

        if (!_dialogs.Confirm("Reject remote command",
                $"Reject {approval.RequestedBy}'s {approval.Shell} command on {approval.ComputerName}?\n\n{approval.Command}",
                danger: false))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _commands.RejectCommandAsync(Actor, approval.Id, null, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The rejection could not be recorded.");
            }

            await LoadApprovalsAsync(token);
        }, "Command approval rejected.");
    }

    [RelayCommand]
    private async Task RunApprovedAsync()
    {
        if (SelectedApproval is not { } approval)
        {
            Fail("Select an approved command first.");
            return;
        }

        if (!CanRunSelectedApproval)
        {
            Fail("Only the requester can run an approved command.");
            return;
        }

        if (!_dialogs.Confirm("Run approved command",
                $"Run the approved {approval.Shell} command on {approval.ComputerName}?\n\n{approval.Command}",
                danger: true))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _commands.ExecuteApprovedAsync(Actor, approval.Id, token);
            StdOut = result.StdOut;
            StdErr = result.StdErr;
            LastExitCode = result.ExitCode;
            LastTimedOut = result.TimedOut;
            ResultSummary = $"Approved command exit code {result.ExitCode} — {result.DurationMs} ms";
            await LoadHistoryAsync(token);
            await LoadApprovalsAsync(token);
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                throw new InvalidOperationException(result.Error);
            }
        }, "Approved command completed.");
    }

    [RelayCommand]
    private void ClearOutput()
    {
        StdOut = string.Empty;
        StdErr = string.Empty;
        ResultSummary = "Output cleared.";
        LastExitCode = null;
        LastTimedOut = false;
    }

    private static Computer ToComputer(ComputerSummary summary) => new()
    {
        Id = summary.Id,
        Name = summary.Name,
        IpAddress = summary.IpAddress,
        AgentVersion = summary.AgentVersion,
        Status = summary.Status
    };
}
