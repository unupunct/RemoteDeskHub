using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Desktop.Controls;
using RemoteDeskHub.Desktop.Services;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Desktop.ViewModels;

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly IComputerService _computers;
    private readonly IMonitoringService _monitoring;
    private readonly IDiscoveryService _discovery;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private DashboardSnapshot? snapshot;

    [ObservableProperty]
    private ObservableCollection<ComputerSummary> highlightedComputers = [];

    [ObservableProperty]
    private IReadOnlyList<ChartSeries> cpuSeries = [];

    [ObservableProperty]
    private IReadOnlyList<ChartSeries> memorySeries = [];

    [ObservableProperty]
    private double cpuAverage;

    [ObservableProperty]
    private double memoryAverage;

    [ObservableProperty]
    private double diskAverage;

    [ObservableProperty]
    private string lastUpdated = "Never";

    public DashboardViewModel(IComputerService computers, IMonitoringService monitoring,
        IDiscoveryService discovery, IDialogService dialogs)
    {
        _computers = computers;
        _monitoring = monitoring;
        _discovery = discovery;
        _dialogs = dialogs;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += async (_, _) =>
        {
            if (!IsBusy)
            {
                await LoadCoreAsync(quiet: true);
            }
        };
    }

    public override string Title => "Dashboard";
    public override string Subtitle => "Live health and activity across your managed LAN";

    public override async Task LoadAsync()
    {
        _timer.Start();
        IsBusy = true;
        try
        {
            await LoadCoreAsync(quiet: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public override void OnNavigatedFrom()
    {
        _timer.Stop();
        base.OnNavigatedFrom();
    }

    private async Task LoadCoreAsync(bool quiet)
    {
        try
        {
            var snapshotData = await _computers.GetDashboardAsync(PageToken);
            Snapshot = snapshotData;
            HighlightedComputers = new ObservableCollection<ComputerSummary>(
                snapshotData.Computers
                    .OrderByDescending(c => c.Status == ComputerStatus.Critical)
                    .ThenByDescending(c => c.Status == ComputerStatus.Warning)
                    .ThenByDescending(c => c.Status == ComputerStatus.Online)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(12));

            var online = snapshotData.Computers.Where(c => c.IsOnline).ToList();
            if (online.Count > 0)
            {
                CpuAverage = Math.Round(online.Average(c => c.CpuPercent), 1);
                MemoryAverage = Math.Round(online.Average(c => c.MemoryPercent), 1);
                DiskAverage = Math.Round(online.Average(c => c.DiskPercent), 1);
            }
            else
            {
                CpuAverage = MemoryAverage = DiskAverage = 0;
            }

            await BuildChartsAsync(online);
            LastUpdated = DateTime.Now.ToString("HH:mm:ss");
            if (!quiet)
            {
                StatusMessage = snapshotData.TotalComputers == 0
                    ? "No computers yet. Run a discovery scan or enroll an agent."
                    : $"Dashboard updated at {LastUpdated}.";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Fail($"The dashboard could not be refreshed: {ex.Message}");
        }
    }

    private async Task BuildChartsAsync(IReadOnlyList<ComputerSummary> online)
    {
        if (online.Count == 0)
        {
            CpuSeries = [];
            MemorySeries = [];
            return;
        }

        var ids = online.Take(6).Select(c => c.Id).ToList();
        var to = DateTime.UtcNow;
        var from = to.AddHours(-2);
        var data = await _monitoring.GetSeriesAsync(ids, from, to, 60, PageToken);
        var palette = new[]
        {
            System.Windows.Media.Color.FromRgb(59, 130, 246),
            System.Windows.Media.Color.FromRgb(34, 211, 238),
            System.Windows.Media.Color.FromRgb(34, 197, 94),
            System.Windows.Media.Color.FromRgb(245, 158, 11),
            System.Windows.Media.Color.FromRgb(168, 85, 247),
            System.Windows.Media.Color.FromRgb(239, 68, 68)
        };

        CpuSeries = data.Take(palette.Length).Select((s, i) => new ChartSeries
        {
            Name = s.ComputerName,
            Color = palette[i],
            Values = s.Samples.Select(x => x.CpuPercent).ToList()
        }).ToList();

        MemorySeries = data.Take(palette.Length).Select((s, i) => new ChartSeries
        {
            Name = s.ComputerName,
            Color = palette[i],
            Values = s.Samples.Select(x => x.MemoryPercent).ToList()
        }).ToList();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunAsync(async token =>
        {
            await _computers.GetDashboardAsync(token);
            await LoadCoreAsync(quiet: false);
        }, "Dashboard refreshed.");
    }

    [RelayCommand]
    private async Task ScanNetworkAsync()
    {
        if (!_dialogs.Confirm("Scan the local network",
                "RemoteDesk Hub will probe the local subnet for computers and open management ports. " +
                "Only private LAN addresses are scanned. Continue?", danger: false))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _discovery.ScanAsync(new DiscoveryScanRequest
            {
                Mode = "local",
                IncludeLocalSubnet = true
            }, null, token);
            await LoadCoreAsync(quiet: true);
            _dialogs.Info("Discovery complete",
                $"Scanned {result.HostsScanned} addresses and found {result.DevicesFound} devices " +
                $"({result.NewDevices} new, {result.AgentsFound} with RemoteDesk Agent). " +
                $"Completed in {result.Duration.TotalSeconds:0.0} seconds.");
        }, "Discovery scan completed.");
    }
}
