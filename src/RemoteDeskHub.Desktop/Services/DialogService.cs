using System.IO;
using System.Windows;
using Microsoft.Win32;
using RemoteDeskHub.Core.Models;

namespace RemoteDeskHub.Desktop.Services;

public interface IDialogService
{
    bool Confirm(string title, string message, bool danger = false);
    bool RequestConsent(string title, string message, string consentText);
    void Info(string title, string message);
    void Warning(string title, string message);
    void Error(string title, string message);
    string? Prompt(string title, string label, string? initialValue = null, bool multiline = false);
    string? PromptPassword(string title, string label);
    string? OpenFile(string filter, string? initialDirectory = null);
    IReadOnlyList<string> OpenFiles(string filter, string? initialDirectory = null);
    string? OpenFolder(string? initialDirectory = null);
    string? SaveFile(string suggestedName, string filter);
    void SaveReport(ReportFile report);
}

public sealed class DialogService : IDialogService
{
    public bool Confirm(string title, string message, bool danger = false)
    {
        var window = new MessageWindow(title, message, MessageKind.Confirmation, danger)
        {
            Owner = ResolveOwner()
        };
        return window.ShowDialog() == true;
    }

    public bool RequestConsent(string title, string message, string consentText)
    {
        var window = new ConsentWindow(title, message, consentText) { Owner = ResolveOwner() };
        return window.ShowDialog() == true;
    }

    public void Info(string title, string message) => Show(title, message, MessageKind.Information);
    public void Warning(string title, string message) => Show(title, message, MessageKind.Warning);
    public void Error(string title, string message) => Show(title, message, MessageKind.Error);

    public string? Prompt(string title, string label, string? initialValue = null, bool multiline = false)
    {
        var window = new PromptWindow(title, label, initialValue, multiline)
        {
            Owner = ResolveOwner()
        };
        return window.ShowDialog() == true && !string.IsNullOrWhiteSpace(window.Value) ? window.Value : null;
    }

    public string? PromptPassword(string title, string label)
    {
        var window = new PasswordPromptWindow(title, label) { Owner = ResolveOwner() };
        return window.ShowDialog() == true ? window.Value : null;
    }

    public string? OpenFile(string filter, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(ResolveOwner()) == true ? dialog.FileName : null;
    }

    public IReadOnlyList<string> OpenFiles(string filter, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Filter = filter,
            CheckFileExists = true,
            Multiselect = true
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(ResolveOwner()) == true ? dialog.FileNames : [];
    }

    public string? OpenFolder(string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog { Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(ResolveOwner()) == true ? dialog.FolderName : null;
    }

    public string? SaveFile(string suggestedName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            FileName = suggestedName,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true
        };
        return dialog.ShowDialog(ResolveOwner()) == true ? dialog.FileName : null;
    }

    public void SaveReport(ReportFile report)
    {
        var extension = Path.GetExtension(report.FileName);
        var filter = extension.ToLowerInvariant() switch
        {
            ".pdf" => "PDF document (*.pdf)|*.pdf",
            ".html" => "HTML document (*.html)|*.html",
            ".csv" => "CSV file (*.csv)|*.csv",
            _ => "All files (*.*)|*.*"
        };

        var target = SaveFile(report.FileName, filter);
        if (target is null)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(target, report.Content);
            Info("Export complete", $"The report was saved to:\n{target}");
        }
        catch (Exception ex)
        {
            Error("Export failed", ex.Message);
        }
    }

    private static void Show(string title, string message, MessageKind kind)
    {
        var window = new MessageWindow(title, message, kind) { Owner = ResolveOwner() };
        window.ShowDialog();
    }

    private static Window? ResolveOwner()
    {
        var active = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        return active ?? Application.Current?.MainWindow;
    }
}
