using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Desktop.Services;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;
using RemoteDeskHub.Shared.Security;

namespace RemoteDeskHub.Desktop.ViewModels;

public sealed partial class FileTransferViewModel : PageViewModel
{
    private readonly IFileTransferService _files;
    private readonly IComputerService _computers;
    private readonly ISharedFolderService _sharedFolder;
    private readonly ISessionContext _session;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _transferCts;

    [ObservableProperty]
    private ObservableCollection<ComputerSummary> computers = [];

    [ObservableProperty]
    private ComputerSummary? selectedComputer;

    [ObservableProperty]
    private ObservableCollection<string> allowedRoots = [];

    [ObservableProperty]
    private string? selectedRoot;

    [ObservableProperty]
    private string currentPath = string.Empty;

    [ObservableProperty]
    private string? parentPath;

    [ObservableProperty]
    private ObservableCollection<FileEntryDto> entries = [];

    [ObservableProperty]
    private FileEntryDto? selectedEntry;

    [ObservableProperty]
    private ObservableCollection<FileTransferView> transfers = [];

    [ObservableProperty]
    private bool isTransferring;

    [ObservableProperty]
    private double transferPercent;

    [ObservableProperty]
    private string transferStatus = "No transfer running.";

    [ObservableProperty]
    private string summaryText = "Select a computer to browse permitted folders.";

    [ObservableProperty]
    private SharedFolderStatusView? sharedStatus;

    [ObservableProperty]
    private ObservableCollection<SharedFolderFileView> sharedFiles = [];

    [ObservableProperty]
    private ObservableCollection<SharedFolderAgentView> sharedAgents = [];

    [ObservableProperty]
    private SharedFolderFileView? selectedSharedFile;

    [ObservableProperty]
    private string? sharedStatusMessage = "The hub checks the shared folder every 15 seconds.";

    [ObservableProperty]
    private bool sharedHasError;

    [ObservableProperty]
    private string sharedRootPath = @"C:\zzz";

    public FileTransferViewModel(IFileTransferService files, IComputerService computers,
        ISharedFolderService sharedFolder, ISessionContext session, IDialogService dialogs)
    {
        _files = files;
        _computers = computers;
        _sharedFolder = sharedFolder;
        _session = session;
        _dialogs = dialogs;
    }

    public override string Title => "File Transfer";
    public override string Subtitle => "Upload and download files inside approved roots";

    public string Actor => _session.CurrentUser?.UserName ?? HubConstants.AuditActorSystem;
    public bool CanTransfer => _session.CurrentUser?.Can(Permission.TransferFiles) == true;
    public bool CanDelete => _session.CurrentUser?.Can(Permission.DeleteRemoteFiles) == true;
    public bool CanManageSharedFolder => _session.CurrentUser?.Can(Permission.TransferFiles) == true;
    public string SharedFileCountText => SharedStatus is null ? "—" : SharedStatus.FileCount.ToString("N0");
    public string SharedTotalSizeText => SharedStatus is null ? "—" : FormatBytes(SharedStatus.TotalBytes);
    public string SharedAgentCountText => SharedStatus is null ? "—" : $"{SharedStatus.AgentCount} online";
    public string SharedConflictCountText => SharedStatus is null ? "—" : SharedStatus.ConflictCopyCount.ToString("N0");
    public string SharedEnabledText => SharedStatus is null ? "—" : SharedStatus.Enabled ? "Enabled" : "Disabled";
    public string SharedLastSyncText => SharedStatus?.LastCompletedUtc is null
        ? "Not yet"
        : SharedStatus.LastCompletedUtc.Value.ToLocalTime().ToString("g");

    partial void OnSharedStatusChanged(SharedFolderStatusView? value)
    {
        OnPropertyChanged(nameof(SharedFileCountText));
        OnPropertyChanged(nameof(SharedTotalSizeText));
        OnPropertyChanged(nameof(SharedAgentCountText));
        OnPropertyChanged(nameof(SharedConflictCountText));
        OnPropertyChanged(nameof(SharedEnabledText));
        OnPropertyChanged(nameof(SharedLastSyncText));
    }

