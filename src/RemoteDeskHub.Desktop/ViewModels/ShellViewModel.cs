using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Desktop.ViewModels;

public sealed class NavItem
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Glyph { get; init; }
    public required Permission RequiredPermission { get; init; }
    public string? BadgeKey { get; init; }
}

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly ISessionContext _session;
    private readonly IHubServer _hub;
    private readonly IEnrollmentService _enrollment;
    private readonly IDiscoveryService _discovery;
    private readonly ISettingsService _settings;
    private readonly Dictionary<string, PageViewModel> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer;
    private DateTime _lastAutoScanUtc = DateTime.MinValue;
    private bool _autoScanRunning;
    private PageViewModel? _subscribedPage;
    private PropertyChangedEventHandler? _pagePropertyChanged;

    [ObservableProperty]
    private PageViewModel? currentPage;

    [ObservableProperty]
    private NavItem? selectedNav;

    [ObservableProperty]
    private bool isSidebarCollapsed;

    [ObservableProperty]
    private double sidebarWidth = 250;

    [ObservableProperty]
    private string hubStatusText = "Hub starting";

    [ObservableProperty]
    private bool hubOnline;

    [ObservableProperty]
    private int pendingApprovals;

    [ObservableProperty]
    private bool isRefreshingStatus;

    public ShellViewModel(IServiceProvider services, ISessionContext session, IHubServer hub,
        IEnrollmentService enrollment, IDiscoveryService discovery, ISettingsService settings)
    {
        _services = services;
        _session = session;
        _hub = hub;
        _enrollment = enrollment;
        _discovery = discovery;
        _settings = settings;

        NavItems = FilterNavItems(
        [
            new NavItem { Key = "dashboard", Label = "Dashboard", Glyph = "\uE80F", RequiredPermission = Permission.ViewDashboard },
            new NavItem { Key = "computers", Label = "Computers", Glyph = "\uE7F4", RequiredPermission = Permission.ViewComputers },
            new NavItem { Key = "discovery", Label = "Discovery", Glyph = "\uE721", RequiredPermission = Permission.ViewComputers },
            new NavItem { Key = "monitoring", Label = "Monitoring", Glyph = "\uE9D9", RequiredPermission = Permission.ViewMonitoring },
            new NavItem { Key = "inventory", Label = "Inventory", Glyph = "\uE7B8", RequiredPermission = Permission.ViewComputers },
            new NavItem { Key = "processes", Label = "Processes", Glyph = "\uE9F5", RequiredPermission = Permission.ViewComputers },
            new NavItem { Key = "services", Label = "Services", Glyph = "\uE90F", RequiredPermission = Permission.ViewComputers },
            new NavItem { Key = "commands", Label = "Remote Commands", Glyph = "\uE756", RequiredPermission = Permission.RunCommands },
            new NavItem { Key = "files", Label = "File Transfer", Glyph = "\uE8B7", RequiredPermission = Permission.TransferFiles },
            new NavItem { Key = "assistance", Label = "Remote Assistance", Glyph = "\uE8AF", RequiredPermission = Permission.LaunchRemoteAssistance },
            new NavItem { Key = "automation", Label = "Automation", Glyph = "\uE945", RequiredPermission = Permission.ManageAutomation },
            new NavItem { Key = "reports", Label = "Reports", Glyph = "\uE9F9", RequiredPermission = Permission.ViewReports },
            new NavItem { Key = "audit", Label = "Audit Log", Glyph = "\uE81C", RequiredPermission = Permission.ViewAudit },
            new NavItem { Key = "users", Label = "Users & Roles", Glyph = "\uE716", RequiredPermission = Permission.ManageUsers },
            new NavItem { Key = "settings", Label = "Settings", Glyph = "\uE713", RequiredPermission = Permission.ManageSettings }
        ]);

        SelectedNav = NavItems[0];
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        _timer.Start();
        _ = RefreshStatusAsync();
    }

    public ObservableCollection<NavItem> NavItems { get; }

    private ObservableCollection<NavItem> FilterNavItems(IEnumerable<NavItem> items)
    {
        var role = _session.CurrentUser?.Role ?? UserRole.Viewer;
        return new ObservableCollection<NavItem>(items.Where(i => Permissions.HasPermission(role, i.RequiredPermission)));
    }

    public string UserName => _session.CurrentUser?.UserName ?? "Not signed in";
    public string RoleText => _session.CurrentUser is null ? string.Empty : Permissions.Describe(_session.CurrentUser.Role);
    public string UserInitials => string.IsNullOrWhiteSpace(UserName) ? "?" : UserName[..Math.Min(2, UserName.Length)].ToUpperInvariant();
    public string VersionText => $"RemoteDesk Hub {HubConstants.ProductVersion}";
    public string PageTitle => CurrentPage?.Title ?? "Dashboard";
    public string PageSubtitle => CurrentPage?.Subtitle ?? string.Empty;
    public string? PageStatus => CurrentPage?.StatusMessage;
    public bool PageHasError => CurrentPage?.HasError == true;
    public bool PageIsBusy => CurrentPage?.IsBusy == true;
    public Visibility ApprovalsVisibility => PendingApprovals > 0 ? Visibility.Visible : Visibility.Collapsed;

    public event EventHandler? SignOutRequested;

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is not null)
        {
            _ = NavigateAsync(value.Key);
        }
    }

    partial void OnCurrentPageChanged(PageViewModel? value)
    {
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(PageStatus));
        OnPropertyChanged(nameof(PageHasError));
        OnPropertyChanged(nameof(PageIsBusy));

        // Keep exactly one subscription per displayed page. Pages are cached, so re-subscribing
        // on every navigation would multiply change notifications and retain stale handlers.
        if (_subscribedPage is not null && _pagePropertyChanged is not null)
        {
            _subscribedPage.PropertyChanged -= _pagePropertyChanged;
        }

        _subscribedPage = value;
        _pagePropertyChanged = value is null
            ? null
            : (_, args) =>
            {
                if (args.PropertyName is nameof(PageViewModel.StatusMessage) or nameof(PageViewModel.HasError)
                    or nameof(PageViewModel.IsBusy))
                {
                    OnPropertyChanged(nameof(PageStatus));
                    OnPropertyChanged(nameof(PageHasError));
                    OnPropertyChanged(nameof(PageIsBusy));
                }
            };

        if (_subscribedPage is not null && _pagePropertyChanged is not null)
        {
            _subscribedPage.PropertyChanged += _pagePropertyChanged;
        }
    }

    partial void OnPendingApprovalsChanged(int value) => OnPropertyChanged(nameof(ApprovalsVisibility));

    [RelayCommand]
    private async Task NavigateAsync(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!_cache.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "dashboard" => _services.GetRequiredService<DashboardViewModel>(),
                "computers" => _services.GetRequiredService<ComputersViewModel>(),
                "discovery" => _services.GetRequiredService<DiscoveryViewModel>(),
                "monitoring" => _services.GetRequiredService<MonitoringViewModel>(),
                "inventory" => _services.GetRequiredService<InventoryViewModel>(),
                "processes" => _services.GetRequiredService<ProcessesViewModel>(),
                "services" => _services.GetRequiredService<ServicesViewModel>(),
                "commands" => _services.GetRequiredService<CommandsViewModel>(),
                "files" => _services.GetRequiredService<FileTransferViewModel>(),
                "assistance" => _services.GetRequiredService<RemoteAssistanceViewModel>(),
                "automation" => _services.GetRequiredService<AutomationViewModel>(),
                "reports" => _services.GetRequiredService<ReportsViewModel>(),
                "audit" => _services.GetRequiredService<AuditLogViewModel>(),
                "users" => _services.GetRequiredService<UsersViewModel>(),
                "settings" => _services.GetRequiredService<SettingsViewModel>(),
                _ => null
            };

            if (page is null)
            {
                return;
            }

            _cache[key] = page;
        }

        if (CurrentPage is not null && !ReferenceEquals(CurrentPage, page))
        {
            CurrentPage.OnNavigatedFrom();
        }

        CurrentPage = page;
        await page.LoadAsync();
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        SidebarWidth = IsSidebarCollapsed ? 76 : 250;
    }

    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        if (IsRefreshingStatus)
        {
            return;
        }

        IsRefreshingStatus = true;
        try
        {
            HubOnline = _hub.IsRunning;
            HubStatusText = HubOnline ? $"Hub online · port {_hub.BoundPort}" : "Hub offline";
            if (_session.CurrentUser?.Can(Permission.EnrollAgents) == true)
            {
                var pending = await _enrollment.GetPendingAgentsAsync();
                PendingApprovals = pending.Count;
            }
            else
            {
                PendingApprovals = 0;
            }

            await MaybeAutoScanAsync();
        }
        catch
        {
            HubStatusText = "Hub status unavailable";
        }
        finally
        {
            IsRefreshingStatus = false;
        }
    }

    [RelayCommand]
    private void SignOut()
    {
        SignOutRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Runs a background local-subnet discovery scan when the administrator configured an
    /// automatic interval. Manual scans from the Discovery page are unaffected.
    /// </summary>
    private async Task MaybeAutoScanAsync()
    {
        if (_autoScanRunning)
        {
            return;
        }

        var interval = await _settings.GetAsync(SettingsKeys.DiscoveryIntervalMinutes, 0);
        if (interval <= 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (_lastAutoScanUtc != DateTime.MinValue && now - _lastAutoScanUtc < TimeSpan.FromMinutes(interval))
        {
            return;
        }

        _autoScanRunning = true;
        try
        {
            _lastAutoScanUtc = now;
            var timeout = await _settings.GetAsync(SettingsKeys.DiscoveryTimeoutMs, 800);
            var concurrency = await _settings.GetAsync(SettingsKeys.DiscoveryConcurrency, 24);
            var ports = await _settings.GetAsync(SettingsKeys.DiscoveryProbePorts,
                $"{HubConstants.DefaultAgentPort},445,3389");
            var probePorts = (ports ?? string.Empty)
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => int.TryParse(p, out var port) ? port : 0)
                .Where(p => p is > 0 and < 65536)
                .Distinct()
                .Take(12)
                .ToList();

            await _discovery.ScanAsync(new DiscoveryScanRequest
            {
                Mode = "local",
                IncludeLocalSubnet = true,
                TimeoutMs = timeout,
                Concurrency = concurrency,
                ProbePorts = probePorts
            }, progress: null);
        }
        catch
        {
            // Automatic scans are best-effort; the Discovery page shows errors for manual scans.
        }
        finally
        {
            _autoScanRunning = false;
        }
    }

    public void SignOutForInactivity()
    {
        SignOut();
    }

    public void Stop()
    {
        _timer.Stop();
    }
}
