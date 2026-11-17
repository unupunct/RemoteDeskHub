using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Desktop.Services;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Desktop.ViewModels;

public sealed record GeneratedReport(ReportFile File, DateTime GeneratedUtc)
{
    public string FileName => File.FileName;
    public string SizeText => File.Content.Length < 1024
        ? $"{File.Content.Length} B"
        : $"{File.Content.Length / 1024.0:0.#} KB";

    public string GeneratedText => GeneratedUtc.ToLocalTime().ToString("HH:mm:ss");
}

public sealed partial class ReportsViewModel : PageViewModel
{
    private readonly IReportService _reports;
    private readonly IMonitoringService _monitoring;
    private readonly ISessionContext _session;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private string selectedFormat = "csv";

    [ObservableProperty]
    private DateTime? fromDate = DateTime.Today.AddDays(-7);

    [ObservableProperty]
    private DateTime? toDate = DateTime.Today;

    [ObservableProperty]
    private ObservableCollection<GeneratedReport> generated = [];

    [ObservableProperty]
    private GeneratedReport? selectedGenerated;

    [ObservableProperty]
    private string selectedCustomReport = "network";

    [ObservableProperty]
    private string summaryText = "Choose a report to generate. Files are saved wherever you choose.";

    public ReportsViewModel(IReportService reports, IMonitoringService monitoring,
        ISessionContext session, IDialogService dialogs)
    {
        _reports = reports;
        _monitoring = monitoring;
        _session = session;
        _dialogs = dialogs;
    }

    public override string Title => "Reports";
    public override string Subtitle => "Inventory, alert, audit and performance exports";

    public bool CanExport => _session.CurrentUser?.Can(Permission.ViewReports) == true;
    public IReadOnlyList<string> Formats { get; } = ["csv", "html", "pdf"];
    public IReadOnlyList<string> CustomReports { get; } = ["network", "hardware", "software", "alerts", "agent-health", "audit", "performance"];

    public override Task LoadAsync()
    {
        StatusMessage = "Reports are generated on demand from current hub data.";
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task NetworkInventoryAsync() => GenerateAsync(() => _reports.GenerateNetworkInventoryAsync(SelectedFormat, PageToken), "Network inventory");

    [RelayCommand]
    private Task HardwareInventoryAsync() => GenerateAsync(() => _reports.GenerateHardwareInventoryAsync(SelectedFormat, PageToken), "Hardware inventory");

    [RelayCommand]
    private Task SoftwareInventoryAsync() => GenerateAsync(() => _reports.GenerateSoftwareInventoryAsync(SelectedFormat, PageToken), "Software inventory");

    [RelayCommand]
    private Task AlertReportAsync() => GenerateAsync(() => _reports.GenerateAlertReportAsync(SelectedFormat, PageToken), "Alert report");

    [RelayCommand]
    private Task AgentHealthAsync() => GenerateAsync(() => _reports.GenerateAgentHealthReportAsync(SelectedFormat, PageToken), "Agent health report");

    [RelayCommand]
    private Task AuditReportAsync() => GenerateAsync(() => _reports.GenerateAuditReportAsync(SelectedFormat, PageToken), "Audit report");

    [RelayCommand]
    private Task CustomReportAsync()
    {
        return GenerateAsync(async () =>
        {
            var fromUtc = (FromDate ?? DateTime.Now.AddDays(-7)).ToUniversalTime();
            var toUtc = (ToDate ?? DateTime.Now).Date.AddDays(1).ToUniversalTime();
            if (SelectedCustomReport.Equals("performance", StringComparison.OrdinalIgnoreCase))
            {
                return await _monitoring.ExportHistoryAsync([], fromUtc, toUtc, SelectedFormat, PageToken);
            }

            return await _reports.GenerateAsync(new ExportRequest
            {
                ReportName = SelectedCustomReport,
                Format = SelectedFormat,
                FromUtc = fromUtc,
                ToUtc = toUtc
            }, PageToken);
        }, "Custom report");
    }

    private async Task GenerateAsync(Func<Task<ReportFile>> factory, string label)
    {
        if (!CanExport)
        {
            Fail("Your role does not permit report exports.");
            return;
        }

        await RunAsync(async _ =>
        {
            var file = await factory();
            var entry = new GeneratedReport(file, DateTime.UtcNow);
            Generated.Insert(0, entry);
            SelectedGenerated = entry;
            SummaryText = $"{label} generated at {entry.GeneratedText} · {entry.SizeText}.";
            _dialogs.SaveReport(file);
        }, $"{label} generated.");
    }

    [RelayCommand]
    private void SaveGenerated()
    {
        if (SelectedGenerated is null)
        {
            Fail("Select a generated report to save again.");
            return;
        }

        _dialogs.SaveReport(SelectedGenerated.File);
        StatusMessage = $"'{SelectedGenerated.File.FileName}' saved.";
    }

    [RelayCommand]
    private void ClearHistory()
    {
        Generated.Clear();
        SummaryText = "Generated report list cleared. Files already saved are not affected.";
    }
}
