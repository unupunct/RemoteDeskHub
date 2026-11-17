using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Desktop.Controls;
using RemoteDeskHub.Desktop.Services;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Desktop.ViewModels;

public sealed partial class MonitoringViewModel : PageViewModel
{
    private readonly IMonitoringService _monitoring;
    private readonly IComputerService _computers;
    private readonly ISessionContext _session;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private ObservableCollection<ComputerSummary> computers = [];

    [ObservableProperty]
    private ComputerSummary? selectedComputer;

    [ObservableProperty]
    private int rangeHoursIndex = 2;

    [ObservableProperty]
    private IReadOnlyList<ChartSeries> cpuSeries = [];

    [ObservableProperty]
    private IReadOnlyList<ChartSeries> memorySeries = [];

    [ObservableProperty]
    private IReadOnlyList<ChartSeries> diskSeries = [];

    [ObservableProperty]
    private AgentMetricsDto? liveMetrics;

    [ObservableProperty]
    private ObservableCollection<AlertView> alerts = [];

    [ObservableProperty]
    private AlertView? selectedAlert;

    [ObservableProperty]
    private bool activeAlertsOnly = true;

    [ObservableProperty]
    private ObservableCollection<AlertRuleView> rules = [];

    [ObservableProperty]
    private AlertRuleView? selectedRule;

    [ObservableProperty]
    private bool isRuleEditorOpen;

    [ObservableProperty]
    private string ruleName = string.Empty;

    [ObservableProperty]
    private AlertMetricType ruleMetric = AlertMetricType.CpuUsage;

    [ObservableProperty]
    private double ruleThreshold = 90;

    [ObservableProperty]
    private AlertSeverity ruleSeverity = AlertSeverity.Warning;

    [ObservableProperty]
    private bool ruleEnabled = true;

    [ObservableProperty]
    private int ruleCooldownMinutes = 30;

    [ObservableProperty]
    private string latestSampleText = "No samples yet";

    public MonitoringViewModel(IMonitoringService monitoring, IComputerService computers,
        ISessionContext session, IDialogService dialogs)
    {
        _monitoring = monitoring;
        _computers = computers;
        _session = session;
        _dialogs = dialogs;
    }

    public override string Title => "Monitoring";
    public override string Subtitle => "Performance history, live metrics and alert rules";

    public string Actor => _session.CurrentUser?.UserName ?? HubConstants.AuditActorSystem;
    public bool CanManageAlerts => _session.CurrentUser?.Can(Permission.ManageAlerts) == true;
    public bool CanPurge => _session.CurrentUser?.Can(Permission.ManageSettings) == true;
    public IReadOnlyList<int> RangeOptions { get; } = [1, 6, 24, 72];
    public IReadOnlyList<AlertMetricType> AlertMetrics { get; } = Enum.GetValues<AlertMetricType>();
    public IReadOnlyList<AlertSeverity> Severities { get; } = Enum.GetValues<AlertSeverity>();

    private int RangeHours => RangeHoursIndex switch { 0 => 1, 1 => 6, 2 => 24, _ => 72 };

    public override async Task LoadAsync()
    {
        await RunAsync(async token =>
        {
            var data = await _computers.GetComputersAsync(cancellationToken: token);
            Computers = new ObservableCollection<ComputerSummary>(data);
            SelectedComputer ??= Computers.FirstOrDefault(c => c.IsOnline) ?? Computers.FirstOrDefault();
            await LoadAlertsAsync(token);
            Rules = new ObservableCollection<AlertRuleView>(await _monitoring.GetRulesAsync(token));
            await LoadSeriesAsync(token);
            await LoadLiveAsync(token);
        });
    }

    partial void OnSelectedComputerChanged(ComputerSummary? value)
    {
        _ = RefreshSelectedAsync();
    }

    partial void OnRangeHoursIndexChanged(int value)
    {
        _ = LoadSeriesAsync(PageToken);
    }

