using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Desktop.Services;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Desktop.ViewModels;

public sealed partial class AutomationViewModel : PageViewModel
{
    private readonly IAutomationService _automation;
    private readonly IComputerService _computers;
    private readonly ISessionContext _session;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private ObservableCollection<AutomationTaskView> tasks = [];

    [ObservableProperty]
    private AutomationTaskView? selectedTask;

    [ObservableProperty]
    private ObservableCollection<AutomationExecutionView> executions = [];

    [ObservableProperty]
    private ObservableCollection<ComputerSummary> computers = [];

    [ObservableProperty]
    private ComputerSummary? targetComputer;

    [ObservableProperty]
    private bool isEditorOpen;

    [ObservableProperty]
    private string taskName = string.Empty;

    [ObservableProperty]
    private string taskDescription = string.Empty;

    [ObservableProperty]
    private bool taskEnabled = true;

    [ObservableProperty]
    private AutomationTriggerType triggerType = AutomationTriggerType.Manual;

    [ObservableProperty]
    private AutomationActionType actionType = AutomationActionType.CheckOnline;

    [ObservableProperty]
    private int? intervalMinutes = 60;

    [ObservableProperty]
    private string dailyTime = "02:00";

    [ObservableProperty]
    private string configServiceName = string.Empty;

    [ObservableProperty]
    private string configCommand = string.Empty;

    [ObservableProperty]
    private CommandShell configShell = CommandShell.PowerShell;

    [ObservableProperty]
    private string configMessage = string.Empty;

    [ObservableProperty]
    private double configThreshold = 10;

    public AutomationViewModel(IAutomationService automation, IComputerService computers,
        ISessionContext session, IDialogService dialogs)
    {
        _automation = automation;
        _computers = computers;
        _session = session;
        _dialogs = dialogs;
    }

    public override string Title => "Automation";
    public override string Subtitle => "Scheduled health checks, reports and maintenance tasks";

    public string Actor => _session.CurrentUser?.UserName ?? HubConstants.AuditActorSystem;
    public bool CanManage => _session.CurrentUser?.Can(Permission.ManageAutomation) == true;
    public IReadOnlyList<AutomationTriggerType> Triggers { get; } = Enum.GetValues<AutomationTriggerType>();
    public IReadOnlyList<AutomationActionType> Actions { get; } = Enum.GetValues<AutomationActionType>();
    public IReadOnlyList<CommandShell> Shells { get; } = Enum.GetValues<CommandShell>();

    public bool NeedsServiceName => ActionType == AutomationActionType.CheckService;
    public bool NeedsCommand => ActionType == AutomationActionType.RunCommand;
    public bool NeedsMessage => ActionType == AutomationActionType.Notify;
    public bool NeedsThreshold => ActionType == AutomationActionType.CheckDiskSpace;
    public bool NeedsInterval => TriggerType == AutomationTriggerType.Interval;
    public bool NeedsDailyTime => TriggerType == AutomationTriggerType.Daily;

    public override async Task LoadAsync()
    {
        await RunAsync(async token =>
        {
            Computers = new ObservableCollection<ComputerSummary>(await _computers.GetComputersAsync(cancellationToken: token));
            await LoadTasksAsync(token);
            await LoadExecutionsAsync(token);
        });
    }

    partial void OnActionTypeChanged(AutomationActionType value)
    {
        OnPropertyChanged(nameof(NeedsServiceName));
        OnPropertyChanged(nameof(NeedsCommand));
        OnPropertyChanged(nameof(NeedsMessage));
        OnPropertyChanged(nameof(NeedsThreshold));
    }

    partial void OnTriggerTypeChanged(AutomationTriggerType value)
    {
        OnPropertyChanged(nameof(NeedsInterval));
        OnPropertyChanged(nameof(NeedsDailyTime));
    }

    private async Task LoadTasksAsync(CancellationToken token)
    {
        Tasks = new ObservableCollection<AutomationTaskView>(await _automation.GetTasksAsync(token));
        SelectedTask ??= Tasks.FirstOrDefault();
    }

    private async Task LoadExecutionsAsync(CancellationToken token)
    {
        Executions = new ObservableCollection<AutomationExecutionView>(
            await _automation.GetExecutionsAsync(SelectedTask?.Id, 100, token));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunAsync(async token =>
        {
            await LoadTasksAsync(token);
            await LoadExecutionsAsync(token);
        }, "Automation data refreshed.");
    }

