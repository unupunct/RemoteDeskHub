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

public sealed partial class ServicesViewModel : PageViewModel
{
    private readonly IServiceManagerService _services;
    private readonly IComputerService _computers;
    private readonly ISessionContext _session;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private ObservableCollection<ComputerSummary> computers = [];

    [ObservableProperty]
    private ComputerSummary? selectedComputer;

    [ObservableProperty]
    private ObservableCollection<RemoteServiceDto> services = [];

    [ObservableProperty]
    private ObservableCollection<RemoteServiceDto> filteredServices = [];

    [ObservableProperty]
    private RemoteServiceDto? selectedService;

    [ObservableProperty]
    private ObservableCollection<RemoteServiceDto> dependencies = [];

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string summaryText = "Select a computer to list its Windows services.";

    [ObservableProperty]
    private string selectedStartupType = "Manual";

    public ServicesViewModel(IServiceManagerService services, IComputerService computers,
        ISessionContext session, IDialogService dialogs)
    {
        _services = services;
        _computers = computers;
        _session = session;
        _dialogs = dialogs;
    }

    public override string Title => "Services";
    public override string Subtitle => "Start, stop and configure Windows services";

    public string Actor => _session.CurrentUser?.UserName ?? HubConstants.AuditActorSystem;
    public bool CanManage => _session.CurrentUser?.Can(Permission.ManageServices) == true;
    public IReadOnlyList<string> StartupTypes { get; } = ["Automatic", "AutomaticDelayedStart", "Manual", "Disabled"];

    public override async Task LoadAsync()
    {
        await RunAsync(async token =>
        {
            var data = await _computers.GetComputersAsync(cancellationToken: token);
            Computers = new ObservableCollection<ComputerSummary>(data);
            SelectedComputer ??= Computers.FirstOrDefault(c => c.IsOnline) ?? Computers.FirstOrDefault();
            await LoadServicesAsync(token);
        });
    }

    partial void OnSelectedComputerChanged(ComputerSummary? value) => _ = LoadServicesAsync(PageToken);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedServiceChanged(RemoteServiceDto? value)
    {
        Dependencies = [];
        if (value is not null)
        {
            SelectedStartupType = value.StartupType;
            _ = LoadDependenciesAsync(value.Name);
        }
    }

    private async Task LoadServicesAsync(CancellationToken token)
    {
        if (SelectedComputer is null)
        {
            Services = [];
            FilteredServices = [];
            return;
        }

        try
        {
            var data = await _services.GetServicesAsync(SelectedComputer.Id, token);
            Services = new ObservableCollection<RemoteServiceDto>(data.OrderBy(s => s.DisplayName));
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Fail($"Services could not be listed: {ex.Message}");
        }
    }

    private void ApplyFilter()
    {
        var query = Services.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(s =>
                s.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) ||
                s.DisplayName.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        FilteredServices = new ObservableCollection<RemoteServiceDto>(query);
        SummaryText = $"{FilteredServices.Count} of {Services.Count} service(s) · " +
                      $"{Services.Count(s => s.Status.Equals("Running", StringComparison.OrdinalIgnoreCase))} running";
    }

    private async Task LoadDependenciesAsync(string serviceName)
    {
        if (SelectedComputer is null)
        {
            return;
        }

        try
        {
            Dependencies = new ObservableCollection<RemoteServiceDto>(
                await _services.GetDependenciesAsync(SelectedComputer.Id, serviceName, PageToken));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Dependencies could not be loaded: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunAsync(token => LoadServicesAsync(token), "Service list refreshed.");
    }

    [RelayCommand]
    private async Task StartAsync() => await ExecuteActionAsync("start");

    [RelayCommand]
    private async Task StopAsync() => await ExecuteActionAsync("stop");

    [RelayCommand]
    private async Task RestartAsync() => await ExecuteActionAsync("restart");

    [RelayCommand]
    private async Task PauseAsync() => await ExecuteActionAsync("pause");

    [RelayCommand]
    private async Task ResumeAsync() => await ExecuteActionAsync("resume");

    [RelayCommand]
    private async Task ApplyStartupTypeAsync() => await ExecuteActionAsync("startup");

    private async Task ExecuteActionAsync(string action)
    {
        if (SelectedComputer is null || SelectedService is null)
        {
            return;
        }

        var service = SelectedService;
        var destructive = action is "stop" or "restart" or "pause";
        var critical = CriticalServices.IsCritical(service.Name);
        if (destructive && !_dialogs.Confirm($"{action} service",
                $"{(critical ? "WARNING: this is a critical Windows service. Stopping it may disconnect or destabilise the computer.\n\n" : string.Empty)}" +
                $"Run '{action}' against '{service.DisplayName}' ({service.Name}) on {SelectedComputer.Name}?",
                danger: critical))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _services.ExecuteActionAsync(Actor, SelectedComputer.Id, service.Name,
                action, action == "startup" ? SelectedStartupType : null, confirmed: true, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The service action failed.");
            }

            await LoadServicesAsync(token);
        }, $"Service '{service.DisplayName}' action '{action}' completed.");
    }
}