    public override async Task LoadAsync()
    {
        await RunAsync(async token =>
        {
            var data = await _computers.GetComputersAsync(cancellationToken: token);
            Computers = new ObservableCollection<ComputerSummary>(data);
            SelectedComputer ??= Computers.FirstOrDefault(c => c.IsOnline) ?? Computers.FirstOrDefault();
            await LoadNavigationAsync(token);
            await LoadHistoryAsync(token);
            await LoadSharedFolderAsync(token);
        });
    }

    partial void OnSelectedComputerChanged(ComputerSummary? value) => _ = LoadNavigationAsync(PageToken);

    private async Task LoadNavigationAsync(CancellationToken token)
    {
        if (SelectedComputer is null)
        {
            AllowedRoots = [];
            Entries = [];
            return;
        }

        try
        {
            AllowedRoots = new ObservableCollection<string>(await _files.GetAllowedRootsAsync(SelectedComputer.Id, token));
            if (AllowedRoots.Count == 0)
            {
                Entries = [];
                SummaryText = "This agent reports no permitted file roots. Configure them in Settings → Agent policy.";
                return;
            }

            var root = SelectedRoot is not null && AllowedRoots.Contains(SelectedRoot, StringComparer.OrdinalIgnoreCase)
                ? SelectedRoot
                : AllowedRoots[0];
            SelectedRoot = root;
            await NavigateAsync(root, token);
        }
        catch (Exception ex)
        {
            Fail($"The remote file browser could not be opened: {ex.Message}");
        }
    }

