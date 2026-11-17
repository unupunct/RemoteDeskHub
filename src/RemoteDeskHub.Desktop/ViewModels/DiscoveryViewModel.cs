using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Desktop.Services;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Desktop.ViewModels;

public sealed partial class DiscoveryViewModel : PageViewModel
{
    private readonly IDiscoveryService _discovery;
    private readonly IComputerService _computers;
    private readonly ISettingsService _settings;
    private readonly ISessionContext _session;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private ObservableCollection<DiscoveryView> devices = [];

    [ObservableProperty]
    private ObservableCollection<string> subnets = [];

    [ObservableProperty]
    private ObservableCollection<GroupView> groups = [];

    [ObservableProperty]
    private string? selectedSubnet;

    [ObservableProperty]
    private DiscoveryView? selectedDevice;

    [ObservableProperty]
    private GroupView? targetGroup;

    [ObservableProperty]
    private bool trustOnAdd;

    [ObservableProperty]
    private int scanModeIndex;

    [ObservableProperty]
    private string rangeText = string.Empty;

    [ObservableProperty]
    private string cidrText = string.Empty;

    [ObservableProperty]
    private int timeoutMs = 800;

    [ObservableProperty]
    private int concurrency = 24;

    private IReadOnlyList<int> _probePorts = [HubConstants.DefaultAgentPort, 445, 3389];

    [ObservableProperty]
    private string progressText = string.Empty;

    [ObservableProperty]
    private string lastResultText = string.Empty;

    [ObservableProperty]
    private bool isScanning;

    [ObservableProperty]
    private string summaryText = string.Empty;

    public DiscoveryViewModel(IDiscoveryService discovery, IComputerService computers, ISettingsService settings,
        ISessionContext session, IDialogService dialogs)
    {
        _discovery = discovery;
        _computers = computers;
        _settings = settings;
        _session = session;
        _dialogs = dialogs;
    }

    public override string Title => "Discovery";
    public override string Subtitle => "Find computers and agents on private LAN subnets";

    public string Actor => _session.CurrentUser?.UserName ?? HubConstants.AuditActorSystem;
    public bool CanDiscover => _session.CurrentUser?.Can(Permission.ManageComputers) == true;
    public bool IsLocalMode => ScanModeIndex == 0;
    public bool IsRangeMode => ScanModeIndex == 1;
    public bool IsCidrMode => ScanModeIndex == 2;

