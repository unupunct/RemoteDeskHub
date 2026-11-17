using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Desktop.ViewModels;

public sealed partial class InventoryViewModel : PageViewModel
{
    private readonly IInventoryService _inventory;
    private readonly IComputerService _computers;
    private readonly ISessionContext _session;

    [ObservableProperty]
    private ObservableCollection<ComputerSummary> computers = [];

    [ObservableProperty]
    private ComputerSummary? selectedComputer;

    [ObservableProperty]
    private ComputerSummary? compareComputer;

    [ObservableProperty]
    private HardwareInventoryDto? hardware;

    [ObservableProperty]
    private string hardwareCollectedText = "Not collected";

    [ObservableProperty]
    private ObservableCollection<SoftwareInventoryItem> software = [];

    [ObservableProperty]
    private ObservableCollection<SoftwareInventoryItem> comparison = [];

    [ObservableProperty]
    private ObservableCollection<string> publishers = [];

    [ObservableProperty]
    private string? selectedPublisher;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string softwareSummary = "Select a computer to view installed software.";

    [ObservableProperty]
    private string comparisonSummary = "Choose a second computer to compare software.";

    [ObservableProperty]
    private int softwareCount;

    public InventoryViewModel(IInventoryService inventory, IComputerService computers, ISessionContext session)
    {
        _inventory = inventory;
        _computers = computers;
        _session = session;
    }

    public override string Title => "Inventory";
    public override string Subtitle => "Hardware, software and change comparison";

    public string Actor => _session.CurrentUser?.UserName ?? HubConstants.AuditActorSystem;
    public bool CanManage => _session.CurrentUser?.Can(Permission.ManageComputers) == true;

    public override async Task LoadAsync()
    {
        await RunAsync(async token =>
        {
            var data = await _computers.GetComputersAsync(cancellationToken: token);
            Computers = new ObservableCollection<ComputerSummary>(data);
            SelectedComputer ??= Computers.FirstOrDefault(c => c.IsOnline) ?? Computers.FirstOrDefault();
            CompareComputer ??= Computers.FirstOrDefault(c => c.Id != SelectedComputer?.Id);
            await LoadSelectedAsync(token);
        });
    }

    partial void OnSelectedComputerChanged(ComputerSummary? value)
    {
        _ = LoadSelectedAsync(PageToken);
    }

    private async Task LoadSelectedAsync(CancellationToken token)
    {
        if (SelectedComputer is null)
        {
            Hardware = null;
            Software = [];
            return;
        }

        try
        {
            Hardware = await _inventory.GetHardwareAsync(SelectedComputer.Id, token);
            HardwareCollectedText = Hardware?.CollectedUtc is { } collected
                ? $"Collected {collected.LocalDateTime:yyyy-MM-dd HH:mm}"
                : "Hardware inventory has not been collected yet.";

            Publishers = new ObservableCollection<string>(await _inventory.GetPublishersAsync(SelectedComputer.Id, token));
            SoftwareCount = await _inventory.GetSoftwareCountAsync(SelectedComputer.Id, token);
            await LoadSoftwareAsync(token);
        }
        catch (Exception ex)
        {
            Fail($"Inventory could not be loaded: {ex.Message}");
        }
    }

    private async Task LoadSoftwareAsync(CancellationToken token)
    {
        if (SelectedComputer is null)
        {
            return;
        }

        var data = await _inventory.GetSoftwareAsync(SelectedComputer.Id,
            string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            string.IsNullOrWhiteSpace(SelectedPublisher) ? null : SelectedPublisher, token);
        Software = new ObservableCollection<SoftwareInventoryItem>(data);
        SoftwareSummary = $"{data.Count} installed item(s) shown · {SoftwareCount} recorded for this computer";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunAsync(async token =>
        {
            await LoadSelectedAsync(token);
            await LoadComparisonAsync(token);
        }, "Inventory refreshed.");
    }

    [RelayCommand]
    private async Task SearchSoftwareAsync()
    {
        await RunAsync(token => LoadSoftwareAsync(token));
    }

    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        SearchText = string.Empty;
        SelectedPublisher = null;
        await RunAsync(token => LoadSoftwareAsync(token));
    }

    [RelayCommand]
    private async Task RefreshHardwareAsync()
    {
        if (SelectedComputer is null)
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _inventory.RefreshHardwareAsync(Actor, SelectedComputer.Id, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "Hardware inventory refresh failed.");
            }

            Hardware = await _inventory.GetHardwareAsync(SelectedComputer.Id, token);
            HardwareCollectedText = Hardware?.CollectedUtc is { } collected
                ? $"Collected {collected.LocalDateTime:yyyy-MM-dd HH:mm}"
                : "Hardware inventory has not been collected yet.";
        }, "Hardware inventory refreshed.");
    }

    [RelayCommand]
    private async Task RefreshSoftwareAsync()
    {
        if (SelectedComputer is null)
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _inventory.RefreshSoftwareAsync(Actor, SelectedComputer.Id, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "Software inventory refresh failed.");
            }

            await LoadSelectedAsync(token);
        }, "Software inventory refreshed.");
    }

    [RelayCommand]
    private async Task CompareAsync()
    {
        if (SelectedComputer is null || CompareComputer is null || SelectedComputer.Id == CompareComputer.Id)
        {
            Fail("Select two different computers to compare their software.");
            return;
        }

        await RunAsync(token => LoadComparisonAsync(token), "Software comparison complete.");
    }

    private async Task LoadComparisonAsync(CancellationToken token)
    {
        if (SelectedComputer is null || CompareComputer is null || SelectedComputer.Id == CompareComputer.Id)
        {
            Comparison = [];
            ComparisonSummary = "Choose a second computer to compare software.";
            return;
        }

        var diff = await _inventory.CompareSoftwareAsync([SelectedComputer.Id, CompareComputer.Id], token);
        Comparison = new ObservableCollection<SoftwareInventoryItem>(diff);
        ComparisonSummary = diff.Count == 0
            ? "The two computers have identical recorded software inventories."
            : $"{diff.Count} software difference(s) between {SelectedComputer.Name} and {CompareComputer.Name}.";
    }
}