    partial void OnActiveAlertsOnlyChanged(bool value)
    {
        _ = LoadAlertsAsync(PageToken);
    }

    private async Task RefreshSelectedAsync()
    {
        try
        {
            await LoadSeriesAsync(PageToken);
            await LoadLiveAsync(PageToken);
        }
        catch (Exception ex)
        {
            Fail($"Monitoring data could not be refreshed: {ex.Message}");
        }
    }

    private async Task LoadSeriesAsync(CancellationToken token)
    {
        var ids = SelectedComputer is not null
            ? new List<Guid> { SelectedComputer.Id }
            : Computers.Take(6).Select(c => c.Id).ToList();

        if (ids.Count == 0)
        {
            CpuSeries = MemorySeries = DiskSeries = [];
            return;
        }

        var to = DateTime.UtcNow;
        var from = to.AddHours(-RangeHours);
        var data = await _monitoring.GetSeriesAsync(ids, from, to, 120, token);
        var palette = new[]
        {
            Color.FromRgb(59, 130, 246), Color.FromRgb(34, 211, 238), Color.FromRgb(34, 197, 94),
            Color.FromRgb(245, 158, 11), Color.FromRgb(168, 85, 247), Color.FromRgb(239, 68, 68)
        };

        CpuSeries = Build(data, palette, s => s.Samples.Select(x => x.CpuPercent));
        MemorySeries = Build(data, palette, s => s.Samples.Select(x => x.MemoryPercent));
        DiskSeries = Build(data, palette, s => s.Samples.Select(x => x.DiskPercent));

        var latest = data.SelectMany(s => s.Samples).OrderByDescending(s => s.TimestampUtc).FirstOrDefault();
        LatestSampleText = latest is null
            ? "No metric samples are stored for this period."
            : $"Latest sample {latest.TimestampUtc.ToLocalTime():HH:mm:ss} · CPU {latest.CpuPercent:0.#}% · " +
              $"memory {latest.MemoryPercent:0.#}% · disk {latest.DiskPercent:0.#}%";
    }

    private static IReadOnlyList<ChartSeries> Build(IReadOnlyList<MonitoringSeries> data, Color[] palette,
        Func<MonitoringSeries, IEnumerable<double>> selector)
    {
        return data.Take(palette.Length).Select((s, i) => new ChartSeries
        {
            Name = s.ComputerName,
            Color = palette[i],
            Values = selector(s).ToList()
        }).ToList();
    }

    private async Task LoadLiveAsync(CancellationToken token)
    {
        if (SelectedComputer is null)
        {
            LiveMetrics = null;
            return;
        }

        LiveMetrics = await _monitoring.GetLiveMetricsAsync(SelectedComputer.Id, token);
    }

    private async Task LoadAlertsAsync(CancellationToken token)
    {
        var data = await _monitoring.GetAlertsAsync(ActiveAlertsOnly, 250, token);
        Alerts = new ObservableCollection<AlertView>(data);
        SelectedAlert = Alerts.FirstOrDefault();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunAsync(async token =>
        {
            await LoadAlertsAsync(token);
            Rules = new ObservableCollection<AlertRuleView>(await _monitoring.GetRulesAsync(token));
            await LoadSeriesAsync(token);
            await LoadLiveAsync(token);
        }, "Monitoring data refreshed.");
    }

