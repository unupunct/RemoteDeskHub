using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Desktop.Services;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Security;

namespace RemoteDeskHub.Desktop.ViewModels;

public sealed partial class ComputersViewModel : PageViewModel
{
    private readonly IComputerService _computers;
    private readonly IEnrollmentService _enrollment;
    private readonly IInventoryService _inventory;
    private readonly IRemoteAssistanceService _assistance;
    private readonly ISettingsService _settings;
    private readonly ISessionContext _session;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private ObservableCollection<ComputerSummary> computers = [];

    [ObservableProperty]
    private ObservableCollection<GroupView> groups = [];

    [ObservableProperty]
    private GroupView? selectedGroupFilter;

    [ObservableProperty]
    private ComputerSummary? selectedComputer;

    [ObservableProperty]
    private ComputerDetail? selectedDetail;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string summaryText = string.Empty;

    [ObservableProperty]
    private bool isEditorOpen;

    [ObservableProperty]
    private bool isEditing;

    [ObservableProperty]
    private string editorName = string.Empty;

    [ObservableProperty]
    private string editorIpAddress = string.Empty;

    [ObservableProperty]
    private string editorMacAddress = string.Empty;

    [ObservableProperty]
    private string editorFriendlyName = string.Empty;

    [ObservableProperty]
    private string editorDescription = string.Empty;

    [ObservableProperty]
    private string editorNotes = string.Empty;

    [ObservableProperty]
    private string editorTags = string.Empty;

    [ObservableProperty]
    private string editorTeamViewerId = string.Empty;

    [ObservableProperty]
    private string editorAnyDeskId = string.Empty;

    [ObservableProperty]
    private string editorLmViewerId = string.Empty;

    [ObservableProperty]
    private string editorRustDeskId = string.Empty;

    [ObservableProperty]
    private GroupView? editorGroup;

    [ObservableProperty]
    private ObservableCollection<HardwareInventoryHistoryView> hardwareHistory = [];

    [ObservableProperty]
    private string bulkProgressText = string.Empty;

    [ObservableProperty]
    private double bulkProgressPercent;

    [ObservableProperty]
    private bool isEnrollmentOpen;

    [ObservableProperty]
    private string enrollmentHubUrl = $"https://localhost:{HubConstants.DefaultHubPort}";

    [ObservableProperty]
    private double enrollmentLifetimeHours = 24;

    [ObservableProperty]
    private bool enrollmentAutoApprove;

    [ObservableProperty]
    private string? generatedEnrollmentToken;

    [ObservableProperty]
    private string generatedEnrollmentInfo = string.Empty;

    [ObservableProperty]
    private ObservableCollection<AgentEnrollment> pendingEnrollments = [];

    public ComputersViewModel(
        IComputerService computers,
        IEnrollmentService enrollment,
        IInventoryService inventory,
        IRemoteAssistanceService assistance,
        ISettingsService settings,
        ISessionContext session,
        IDialogService dialogs)
    {
        _computers = computers;
        _enrollment = enrollment;
        _inventory = inventory;
        _assistance = assistance;
        _settings = settings;
        _session = session;
        _dialogs = dialogs;
    }

    public override string Title => "Computers";
    public override string Subtitle => "Every managed and discovered computer on your LAN";

