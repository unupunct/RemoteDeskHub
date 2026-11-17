using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Desktop.Services;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Desktop.ViewModels;

public sealed partial class ProcessesViewModel : PageViewModel
{
    private readonly IProcessService _processes;
    private readonly IComputerService _computers;
    private readonly ISessionContext _session;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private ObservableCollection<ComputerSummary> computers = [];

    [ObservableProperty]
    private ComputerSummary? selectedComputer;

    [ObservableProperty]
    private ObservableCollection<RemoteProcessDto> processes = [];

    [ObservableProperty]
    private ObservableCollection<RemoteProcessDto> filteredProcesses = [];

    [ObservableProperty]
    private RemoteProcessDto? selectedProcess;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string summaryText = "Select a computer to list its processes.";

    [ObservableProperty]
    private bool showProtected;

    public ProcessesViewModel(IProcessService processes, IComputerService computers,
        ISessionContext session, IDialogService dialogs)
    {
        _processes = processes;
        _computers = computers;
        _session = session;
        _dialogs = dialogs;
    }

    public override string Title => "Processes";
    public override string Subtitle => "Inspect and terminate processes on managed computers";

    public string Actor => _session.CurrentUser?.UserName ?? HubConstants.AuditActorSystem;
    public bool CanManage => _session.CurrentUser?.Can(Permission.ManageProcesses) == true;

    public override async Task LoadAsync()
    {
        await RunAsync(async token =>
        {
            var data = await _computers.GetComputersAsync(cancellationToken: token);
            Computers = new ObservableCollection<ComputerSummary>(data);
            SelectedComputer ??= Computers.FirstOrDefault(c => c.IsOnline) ?? Computers.FirstOrDefault();
            await LoadProcessesAsync(token);
        });
    }

    partial void OnSelectedComputerChanged(ComputerSummary? value) => _ = LoadProcessesAsync(PageToken);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnShowProtectedChanged(bool value) => ApplyFilter();

    private async Task LoadProcessesAsync(CancellationToken token)
    {
        if (SelectedComputer is null)
        {
            Processes = [];
            FilteredProcesses = [];
            return;
        }

        try
        {
            var data = await _processes.GetProcessesAsync(SelectedComputer.Id, token);
            Processes = new ObservableCollection<RemoteProcessDto>(data.OrderByDescending(p => p.CpuPercent));
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Fail($"Processes could not be listed: {ex.Message}");
        }
    }

    private void ApplyFilter()
    {
        var query = Processes.AsEnumerable();
        if (!ShowProtected)
        {
            query = query.Where(p => !p.IsProtected);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(p =>
                p.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) ||
                p.Pid.ToString().Contains(SearchText.Trim(), StringComparison.Ordinal) ||
                (p.UserName?.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) ?? false));
        }

        FilteredProcesses = new ObservableCollection<RemoteProcessDto>(query);
        SummaryText = $"{FilteredProcesses.Count} of {Processes.Count} process(es)" +
                      (ShowProtected ? string.Empty : " · protected processes hidden");
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunAsync(token => LoadProcessesAsync(token), "Process list refreshed.");
    }

    [RelayCommand]
    private async Task TerminateAsync()
    {
        if (SelectedComputer is null || SelectedProcess is null)
        {
            return;
        }

        if (SelectedProcess.IsProtected || ProtectedProcesses.IsProtected(SelectedProcess.Name))
        {
            Fail($"'{SelectedProcess.Name}' is a protected system process and cannot be terminated through RemoteDesk Hub.");
            return;
        }

        if (!_dialogs.Confirm("Terminate process",
                $"Terminate '{SelectedProcess.Name}' (PID {SelectedProcess.Pid}) on {SelectedComputer.Name}?\n\n" +
                "Unsaved work in that application may be lost. The action is audited.",
                danger: true))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var outcome = await _processes.TerminateAsync(Actor, SelectedComputer.Id, SelectedProcess.Pid,
                SelectedProcess.Name, confirmed: true, token);
            if (!outcome.Success)
            {
                throw new InvalidOperationException(outcome.Message);
            }

            await LoadProcessesAsync(token);
        }, "Process terminated.");
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (SelectedComputer is null)
        {
            return;
        }

        await RunAsync(async token =>
        {
            var report = await _processes.ExportAsync(SelectedComputer.Id, "csv", token);
            _dialogs.SaveReport(report);
        }, "Process list exported.");
    }
}
