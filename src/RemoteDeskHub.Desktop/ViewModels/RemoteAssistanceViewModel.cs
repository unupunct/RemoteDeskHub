using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Desktop.Services;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Desktop.ViewModels;

public sealed partial class RemoteAssistanceViewModel : PageViewModel
{
    private readonly IRemoteAssistanceService _assistance;
    private readonly IRemoteToolLauncher _launcher;
    private readonly ISettingsService _settings;
    private readonly ISessionContext _session;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private ObservableCollection<RemoteAssistanceTarget> targets = [];

    [ObservableProperty]
    private RemoteAssistanceTarget? selectedTarget;

    [ObservableProperty]
    private RemoteToolDefinition? selectedTool;

    [ObservableProperty]
    private string connectionTarget = string.Empty;

    [ObservableProperty]
    private bool requiresConnectionTarget;

    [ObservableProperty]
    private string connectionTargetLabel = "Connection ID";

    [ObservableProperty]
    private ObservableCollection<RemoteSession> history = [];

    [ObservableProperty]
    private RemoteSession? selectedSession;

    [ObservableProperty]
    private ObservableCollection<RemoteAssistanceProfile> profiles = [];

    [ObservableProperty]
    private RemoteAssistanceProfile? selectedProfile;

    [ObservableProperty]
    private bool isProfileEditorOpen;

    [ObservableProperty]
    private string profileName = string.Empty;

    [ObservableProperty]
    private RemoteToolDefinition? profileTool;

    [ObservableProperty]
    private string profileArguments = string.Empty;

    [ObservableProperty]
    private string profileInstructions = string.Empty;

    [ObservableProperty]
    private string launchInfo = "Select a computer and a tool, then launch the session. Every launch and its duration are audited.";

    public RemoteAssistanceViewModel(
        IRemoteAssistanceService assistance,
        IRemoteToolLauncher launcher,
        ISettingsService settings,
        ISessionContext session,
        IDialogService dialogs)
    {
        _assistance = assistance;
        _launcher = launcher;
        _settings = settings;
        _session = session;
        _dialogs = dialogs;
        SelectedTool = Tools.First(t => t.Kind == RemoteToolKind.Rdp);
        ProfileTool = ProfileTools.First(t => t.Kind == RemoteToolKind.Rdp);
    }

    public override string Title => "Remote Assistance";
    public override string Subtitle => "Launch RDP, Quick Assist, TeamViewer, AnyDesk, LM Viewer and RustDesk sessions";

    public string Actor => _session.CurrentUser?.UserName ?? HubConstants.AuditActorSystem;
    public bool CanLaunch => _session.CurrentUser?.Can(Permission.LaunchRemoteAssistance) == true;

    public IReadOnlyList<RemoteToolDefinition> Tools { get; } =
        RemoteToolCatalog.All.Where(t => t.Kind != RemoteToolKind.ThirdParty).ToList();

    public IReadOnlyList<RemoteToolDefinition> ProfileTools { get; } = RemoteToolCatalog.All;