    public string Actor => _session.CurrentUser?.UserName ?? HubConstants.AuditActorSystem;
    public bool CanManage => _session.CurrentUser?.Can(Permission.ManageComputers) == true;
    public bool CanManageGroups => _session.CurrentUser?.Can(Permission.ManageGroups) == true;
    public bool CanEnroll => _session.CurrentUser?.Can(Permission.EnrollAgents) == true;
    public bool CanRevokeAgent => CanManage && SelectedDetail?.AgentId is not null &&
                                  SelectedDetail.AgentState == AgentState.Active;
    public bool IsDetailsVisible => !IsEditorOpen && !IsEnrollmentOpen;
    public bool HasPendingEnrollments => PendingEnrollments.Count > 0;
    public string EnrollmentButtonText => PendingEnrollments.Count > 0
        ? $"Agent enrollments ({PendingEnrollments.Count})"
        : "Enroll agent";
    public string EditorTitle => IsEditing ? "Edit computer" : "Add computer";

    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(EditorTitle));

    partial void OnIsEditorOpenChanged(bool value) => OnPropertyChanged(nameof(IsDetailsVisible));

    partial void OnIsEnrollmentOpenChanged(bool value) => OnPropertyChanged(nameof(IsDetailsVisible));

    partial void OnPendingEnrollmentsChanged(ObservableCollection<AgentEnrollment> value)
    {
        OnPropertyChanged(nameof(HasPendingEnrollments));
        OnPropertyChanged(nameof(EnrollmentButtonText));
    }

    partial void OnSelectedDetailChanged(ComputerDetail? value) => OnPropertyChanged(nameof(CanRevokeAgent));

    public override async Task LoadAsync()
    {
        await RunAsync(async token =>
        {
            await LoadGroupsAsync(token);
            await LoadComputersAsync(token);
            if (CanEnroll)
            {
                await LoadPendingEnrollmentsAsync(token);
            }
        });
    }

    private async Task LoadGroupsAsync(CancellationToken token)
    {
        var data = await _computers.GetGroupsAsync(token);
        var list = new List<GroupView> { new() { Id = Guid.Empty, Name = "All groups", Kind = "All" } };
        list.AddRange(data);
        Groups = new ObservableCollection<GroupView>(list);
        SelectedGroupFilter ??= Groups[0];
        EditorGroup ??= Groups.FirstOrDefault(g => g.Id != Guid.Empty);
    }

    private async Task LoadComputersAsync(CancellationToken token)
    {
        var groupId = SelectedGroupFilter is not null && SelectedGroupFilter.Id != Guid.Empty
            ? SelectedGroupFilter.Id
            : (Guid?)null;
        var data = await _computers.GetComputersAsync(groupId, string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(), token);
        Computers = new ObservableCollection<ComputerSummary>(data);
        SummaryText = data.Count == 0
            ? "No computers match this view. Run a discovery scan or enroll an agent."
            : $"{data.Count} computer(s) · {data.Count(c => c.IsOnline)} online · {data.Count(c => c.ActiveAlertCount > 0)} with active alerts";

        var previousId = SelectedComputer?.Id;
        SelectedComputer = previousId is null ? Computers.FirstOrDefault() : Computers.FirstOrDefault(c => c.Id == previousId) ?? Computers.FirstOrDefault();
    }

    private async Task LoadPendingEnrollmentsAsync(CancellationToken token)
    {
        var all = await _enrollment.GetEnrollmentsAsync(token);
        PendingEnrollments = new ObservableCollection<AgentEnrollment>(
            all.Where(e => e.Status == EnrollmentState.Pending && e.ConsumedUtc is not null));
    }

    partial void OnSelectedComputerChanged(ComputerSummary? value)
    {
        if (value is not null)
        {
            _ = LoadDetailAsync(value.Id);
        }
        else
        {
            SelectedDetail = null;
        }
    }

    private async Task LoadDetailAsync(Guid computerId)
    {
        try
        {
            var detail = await _computers.GetComputerDetailAsync(computerId);
            var history = await _inventory.GetHardwareHistoryAsync(computerId, 50, PageToken);
            SelectedDetail = detail;
            HardwareHistory = new ObservableCollection<HardwareInventoryHistoryView>(history);
        }
        catch (Exception ex)
        {
            Fail($"Computer details could not be loaded: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunAsync(async token =>
        {
            await LoadGroupsAsync(token);
            await LoadComputersAsync(token);
            if (CanEnroll)
            {
                await LoadPendingEnrollmentsAsync(token);
            }

            if (SelectedComputer is not null)
            {
                await LoadDetailAsync(SelectedComputer.Id);
            }
        }, "Computer list refreshed.");
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        await RunAsync(token => LoadComputersAsync(token));
    }

    [RelayCommand]
    private async Task OpenEnrollmentAsync()
    {
        if (!CanEnroll)
        {
            Fail("Your role cannot enroll agents.");
            return;
        }

        IsEditorOpen = false;
        IsEnrollmentOpen = true;
        GeneratedEnrollmentToken = null;
        GeneratedEnrollmentInfo = string.Empty;

        await RunAsync(async token =>
        {
            var configured = await _settings.GetAsync(SettingsKeys.HubUrl,
                $"https://{Environment.MachineName}:{HubConstants.DefaultHubPort}", token);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                EnrollmentHubUrl = configured;
            }

            await LoadPendingEnrollmentsAsync(token);
        });
    }

    [RelayCommand]
    private void CloseEnrollment()
    {
        IsEnrollmentOpen = false;
        GeneratedEnrollmentToken = null;
        GeneratedEnrollmentInfo = string.Empty;
    }

    [RelayCommand]
    private async Task GenerateEnrollmentPackageAsync()
    {
        if (!CanEnroll)
        {
            Fail("Your role cannot enroll agents.");
            return;
        }

        if (!Uri.TryCreate(EnrollmentHubUrl?.Trim(), UriKind.Absolute, out var hubUri) ||
            hubUri.Scheme != Uri.UriSchemeHttps)
        {
            Fail("Enter the HTTPS address agents will use to reach this hub, for example https://server01:7443.");
            return;
        }

        if (EnrollmentLifetimeHours is < 1 or > 168)
        {
            Fail("Choose a token lifetime between 1 and 168 hours.");
            return;
        }

        await RunAsync(async token =>
        {
            var package = await _enrollment.CreatePairingPackageAsync(
                Actor,
                TimeSpan.FromHours(EnrollmentLifetimeHours),
                hubUri.ToString().TrimEnd('/'),
                EnrollmentAutoApprove,
                token);

            GeneratedEnrollmentToken = package.EnrollmentToken;
            GeneratedEnrollmentInfo =
                $"Token expires {package.ExpiresUtc.ToLocalTime():g}. " +
                "Copy the pairing package to the target computer now; it can only be used once.";

            var json = JsonDefaults.Serialize(package);
            var target = _dialogs.SaveFile(
                $"remotedesk-{Environment.MachineName}.rdhenroll",
                "RemoteDesk pairing package (*.rdhenroll)|*.rdhenroll");

            if (target is not null)
            {
                await File.WriteAllTextAsync(target, json, token);
                _dialogs.Info("Pairing package created",
                    $"Saved to:\n{target}\n\n" +
                    "Deliver this single-use file to the target computer over a trusted channel, then install or restart the agent.\n\n" +
                    $"Hub: {package.HubUrl}\nToken expires: {package.ExpiresUtc.ToLocalTime():g}");
            }

            await LoadPendingEnrollmentsAsync(token);
        }, "Enrollment token generated.");
    }

    [RelayCommand]
    private async Task ApproveEnrollmentAsync(AgentEnrollment? enrollment)
    {
        if (enrollment is null || !CanEnroll)
        {
            return;
        }

        if (!_dialogs.Confirm("Approve agent",
                $"Approve '{enrollment.ComputerName ?? "the pending agent"}'?\n\n" +
                "A client certificate will be issued and pinned to this computer. This action is audited.",
                danger: false))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _enrollment.ApproveAsync(Actor, enrollment.Id, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The agent could not be approved.");
            }

            await LoadComputersAsync(token);
            await LoadPendingEnrollmentsAsync(token);
        }, $"Agent '{enrollment.ComputerName}' approved.");
    }

    [RelayCommand]
    private async Task RejectEnrollmentAsync(AgentEnrollment? enrollment)
    {
        if (enrollment is null || !CanEnroll)
        {
            return;
        }

        if (!_dialogs.Confirm("Reject agent",
                $"Reject '{enrollment.ComputerName ?? "the pending agent"}'? The agent will not receive a certificate.",
                danger: true))
        {
            return;
        }

        var reason = _dialogs.Prompt("Reject enrollment", "Reason (recorded in the audit log)",
            "Rejected by administrator.");
        if (reason is null)
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _enrollment.RejectAsync(Actor, enrollment.Id, reason, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The enrollment could not be rejected.");
            }

            await LoadPendingEnrollmentsAsync(token);
        }, "Enrollment rejected.");
    }

    [RelayCommand]
    private async Task RevokeAgentAsync()
    {
        if (SelectedDetail?.AgentId is not { } agentId)
        {
            return;
        }

        if (!_dialogs.Confirm("Revoke agent",
                $"Revoke the agent on '{SelectedDetail.Summary.Name}'?\n\n" +
                "The pinned certificate is rejected immediately. The agent must be re-enrolled with a new pairing package.",
                danger: true))
        {
            return;
        }

        var reason = _dialogs.Prompt("Revoke agent", "Reason (recorded in the audit log)", "Administrative revocation");
        if (reason is null)
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _enrollment.RevokeAgentAsync(Actor, agentId, reason, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The agent could not be revoked.");
            }

            await LoadComputersAsync(token);
            await LoadDetailAsync(SelectedDetail.Summary.Id);
        }, "Agent revoked.");
    }

    [RelayCommand]
    private async Task OpenAddAsync()
    {
        IsEnrollmentOpen = false;
        IsEditing = false;
        EditorName = string.Empty;
        EditorIpAddress = string.Empty;
        EditorMacAddress = string.Empty;
        EditorFriendlyName = string.Empty;
        EditorDescription = string.Empty;
        EditorNotes = string.Empty;
        EditorTags = string.Empty;
        EditorTeamViewerId = string.Empty;
        EditorAnyDeskId = string.Empty;
        EditorLmViewerId = string.Empty;
        EditorRustDeskId = string.Empty;
        EditorGroup = Groups.FirstOrDefault(g => g.Id != Guid.Empty);
        IsEditorOpen = true;
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task OpenEditAsync()
    {
        if (SelectedDetail is null)
        {
            return;
        }

        IsEditing = true;
        IsEnrollmentOpen = false;
        EditorName = SelectedDetail.Summary.Name;
        EditorIpAddress = SelectedDetail.Summary.IpAddress ?? string.Empty;
        EditorMacAddress = SelectedDetail.MacAddress ?? string.Empty;
        EditorFriendlyName = SelectedDetail.Summary.FriendlyName ?? string.Empty;
        EditorDescription = SelectedDetail.Summary.Description ?? string.Empty;
        EditorNotes = SelectedDetail.Summary.Notes ?? string.Empty;
        EditorTags = string.Empty;
        EditorTeamViewerId = SelectedDetail.Summary.TeamViewerId ?? string.Empty;
        EditorAnyDeskId = SelectedDetail.Summary.AnyDeskId ?? string.Empty;
        EditorLmViewerId = SelectedDetail.Summary.LmViewerId ?? string.Empty;
        EditorRustDeskId = SelectedDetail.Summary.RustDeskId ?? string.Empty;
        EditorGroup = Groups.FirstOrDefault(g => g.Id == SelectedDetail.Summary.GroupId);
        IsEditorOpen = true;
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditorOpen = false;

    [RelayCommand]
    private async Task SaveEditorAsync()
    {
        if (IsEditing)
        {
            if (SelectedComputer is null)
            {
                return;
            }

            await RunAsync(async token =>
            {
                var computerId = SelectedComputer.Id;
                var result = await _computers.UpdateComputerAsync(Actor, computerId,
                    NullIfEmpty(EditorFriendlyName), NullIfEmpty(EditorDescription), NullIfEmpty(EditorNotes),
                    NullIfEmpty(EditorTags), GroupId(EditorGroup), token);
                if (!result.Success)
                {
                    throw new InvalidOperationException(result.Error ?? "The computer could not be updated.");
                }

                var ids = await _assistance.SaveRemoteAccessIdsAsync(
                    Actor,
                    computerId,
                    NullIfEmpty(EditorTeamViewerId),
                    NullIfEmpty(EditorAnyDeskId),
                    NullIfEmpty(EditorLmViewerId),
                    NullIfEmpty(EditorRustDeskId),
                    token);
                if (!ids.Success)
                {
                    throw new InvalidOperationException(ids.Error ?? "The remote-access IDs could not be saved.");
                }

                IsEditorOpen = false;
                await LoadComputersAsync(token);
                await LoadDetailAsync(computerId);
            }, "Computer details updated.");
            return;
        }

        if (string.IsNullOrWhiteSpace(EditorName))
        {
            Fail("A computer name is required.");
            return;
        }

        if (!NetworkAddressGuard.TryParseLanAddress(EditorIpAddress, out var address, out var error))
        {
            Fail(error);
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _computers.AddManualComputerAsync(Actor, EditorName.Trim(), address.ToString(),
                NullIfEmpty(EditorMacAddress), GroupId(EditorGroup),
                NullIfEmpty(EditorNotes), token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The computer could not be added.");
            }

            IsEditorOpen = false;
            await LoadComputersAsync(token);
        }, $"Computer '{EditorName.Trim()}' added to the inventory.");
    }

    [RelayCommand]
    private async Task ToggleTrustAsync()
    {
        if (SelectedComputer is null)
        {
            return;
        }

        var trust = !SelectedComputer.IsTrusted;
        if (!_dialogs.Confirm(trust ? "Trust computer" : "Remove trust",
                trust
                    ? $"Mark '{SelectedComputer.Name}' as trusted? Trusted computers are pinned to their enrolled agent identity."
                    : $"Remove trust from '{SelectedComputer.Name}'? Its agent will need to be re-approved before management operations are allowed.",
                danger: !trust))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _computers.SetTrustedAsync(Actor, SelectedComputer.Id, trust, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "Trust could not be changed.");
            }

            await LoadComputersAsync(token);
            await LoadDetailAsync(SelectedComputer.Id);
        }, trust ? "Computer marked as trusted." : "Trust removed.");
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (SelectedComputer is null)
        {
            return;
        }

        if (!_dialogs.Confirm("Remove computer",
                $"Remove '{SelectedComputer.Name}' and its history from the hub?\n\n" +
                "This does not uninstall the agent; a re-enrolled agent will appear again. This action is audited.",
                danger: true))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _computers.RemoveComputerAsync(Actor, SelectedComputer.Id, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The computer could not be removed.");
            }

            SelectedComputer = null;
            await LoadComputersAsync(token);
        }, "Computer removed.");
    }

    [RelayCommand]
    private async Task RefreshInventoryAsync()
    {
        if (SelectedComputer is null)
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _computers.RefreshInventoryAsync(Actor, SelectedComputer.Id, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "Inventory refresh failed.");
            }

            await LoadDetailAsync(SelectedComputer.Id);
        }, "Inventory refreshed from the agent.");
    }

    [RelayCommand]
    private async Task BulkRefreshInventoryAsync()
    {
        await RunBulkAsync(ids => _computers.BulkRefreshInventoryAsync(Actor, ids, null, PageToken), "inventory refresh");
    }

    [RelayCommand]
    private async Task BulkDiagnosticsAsync()
    {
        await RunBulkAsync(ids => _computers.BulkRunDiagnosticAsync(Actor, ids, null, PageToken), "diagnostic");
    }

    private async Task RunBulkAsync(Func<IReadOnlyCollection<Guid>, Task<BulkOperationProgress>> action, string label)
    {
        var ids = Computers.Select(c => c.Id).ToList();
        if (ids.Count == 0)
        {
            Fail("There are no computers in the current view.");
            return;
        }

        if (!_dialogs.Confirm($"Run {label}", $"Run a {label} against {ids.Count} computer(s)? Each agent is contacted independently.", danger: false))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var progress = new Progress<BulkOperationProgress>(p =>
            {
                BulkProgressPercent = p.Percent;
                BulkProgressText = $"{p.Operation}: {p.Completed + p.Failed}/{p.Total} complete, {p.Failed} failed" +
                                   (string.IsNullOrWhiteSpace(p.CurrentComputer) ? string.Empty : $" — {p.CurrentComputer}");
            });
            var result = await action(ids);
            BulkProgressPercent = 100;
            BulkProgressText = $"{result.Operation} finished: {result.Completed} succeeded, {result.Failed} failed.";
            await LoadComputersAsync(token);
        }, $"{label} completed.");
    }

    [RelayCommand]
    private async Task CreateGroupAsync()
    {
        var name = _dialogs.Prompt("New group", "Group name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _computers.CreateGroupAsync(Actor, name.Trim(), null, "Custom", token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The group could not be created.");
            }

            await LoadGroupsAsync(token);
        }, $"Group '{name.Trim()}' created.");
    }

    [RelayCommand]
    private async Task RenameGroupAsync()
    {
        if (SelectedGroupFilter is null || SelectedGroupFilter.Id == Guid.Empty || SelectedGroupFilter.IsSystem)
        {
            Fail("Select a custom group to rename. Built-in groups cannot be renamed.");
            return;
        }

        var name = _dialogs.Prompt("Rename group", "Group name", SelectedGroupFilter.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _computers.RenameGroupAsync(Actor, SelectedGroupFilter.Id, name.Trim(), SelectedGroupFilter.Description, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The group could not be renamed.");
            }

            await LoadGroupsAsync(token);
        }, "Group renamed.");
    }

    [RelayCommand]
    private async Task DeleteGroupAsync()
    {
        if (SelectedGroupFilter is null || SelectedGroupFilter.Id == Guid.Empty)
        {
            return;
        }

        if (SelectedGroupFilter.IsSystem)
        {
            Fail("Built-in groups cannot be deleted.");
            return;
        }

        if (!_dialogs.Confirm("Delete group", $"Delete the group '{SelectedGroupFilter.Name}'? Computers are not deleted; they become ungrouped.", danger: true))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _computers.DeleteGroupAsync(Actor, SelectedGroupFilter.Id, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The group could not be deleted.");
            }

            SelectedGroupFilter = Groups.FirstOrDefault(g => g.Id == Guid.Empty);
            await LoadGroupsAsync(token);
            await LoadComputersAsync(token);
        }, "Group deleted.");
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Guid? GroupId(GroupView? group) =>
        group is not null && group.Id != Guid.Empty ? group.Id : null;
}