    [RelayCommand]
    private void NewTask()
    {
        SelectedTask = null;
        TaskName = "New maintenance task";
        TaskDescription = string.Empty;
        TaskEnabled = true;
        TriggerType = AutomationTriggerType.Manual;
        ActionType = AutomationActionType.CheckOnline;
        IntervalMinutes = 60;
        DailyTime = "02:00";
        ConfigServiceName = string.Empty;
        ConfigCommand = string.Empty;
        ConfigShell = CommandShell.PowerShell;
        ConfigMessage = string.Empty;
        ConfigThreshold = 10;
        TargetComputer = null;
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void EditTask()
    {
        if (SelectedTask is null)
        {
            return;
        }

        TaskName = SelectedTask.Name;
        TaskDescription = SelectedTask.Description ?? string.Empty;
        TaskEnabled = SelectedTask.Enabled;
        TriggerType = SelectedTask.TriggerType;
        ActionType = SelectedTask.ActionType;
        IntervalMinutes = SelectedTask.IntervalMinutes;
        DailyTime = SelectedTask.DailyTime ?? "02:00";
        TargetComputer = Computers.FirstOrDefault(c => SelectedTask.TargetName == c.Name);
        ConfigServiceName = string.Empty;
        ConfigCommand = string.Empty;
        ConfigMessage = string.Empty;
        ConfigThreshold = 10;
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void CancelTask() => IsEditorOpen = false;

    [RelayCommand]
    private async Task SaveTaskAsync()
    {
        if (string.IsNullOrWhiteSpace(TaskName))
        {
            Fail("A task name is required.");
            return;
        }

        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (ActionType)
        {
            case AutomationActionType.CheckService:
                if (string.IsNullOrWhiteSpace(ConfigServiceName))
                {
                    Fail("Enter the Windows service name to check.");
                    return;
                }
                config["serviceName"] = ConfigServiceName.Trim();
                break;

            case AutomationActionType.RunCommand:
                if (string.IsNullOrWhiteSpace(ConfigCommand))
                {
                    Fail("Enter the command to run.");
                    return;
                }
                config["command"] = ConfigCommand.Trim();
                config["shell"] = ConfigShell.ToString();
                config["timeoutSeconds"] = "120";
                break;

            case AutomationActionType.Notify:
                if (string.IsNullOrWhiteSpace(ConfigMessage))
                {
                    Fail("Enter the notification message.");
                    return;
                }
                config["message"] = ConfigMessage.Trim();
                break;

            case AutomationActionType.CheckDiskSpace:
                config["threshold"] = ConfigThreshold.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                break;
        }

        await RunAsync(async token =>
        {
            var task = new AutomationTask
            {
                Id = SelectedTask?.Id ?? Guid.Empty,
                Name = TaskName.Trim(),
                Description = string.IsNullOrWhiteSpace(TaskDescription) ? null : TaskDescription.Trim(),
                Enabled = TaskEnabled,
                TriggerType = TriggerType,
                ActionType = ActionType,
                TargetComputerId = TargetComputer?.Id,
                IntervalMinutes = TriggerType == AutomationTriggerType.Interval ? IntervalMinutes : null,
                DailyTime = TriggerType == AutomationTriggerType.Daily ? DailyTime.Trim() : null,
                ActionConfigJson = config.Count == 0 ? null : JsonSerializer.Serialize(config)
            };

            var result = await _automation.SaveTaskAsync(Actor, task, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The automation task could not be saved.");
            }

            IsEditorOpen = false;
            await LoadTasksAsync(token);
            await LoadExecutionsAsync(token);
        }, "Automation task saved.");
    }

    [RelayCommand]
    private async Task DeleteTaskAsync()
    {
        if (SelectedTask is null)
        {
            return;
        }

        if (!_dialogs.Confirm("Delete automation", $"Delete the automation '{SelectedTask.Name}'?", danger: true))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _automation.DeleteTaskAsync(Actor, SelectedTask.Id, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The automation task could not be deleted.");
            }

            SelectedTask = null;
            await LoadTasksAsync(token);
        }, "Automation task deleted.");
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync()
    {
        if (SelectedTask is null)
        {
            return;
        }

        var enable = !SelectedTask.Enabled;
        await RunAsync(async token =>
        {
            var result = await _automation.SetEnabledAsync(Actor, SelectedTask.Id, enable, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The task could not be updated.");
            }

            await LoadTasksAsync(token);
        }, enable ? "Automation enabled." : "Automation disabled.");
    }

    [RelayCommand]
    private async Task RunNowAsync()
    {
        if (SelectedTask is null)
        {
            return;
        }

        if (!_dialogs.Confirm("Run automation now",
                $"Run '{SelectedTask.Name}' immediately? It will target {(string.IsNullOrWhiteSpace(SelectedTask.TargetName) ? "all managed computers" : SelectedTask.TargetName)}.",
                danger: false))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var execution = await _automation.RunNowAsync(Actor, SelectedTask.Id, token);
            await LoadTasksAsync(token);
            await LoadExecutionsAsync(token);
            StatusMessage = $"'{execution.TaskName}' finished with {execution.Succeeded} success(es) and {execution.Failed} failure(s).";
            HasError = execution.Failed > 0;
        });
    }

    partial void OnSelectedTaskChanged(AutomationTaskView? value) => _ = LoadExecutionsAsync(PageToken);
}
