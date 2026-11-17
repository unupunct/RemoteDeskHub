using System.Diagnostics;
using Microsoft.Win32;

namespace RemoteDeskHub.Desktop.Services;

/// <summary>
/// Adds or removes the per-user Windows startup entry for the hub console. HKCU is used so the
/// preference never requires elevation.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RemoteDeskHub";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("The Windows startup registry key is not accessible.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("The hub executable path could not be resolved.");
        key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
    }
}
