using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Desktop.Services;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Desktop.ViewModels;

public sealed partial class AuditLogViewModel : PageViewModel
{
    private readonly IAuditService _audit;
    private readonly IReportService _reports;
    private readonly ISessionContext _session;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private ObservableCollection<AuditView> entries = [];

    [ObservableProperty]
    private AuditView? selectedEntry;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private DateTime? fromDate = DateTime.Today.AddDays(-7);

    [ObservableProperty]
    private DateTime? toDate = DateTime.Today;

    [ObservableProperty]
    private int limit = 500;

    [ObservableProperty]
    private string chainStatus = "The audit hash chain has not been verified in this session.";

    [ObservableProperty]
    private bool? chainValid;

    [ObservableProperty]
    private string summaryText = "No audit entries loaded.";

    public AuditLogViewModel(IAuditService audit, IReportService reports, ISessionContext session, IDialogService dialogs)
    {
        _audit = audit;
        _reports = reports;
        _session = session;
        _dialogs = dialogs;
    }

    public override string Title => "Audit Log";
    public override string Subtitle => "Tamper-evident record of every privileged action";

    public bool CanPurge => _session.CurrentUser?.Can(Permission.ManageSettings) == true;

    public override async Task LoadAsync()
    {
        await RunAsync(token => LoadAsync(token, 500), "Audit log loaded.");
    }

    private async Task LoadAsync(CancellationToken token, int max)
    {
        var from = FromDate?.Date.ToUniversalTime();
        var to = ToDate?.Date.AddDays(1).ToUniversalTime();
        var data = await _audit.QueryAsync(from, to, string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(), max, token);
        Entries = new ObservableCollection<AuditView>(data);
        SelectedEntry = Entries.FirstOrDefault();
        SummaryText = $"{data.Count} entr{(data.Count == 1 ? "y" : "ies")} loaded" +
                      (from is not null ? $" from {from.Value.ToLocalTime():yyyy-MM-dd}" : string.Empty) +
                      (to is not null ? $" to {to.Value.ToLocalTime():yyyy-MM-dd}" : string.Empty) + ".";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunAsync(token => LoadAsync(token, Limit), "Audit log refreshed.");
    }

    [RelayCommand]
    private async Task VerifyChainAsync()
    {
        await RunAsync(async token =>
        {
            var (valid, message) = await _audit.VerifyChainAsync(token);
            ChainValid = valid;
            ChainStatus = message;
        });
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        await RunAsync(async token =>
        {
            var report = await _reports.GenerateAuditReportAsync("csv", token);
            _dialogs.SaveReport(report);
        }, "Audit log exported.");
    }

    [RelayCommand]
    private async Task PurgeOldAsync()
    {
        if (!_dialogs.Confirm("Purge old audit entries",
                "Delete audit entries older than the configured retention period? This weakens the audit trail and cannot be undone.",
                danger: true))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var count = await _audit.PurgeOldAsync(token);
            await LoadAsync(token, Limit);
            StatusMessage = $"Purged {count} audit entr{(count == 1 ? "y" : "ies")}.";
        });
    }
}