    [RelayCommand]
    private async Task AcknowledgeAlertAsync()
    {
        if (SelectedAlert is null)
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _monitoring.AcknowledgeAlertAsync(Actor, SelectedAlert.Id, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The alert could not be acknowledged.");
            }

            await LoadAlertsAsync(token);
        }, "Alert acknowledged.");
    }

    [RelayCommand]
    private async Task ResolveAlertAsync()
    {
        if (SelectedAlert is null)
        {
            return;
        }

        if (!_dialogs.Confirm("Resolve alert", $"Resolve '{SelectedAlert.Title}'? The alert will close even if the condition persists.", danger: false))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _monitoring.ResolveAlertAsync(Actor, SelectedAlert.Id, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The alert could not be resolved.");
            }

            await LoadAlertsAsync(token);
        }, "Alert resolved.");
    }

    [RelayCommand]
    private void NewRule()
    {
        SelectedRule = null;
        RuleName = "Custom threshold";
        RuleMetric = AlertMetricType.CpuUsage;
        RuleThreshold = 90;
        RuleSeverity = AlertSeverity.Warning;
        RuleEnabled = true;
        RuleCooldownMinutes = 30;
        IsRuleEditorOpen = true;
    }

    [RelayCommand]
    private void EditRule()
    {
        if (SelectedRule is null)
        {
            return;
        }

        RuleName = SelectedRule.Name;
        RuleMetric = SelectedRule.MetricType;
        RuleThreshold = SelectedRule.Threshold;
        RuleSeverity = SelectedRule.Severity;
        RuleEnabled = SelectedRule.Enabled;
        RuleCooldownMinutes = SelectedRule.CooldownMinutes;
        IsRuleEditorOpen = true;
    }

    [RelayCommand]
    private void CancelRule() => IsRuleEditorOpen = false;

    [RelayCommand]
    private async Task SaveRuleAsync()
    {
        if (string.IsNullOrWhiteSpace(RuleName))
        {
            Fail("A rule name is required.");
            return;
        }

        await RunAsync(async token =>
        {
            var rule = new AlertRuleView
            {
                Id = SelectedRule?.Id ?? Guid.NewGuid(),
                Name = RuleName.Trim(),
                MetricType = RuleMetric,
                Threshold = RuleThreshold,
                Severity = RuleSeverity,
                Enabled = RuleEnabled,
                CooldownMinutes = RuleCooldownMinutes,
                ScopeComputerId = SelectedRule?.ScopeComputerId,
                ScopeGroupId = SelectedRule?.ScopeGroupId
            };
            var result = await _monitoring.SaveRuleAsync(Actor, rule, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The rule could not be saved.");
            }

            IsRuleEditorOpen = false;
            Rules = new ObservableCollection<AlertRuleView>(await _monitoring.GetRulesAsync(token));
        }, "Alert rule saved.");
    }

    [RelayCommand]
    private async Task DeleteRuleAsync()
    {
        if (SelectedRule is null)
        {
            return;
        }

        if (!_dialogs.Confirm("Delete alert rule", $"Delete the rule '{SelectedRule.Name}'?", danger: true))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _monitoring.DeleteRuleAsync(Actor, SelectedRule.Id, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The rule could not be deleted.");
            }

            Rules = new ObservableCollection<AlertRuleView>(await _monitoring.GetRulesAsync(token));
        }, "Alert rule deleted.");
    }

    [RelayCommand]
    private async Task ExportHistoryAsync()
    {
        var ids = SelectedComputer is not null
            ? new List<Guid> { SelectedComputer.Id }
            : Computers.Select(c => c.Id).ToList();
        if (ids.Count == 0)
        {
            Fail("There are no computers to export.");
            return;
        }

        await RunAsync(async token =>
        {
            var to = DateTime.UtcNow;
            var from = to.AddHours(-RangeHours);
            var report = await _monitoring.ExportHistoryAsync(ids, from, to, "csv", token);
            _dialogs.SaveReport(report);
        }, "Performance history exported.");
    }

    [RelayCommand]
    private async Task PurgeOldMetricsAsync()
    {
        if (!_dialogs.Confirm("Purge metric history",
                "Delete metric samples older than the configured retention period? This cannot be undone. Alerts and audit entries are not deleted.",
                danger: true))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var count = await _monitoring.PurgeOldMetricsAsync(Actor, token);
            StatusMessage = $"Purged {count} old metric sample(s).";
        });
    }
}