    private async Task NavigateAsync(string path, CancellationToken token)
    {
        if (SelectedComputer is null)
        {
            return;
        }

        var listing = await _files.ListRemoteAsync(SelectedComputer.Id, path, token);
        CurrentPath = listing.Path;
        ParentPath = listing.ParentPath;
        Entries = new ObservableCollection<FileEntryDto>(listing.Entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name));
        SummaryText = $"{listing.Entries.Count(e => !e.IsDirectory)} file(s), {listing.Entries.Count(e => e.IsDirectory)} folder(s)" +
                      (listing.IsTruncated ? " · listing truncated by the agent" : string.Empty);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunAsync(async token =>
        {
            if (!string.IsNullOrWhiteSpace(CurrentPath))
            {
                await NavigateAsync(CurrentPath, token);
            }
            else
            {
                await LoadNavigationAsync(token);
            }

            await LoadHistoryAsync(token);
        }, "Folder refreshed.");
    }

    [RelayCommand]
    private async Task OpenEntryAsync()
    {
        if (SelectedEntry is null || !SelectedEntry.IsDirectory)
        {
            return;
        }

        var entry = SelectedEntry;
        await RunAsync(token => NavigateAsync(entry.FullPath, token));
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (string.IsNullOrWhiteSpace(ParentPath))
        {
            return;
        }

        var target = ParentPath;
        await RunAsync(token => NavigateAsync(target, token));
    }

    [RelayCommand]
    private async Task OpenRootAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedRoot))
        {
            return;
        }

        var root = SelectedRoot;
        await RunAsync(token => NavigateAsync(root, token));
    }

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentPath))
        {
            Fail("Open a folder first.");
            return;
        }

        var name = _dialogs.Prompt("New folder", $"Folder name inside {CurrentPath}");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var target = Path.Combine(CurrentPath, name.Trim());
        await RunAsync(async token =>
        {
            var result = await _files.CreateRemoteDirectoryAsync(Actor, SelectedComputer!.Id, target, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The folder could not be created.");
            }

            await NavigateAsync(CurrentPath, token);
        }, "Folder created.");
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (SelectedComputer is null || SelectedEntry is null)
        {
            return;
        }

        var entry = SelectedEntry;
        var newName = _dialogs.Prompt("Rename", $"New name for '{entry.Name}'", entry.Name);
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, entry.Name, StringComparison.Ordinal))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _files.RenameRemoteAsync(Actor, SelectedComputer.Id, entry.FullPath, newName.Trim(), confirmed: true, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The item could not be renamed.");
            }

            await NavigateAsync(CurrentPath, token);
        }, "Item renamed.");
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedComputer is null || SelectedEntry is null)
        {
            return;
        }

        var entry = SelectedEntry;
        if (!_dialogs.Confirm("Delete remote item",
                $"Permanently delete '{entry.Name}' on {SelectedComputer.Name}?" +
                (entry.IsDirectory ? "\n\nThe folder and all of its contents will be removed." : string.Empty),
                danger: true))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _files.DeleteRemoteAsync(Actor, SelectedComputer.Id, entry.FullPath, entry.IsDirectory, confirmed: true, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The item could not be deleted.");
            }

            await NavigateAsync(CurrentPath, token);
        }, "Remote item deleted.");
    }

    [RelayCommand]
    private async Task UploadAsync()
    {
        if (SelectedComputer is null || string.IsNullOrWhiteSpace(CurrentPath))
        {
            Fail("Open a remote folder first.");
            return;
        }

        var localPath = _dialogs.OpenFile("All files (*.*)|*.*");
        if (localPath is null)
        {
            return;
        }

        var remotePath = Path.Combine(CurrentPath, Path.GetFileName(localPath));
        var confirmed = _dialogs.Confirm("Upload file",
            $"Upload:\n{localPath}\n\nto:\n{SelectedComputer.Name} → {remotePath}\n\nThe transfer is audited.", danger: false);
        if (!confirmed)
        {
            return;
        }

        _transferCts?.Dispose();
        _transferCts = new CancellationTokenSource();
        IsTransferring = true;
        TransferPercent = 0;
        try
        {
            var progress = new Progress<TransferProgress>(p =>
            {
                TransferPercent = p.Percent;
                TransferStatus = $"{p.Percent:0.0}% · {FormatBytes(p.TransferredBytes)} of {FormatBytes(p.TotalBytes)} · {FormatBytes((long)p.BytesPerSecond)}/s";
            });

            var result = await _files.UploadAsync(Actor, SelectedComputer.Id, localPath, remotePath, progress, _transferCts.Token);
            TransferStatus = result.Status == Shared.TransferStatus.Completed
                ? $"Upload complete · {FormatBytes(result.SizeBytes)} · SHA-256 {ShortHash(result.Sha256)}"
                : $"Upload {result.Status}: {result.Error}";
            await NavigateAsync(CurrentPath, PageToken);
            await LoadHistoryAsync(PageToken);
            StatusMessage = result.Status == Shared.TransferStatus.Completed ? "Upload completed." : "Upload did not complete.";
            HasError = result.Status != Shared.TransferStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            TransferStatus = "Upload cancelled.";
            StatusMessage = "Upload cancelled.";
        }
        catch (Exception ex)
        {
            TransferStatus = $"Upload failed: {ex.Message}";
            Fail($"Upload failed: {ex.Message}");
        }
        finally
        {
            IsTransferring = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (SelectedComputer is null || SelectedEntry is null || SelectedEntry.IsDirectory)
        {
            Fail("Select a remote file to download.");
            return;
        }

        var entry = SelectedEntry;
        var target = _dialogs.SaveFile(entry.Name, "All files (*.*)|*.*");
        if (target is null)
        {
            return;
        }

        _transferCts?.Dispose();
        _transferCts = new CancellationTokenSource();
        IsTransferring = true;
        TransferPercent = 0;
        try
        {
            var progress = new Progress<TransferProgress>(p =>
            {
                TransferPercent = p.Percent;
                TransferStatus = $"{p.Percent:0.0}% · {FormatBytes(p.TransferredBytes)} of {FormatBytes(p.TotalBytes)} · {FormatBytes((long)p.BytesPerSecond)}/s";
            });

            var result = await _files.DownloadAsync(Actor, SelectedComputer.Id, entry.FullPath, target, progress, _transferCts.Token);
            TransferStatus = result.Status == Shared.TransferStatus.Completed
                ? $"Download complete · {FormatBytes(result.SizeBytes)} · SHA-256 {ShortHash(result.Sha256)}"
                : $"Download {result.Status}: {result.Error}";
            await LoadHistoryAsync(PageToken);
            StatusMessage = result.Status == Shared.TransferStatus.Completed ? $"Saved to {target}." : "Download did not complete.";
            HasError = result.Status != Shared.TransferStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            TransferStatus = "Download cancelled.";
            StatusMessage = "Download cancelled.";
        }
        catch (Exception ex)
        {
            TransferStatus = $"Download failed: {ex.Message}";
            Fail($"Download failed: {ex.Message}");
        }
        finally
        {
            IsTransferring = false;
        }
    }

    [RelayCommand]
    private void CancelTransfer() => _transferCts?.Cancel();

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        await RunAsync(token => LoadHistoryAsync(token), "Transfer history refreshed.");
    }

    [RelayCommand]
    private async Task RefreshSharedFolderAsync()
    {
        await RunAsync(async token =>
        {
            await LoadSharedFolderAsync(token);
            StatusMessage = "Shared-folder status refreshed.";
        });
    }

    [RelayCommand]
    private async Task AddSharedFilesAsync()
    {
        if (!CanManageSharedFolder)
        {
            Fail("Your account cannot add files to the shared folder.");
            return;
        }

        var sourceFiles = _dialogs.OpenFiles("All files (*.*)|*.*", SharedRootPath);
        if (sourceFiles.Count == 0)
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _sharedFolder.AddLocalFilesAsync(Actor, sourceFiles, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The files could not be added to the shared folder.");
            }

            await LoadSharedFolderAsync(token);
            SharedStatusMessage = result.Message;
            SharedHasError = false;
            StatusMessage = result.Message;
        });
    }

    [RelayCommand]
    private async Task AddSharedFolderAsync()
    {
        if (!CanManageSharedFolder)
        {
            Fail("Your account cannot add folders to the shared folder.");
            return;
        }

        var sourceFolder = _dialogs.OpenFolder(SharedRootPath);
        if (string.IsNullOrWhiteSpace(sourceFolder))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _sharedFolder.AddLocalFolderAsync(Actor, sourceFolder, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The folder could not be added to the shared folder.");
            }

            await LoadSharedFolderAsync(token);
            SharedStatusMessage = result.Message;
            SharedHasError = false;
            StatusMessage = result.Message;
        });
    }

    [RelayCommand]
    private void OpenSharedFolder()
    {
        var root = SharedStatus?.RootPath ?? SharedRootPath;
        if (!Directory.Exists(root))
        {
            Fail($"The shared folder '{root}' does not exist yet. Add a file to create it.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{root}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Fail($"Windows Explorer could not be opened: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenSharedFileLocation()
    {
        var root = SharedStatus?.RootPath ?? SharedRootPath;
        var relative = SelectedSharedFile?.Path;
        if (string.IsNullOrWhiteSpace(relative))
        {
            Fail("Select a shared file first.");
            return;
        }

        if (!PathGuard.TryResolveRelativeWithinRoot(root, relative, out var full, out _, out var error))
        {
            Fail($"The shared-file path is invalid: {error}");
            return;
        }

        var argument = File.Exists(full) ? $"/select,\"{full}\"" : $"\"{root}\"";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = argument,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Fail($"Windows Explorer could not be opened: {ex.Message}");
        }
    }

    private async Task LoadSharedFolderAsync(CancellationToken token)
    {
        try
        {
            var status = await _sharedFolder.GetStatusAsync(token);
            var files = await _sharedFolder.GetFilesAsync(500, token);
            var agents = await _sharedFolder.GetAgentsAsync(token);
            SharedStatus = status;
            SharedFiles = new ObservableCollection<SharedFolderFileView>(files);
            SharedAgents = new ObservableCollection<SharedFolderAgentView>(agents);
            SharedRootPath = status.RootPath;
            SharedHasError = !string.IsNullOrWhiteSpace(status.LastError);
            SharedStatusMessage = SharedHasError
                ? status.LastError
                : $"Last check {SharedLastSyncText}. The hub checks for new files every 15 seconds.";
        }
        catch (Exception ex)
        {
            SharedHasError = true;
            SharedStatusMessage = $"Shared-folder status is unavailable: {ex.Message}";
        }
    }

    private async Task LoadHistoryAsync(CancellationToken token)
    {
        var data = await _files.GetHistoryAsync(SelectedComputer?.Id, 100, token);
        Transfers = new ObservableCollection<FileTransferView>(data);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        if (bytes <= 0)
        {
            return "0 B";
        }

        var order = Math.Clamp((int)Math.Floor(Math.Log(bytes, 1024)), 0, units.Length - 1);
        return $"{bytes / Math.Pow(1024, order):0.#} {units[order]}";
    }

    private static string ShortHash(string? hash) =>
        string.IsNullOrWhiteSpace(hash) ? "n/a" : hash.Length <= 16 ? hash : hash[..16] + "…";
}