    public override async Task LoadAsync()
    {
        await RunAsync(async token =>
        {
            Targets = new ObservableCollection<RemoteAssistanceTarget>(await _assistance.GetTargetsAsync(token));
            SelectedTarget ??= Targets.FirstOrDefault(t => t.Online) ?? Targets.FirstOrDefault();
            Profiles = new ObservableCollection<RemoteAssistanceProfile>(await _assistance.GetProfilesAsync(token));
            History = new ObservableCollection<RemoteSession>(await _assistance.GetSessionHistoryAsync(100, token));
            SelectedSession ??= History.FirstOrDefault(s => s.Status is RemoteSessionStatus.Prepared or RemoteSessionStatus.Started)
                                ?? History.FirstOrDefault();
        });
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunAsync(async token =>
        {
            Targets = new ObservableCollection<RemoteAssistanceTarget>(await _assistance.GetTargetsAsync(token));
            History = new ObservableCollection<RemoteSession>(await _assistance.GetSessionHistoryAsync(100, token));
            Profiles = new ObservableCollection<RemoteAssistanceProfile>(await _assistance.GetProfilesAsync(token));
        }, "Remote assistance data refreshed.");
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        if (SelectedTarget is null)
        {
            Fail("Select a target computer.");
            return;
        }

        if (SelectedTool is null)
        {
            Fail("Select a remote assistance tool.");
            return;
        }

        if (!SelectedTarget.Online)
        {
            Fail("The selected computer is offline. Remote assistance cannot be launched until its agent reconnects.");
            return;
        }

        var tool = SelectedTool.Kind;
        if (SelectedTool.RequiresConnectionTarget && string.IsNullOrWhiteSpace(ConnectionTarget))
        {
            Fail($"{SelectedTool.DisplayName} requires a connection ID or address.");
            return;
        }

        var target = RemoteToolCatalog.ResolveTarget(tool, SelectedTarget.IpAddress, ConnectionTarget);
        if (tool != RemoteToolKind.QuickAssist && string.IsNullOrWhiteSpace(target))
        {
            Fail("This tool needs a connection target.");
            return;
        }

        if (target is not null && !RemoteToolCatalog.IsValidTarget(target))
        {
            Fail("The connection target contains invalid characters or is too long.");
            return;
        }

        var displayTarget = target ?? "this computer";
        var consentRequired = await _settings.GetAsync(SettingsKeys.RemoteConsentRequired, false, PageToken);
        if (consentRequired)
        {
            var consentMessage =
                $"This will start {SelectedTool.DisplayName} on this computer and connect to {displayTarget}.\n\n" +
                "Only continue if the person at the target has explicitly agreed to this remote session. " +
                "The tool, target, consent and your account are recorded in the audit trail. " +
                "RemoteDesk Hub does not store tool passwords or Quick Assist codes.";
            var consentText =
                $"I confirm that the person at {SelectedTarget.ComputerName} has explicitly consented to this remote session.";

            if (!_dialogs.RequestConsent("Confirm remote session", consentMessage, consentText))
            {
                return;
            }
        }

        await RunAsync(async token =>
        {
            var result = await _assistance.PrepareLaunchAsync(
                Actor,
                SelectedTarget.ComputerId,
                tool,
                string.IsNullOrWhiteSpace(ConnectionTarget) ? null : ConnectionTarget.Trim(),
                consentConfirmed: consentRequired,
                token);

            if (!result.Success || result.Value is null)
            {
                throw new InvalidOperationException(result.Error ?? "The session could not be prepared.");
            }

            var info = result.Value;
            var launch = await _launcher.LaunchAsync(info, token);
            if (!launch.Success || launch.Value is null)
            {
                var error = launch.Error ?? "The remote tool could not be started.";
                await _assistance.CompleteLaunchAsync(
                    Actor, info.SessionId, launched: false, error, cancellationToken: token);
                throw new InvalidOperationException(error);
            }

            var details =
                $"Started {info.ExecutableName ?? info.ToolDisplayName} on the operator computer" +
                (launch.Value.ProcessId is null ? "." : $" (PID {launch.Value.ProcessId}).");
            var completion = await _assistance.CompleteLaunchAsync(
                Actor, info.SessionId, launched: true, details, processId: launch.Value.ProcessId, cancellationToken: token);

            if (launch.Value.ProcessId is > 0)
            {
                _ = MonitorSessionProcessAsync(info.SessionId, launch.Value.ProcessId.Value);
            }

            LaunchInfo = $"{info.ToolDisplayName} → {info.Target}\n{info.Instructions}" +
                         (string.IsNullOrWhiteSpace(info.ExecutableName) ? string.Empty : $"\nExecutable: {info.ExecutableName}") +
                         (string.IsNullOrWhiteSpace(info.Arguments) ? string.Empty : $"\nArguments: {info.Arguments}");
            History = new ObservableCollection<RemoteSession>(await _assistance.GetSessionHistoryAsync(100, token));
            SelectedSession = History.FirstOrDefault(s => s.Id == info.SessionId) ?? SelectedSession;

            _dialogs.Info(
                $"{info.ToolDisplayName} launched",
                $"Target: {info.Target}\n\n{info.Instructions}" +
                (string.IsNullOrWhiteSpace(info.ExecutableName) ? string.Empty : $"\n\nExecutable: {info.ExecutableName}") +
                (string.IsNullOrWhiteSpace(info.Arguments) ? string.Empty : $"\n\nArguments: {info.Arguments}") +
                (completion.Success ? string.Empty : $"\n\nWarning: the launch succeeded but the audit update failed: {completion.Error}"));
        }, "Remote session launched.");
    }