    public override async Task LoadAsync()
    {
        await RunAsync(async token =>
        {
            TimeoutMs = await _settings.GetAsync(SettingsKeys.DiscoveryTimeoutMs, 800, token);
            Concurrency = await _settings.GetAsync(SettingsKeys.DiscoveryConcurrency, 24, token);
            var ports = await _settings.GetAsync(SettingsKeys.DiscoveryProbePorts, $"{HubConstants.DefaultAgentPort},445,3389", token);
            _probePorts = (ports ?? string.Empty)
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => int.TryParse(p, out var port) ? port : 0)
                .Where(p => p is > 0 and < 65536)
                .Distinct()
                .Take(12)
                .ToList();
            if (_probePorts.Count == 0)
            {
                _probePorts = [HubConstants.DefaultAgentPort, 445, 3389];
            }
            Subnets = new ObservableCollection<string>(await _discovery.GetLocalSubnetsAsync(token));
            SelectedSubnet ??= Subnets.FirstOrDefault();
            var groups = await _computers.GetGroupsAsync(token);
            Groups = new ObservableCollection<GroupView>(groups);
            TargetGroup ??= Groups.FirstOrDefault();
            await LoadDevicesAsync(token);
        });
    }

    partial void OnScanModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsLocalMode));
        OnPropertyChanged(nameof(IsRangeMode));
        OnPropertyChanged(nameof(IsCidrMode));
    }

    partial void OnSelectedSubnetChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            CidrText = value;
        }
    }

    private async Task LoadDevicesAsync(CancellationToken token)
    {
        var data = await _discovery.GetDevicesAsync(token);
        Devices = new ObservableCollection<DiscoveryView>(data);
        SummaryText = data.Count == 0
            ? "No discovery results yet. Run a scan to find devices."
            : $"{data.Count} device(s) · {data.Count(d => d.HasAgent)} with RemoteDesk Agent · {data.Count(d => d.IsRegistered)} in inventory";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunAsync(async token =>
        {
            Subnets = new ObservableCollection<string>(await _discovery.GetLocalSubnetsAsync(token));
            await LoadDevicesAsync(token);
        }, "Discovery results refreshed.");
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        var mode = ScanModeIndex switch { 1 => "range", 2 => "cidr", _ => "local" };
        if (mode == "range" && string.IsNullOrWhiteSpace(RangeText))
        {
            Fail("Enter an address range such as 192.168.1.1-192.168.1.254.");
            return;
        }

        if (mode == "cidr" && string.IsNullOrWhiteSpace(CidrText))
        {
            Fail("Enter a CIDR range such as 192.168.1.0/24.");
            return;
        }

        if (!_dialogs.Confirm("Scan the network",
                "RemoteDesk Hub will probe private LAN addresses for open management ports and agent endpoints. " +
                "Only private IPv4 ranges are permitted. Continue?", danger: false))
        {
            return;
        }

        IsScanning = true;
        ProgressText = "Preparing scan…";
        HasError = false;
        try
        {
            var request = new DiscoveryScanRequest
            {
                Mode = mode,
                Range = mode == "range" ? RangeText.Trim() : null,
                Cidr = mode == "cidr" ? CidrText.Trim() : null,
                IncludeLocalSubnet = mode == "local",
                TimeoutMs = TimeoutMs,
                Concurrency = Concurrency,
                ProbePorts = _probePorts
            };

            var progress = new Progress<string>(message => ProgressText = message);
            var result = await _discovery.ScanAsync(request, progress, PageToken);
            LastResultText = $"{result.DevicesFound} device(s) found in {result.Duration.TotalSeconds:0.0}s " +
                             $"({result.NewDevices} new, {result.AgentsFound} agents).";
            await LoadDevicesAsync(PageToken);
            StatusMessage = $"Scan complete: {result.HostsScanned} addresses probed.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            Fail($"The scan failed: {ex.Message}");
        }
        finally
        {
            IsScanning = false;
            ProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task AddToInventoryAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        var friendlyName = _dialogs.Prompt("Add to inventory",
            $"Friendly name for {SelectedDevice.DisplayName} (leave blank to use the detected name)", SelectedDevice.FriendlyName);
        if (friendlyName is null)
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _discovery.AddToInventoryAsync(Actor, SelectedDevice.Id,
                string.IsNullOrWhiteSpace(friendlyName) ? null : friendlyName.Trim(),
                TargetGroup?.Id, SelectedDevice.Notes, TrustOnAdd, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The device could not be added.");
            }

            await LoadDevicesAsync(token);
        }, "Device added to the computer inventory.");
    }

    [RelayCommand]
    private async Task ToggleTrustAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        var trust = !SelectedDevice.IsTrusted;
        await RunAsync(async token =>
        {
            var result = await _discovery.UpdateDeviceAsync(Actor, SelectedDevice.Id, SelectedDevice.FriendlyName,
                SelectedDevice.Notes, trust, TargetGroup?.Id, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "Trust could not be changed.");
            }

            await LoadDevicesAsync(token);
        }, trust ? "Device marked as trusted." : "Device trust removed.");
    }

    [RelayCommand]
    private async Task EditNotesAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        var notes = _dialogs.Prompt("Device notes", $"Notes for {SelectedDevice.DisplayName}", SelectedDevice.Notes, multiline: true);
        if (notes is null)
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _discovery.UpdateDeviceAsync(Actor, SelectedDevice.Id, SelectedDevice.FriendlyName,
                notes, SelectedDevice.IsTrusted, TargetGroup?.Id, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "Notes could not be saved.");
            }

            await LoadDevicesAsync(token);
        }, "Device notes saved.");
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        if (!_dialogs.Confirm("Remove discovery record",
                $"Remove the discovery record for {SelectedDevice.DisplayName}? Inventory computers are not affected.",
                danger: true))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _discovery.RemoveDeviceAsync(Actor, SelectedDevice.Id, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The record could not be removed.");
            }

            await LoadDevicesAsync(token);
        }, "Discovery record removed.");
    }

    [RelayCommand]
    private void UseSubnet()
    {
        if (!string.IsNullOrWhiteSpace(SelectedSubnet))
        {
            ScanModeIndex = 2;
            CidrText = SelectedSubnet;
        }
    }
}