    [RelayCommand]
    private void CopyLaunchInfo()
    {
        try
        {
            Clipboard.SetText(LaunchInfo);
            StatusMessage = "Session details copied to the clipboard.";
        }
        catch (Exception ex)
        {
            Fail($"Clipboard is unavailable: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task EndSessionAsync()
    {
        if (SelectedSession is null || SelectedSession.Status is not (RemoteSessionStatus.Started or RemoteSessionStatus.Prepared))
        {
            Fail("Select a running or prepared session to mark as ended.");
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _assistance.MarkSessionEndedAsync(
                Actor, SelectedSession.Id, "marked ended by the operator", token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The session could not be ended.");
            }

            History = new ObservableCollection<RemoteSession>(await _assistance.GetSessionHistoryAsync(100, token));
        }, "Session marked as ended.");
    }

    [RelayCommand]
    private void NewProfile()
    {
        SelectedProfile = null;
        ProfileName = "My remote support profile";
        ProfileTool = ProfileTools.First(t => t.Kind == RemoteToolKind.Rdp);
        ProfileArguments = "/f";
        ProfileInstructions = "Connect with your standard administrator credentials. Do not share passwords in chat.";
        IsProfileEditorOpen = true;
    }

    [RelayCommand]
    private void EditProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        ProfileName = SelectedProfile.Name;
        ProfileTool = ProfileTools.FirstOrDefault(t => t.Kind == SelectedProfile.Tool) ??
                      ProfileTools.First(t => t.Kind == RemoteToolKind.Rdp);
        ProfileArguments = SelectedProfile.Arguments ?? string.Empty;
        ProfileInstructions = SelectedProfile.Instructions ?? string.Empty;
        IsProfileEditorOpen = true;
    }

    [RelayCommand]
    private void CancelProfile() => IsProfileEditorOpen = false;

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            Fail("A profile name is required.");
            return;
        }

        await RunAsync(async token =>
        {
            var profile = new RemoteAssistanceProfile
            {
                Id = SelectedProfile?.Id ?? Guid.NewGuid(),
                Name = ProfileName.Trim(),
                Tool = ProfileTool?.Kind ?? RemoteToolKind.Rdp,
                Arguments = string.IsNullOrWhiteSpace(ProfileArguments) ? null : ProfileArguments.Trim(),
                Instructions = string.IsNullOrWhiteSpace(ProfileInstructions) ? null : ProfileInstructions.Trim(),
                TargetComputerId = SelectedTarget?.ComputerId,
                CreatedUtc = SelectedProfile?.CreatedUtc ?? DateTime.UtcNow
            };

            var result = await _assistance.SaveProfileAsync(Actor, profile, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The profile could not be saved.");
            }

            IsProfileEditorOpen = false;
            Profiles = new ObservableCollection<RemoteAssistanceProfile>(await _assistance.GetProfilesAsync(token));
        }, "Support profile saved.");
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        if (!_dialogs.Confirm("Delete profile", $"Delete the support profile '{SelectedProfile.Name}'?", danger: true))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _assistance.DeleteProfileAsync(Actor, SelectedProfile.Id, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The profile could not be deleted.");
            }

            Profiles = new ObservableCollection<RemoteAssistanceProfile>(await _assistance.GetProfilesAsync(token));
        }, "Support profile deleted.");
    }

    private async Task MonitorSessionProcessAsync(Guid sessionId, int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync();
        }
        catch (ArgumentException)
        {
            // The process exited before monitoring started; still close the session below.
        }
        catch (InvalidOperationException)
        {
            // The process object is no longer valid.
        }

        try
        {
            var result = await _assistance.MarkSessionEndedAsync(
                Actor,
                sessionId,
                "the operator-side tool exited",
                CancellationToken.None);
            if (!result.Success)
            {
                return;
            }

            History = new ObservableCollection<RemoteSession>(
                await _assistance.GetSessionHistoryAsync(100, CancellationToken.None));
            SelectedSession = History.FirstOrDefault(s => s.Id == sessionId) ?? SelectedSession;
            StatusMessage = "Remote session ended automatically when the tool exited. Duration was recorded.";
        }
        catch
        {
            // Automatic closure is best effort; the operator can still mark the session ended.
        }
    }

    partial void OnSelectedToolChanged(RemoteToolDefinition? value)
    {
        RequiresConnectionTarget = value?.RequiresConnectionTarget == true;
        ConnectionTargetLabel = value?.Kind == RemoteToolKind.LmViewer
            ? "IP / hostname / NoIP ID"
            : "Connection ID";
        ApplySavedConnectionTarget(clearWhenMissing: true);
    }

    partial void OnSelectedTargetChanged(RemoteAssistanceTarget? value)
        => ApplySavedConnectionTarget(clearWhenMissing: true);

    private void ApplySavedConnectionTarget(bool clearWhenMissing)
    {
        if (SelectedTool is null || SelectedTarget is null)
        {
            return;
        }

        if (!SelectedTool.RequiresConnectionTarget)
        {
            // RDP and Windows Remote Assistance build their target from the computer address.
            // Keep a stale TeamViewer/AnyDesk ID from leaking into those command lines.
            ConnectionTarget = string.Empty;
            return;
        }

        var saved = SelectedTarget.GetConnectionTarget(SelectedTool.Kind);
        if (!string.IsNullOrWhiteSpace(saved))
        {
            ConnectionTarget = saved;
        }
        else if (clearWhenMissing)
        {
            ConnectionTarget = string.Empty;
        }
    }
}
