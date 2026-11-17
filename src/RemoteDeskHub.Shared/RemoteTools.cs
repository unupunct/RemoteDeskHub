using System.Net;

namespace RemoteDeskHub.Shared;

/// <summary>
/// Describes a remote-assistance client that the hub can launch on the operator computer.
/// The hub never stores credentials for these tools.
/// </summary>
public sealed record RemoteToolDefinition(
    RemoteToolKind Kind,
    string DisplayName,
    bool RequiresConnectionTarget,
    string? ExecutableName,
    string Instructions);

/// <summary>
/// Central catalogue for supported remote-assistance clients. Argument construction and executable
/// discovery live here so the service, the UI and the launcher agree on the same rules.
/// </summary>
public static class RemoteToolCatalog
{
    public static IReadOnlyList<RemoteToolDefinition> All { get; } =
    [
        new(
            RemoteToolKind.Rdp,
            "Windows Remote Desktop (RDP)",
            RequiresConnectionTarget: false,
            ExecutableName: "mstsc.exe",
            Instructions:
                "Remote Desktop must be enabled on the target computer and your account must be permitted to sign in remotely. " +
                "RemoteDesk Hub launches mstsc on this computer and connects to the target address. If the target is on a different " +
                "subnet, verify that TCP 3389 is reachable."),
        new(
            RemoteToolKind.QuickAssist,
            "Windows Quick Assist",
            RequiresConnectionTarget: false,
            ExecutableName: "quickassist.exe",
            Instructions:
                "Quick Assist opens a Microsoft-hosted assistance session. RemoteDesk Hub launches Quick Assist on this computer. " +
                "The person at the target computer chooses 'Assist another person' and shares the 6-digit code with you. " +
                "RemoteDesk Hub records the session but never stores or transmits the code."),
        new(
            RemoteToolKind.WindowsRemoteAssistance,
            "Windows Remote Assistance",
            RequiresConnectionTarget: false,
            ExecutableName: "msra.exe",
            Instructions:
                "Windows Remote Assistance must be enabled on the target. RemoteDesk Hub launches msra.exe on this computer and " +
                "targets the remote computer. The person at the target must accept the invitation."),
        new(
            RemoteToolKind.TeamViewer,
            "TeamViewer",
            RequiresConnectionTarget: true,
            ExecutableName: "TeamViewer.exe",
            Instructions:
                "TeamViewer must be installed on this computer. Enter the target's TeamViewer ID. The target must accept the " +
                "connection or use its configured unattended-access password. RemoteDesk Hub does not store the password."),
        new(
            RemoteToolKind.AnyDesk,
            "AnyDesk",
            RequiresConnectionTarget: true,
            ExecutableName: "AnyDesk.exe",
            Instructions:
                "AnyDesk must be installed on this computer. Enter the target's AnyDesk ID or alias. The target must accept the " +
                "connection. RemoteDesk Hub does not store the AnyDesk password."),
        new(
            RemoteToolKind.LmViewer,
            "LM Viewer (LiteManager)",
            RequiresConnectionTarget: true,
            ExecutableName: "ROMViewer.exe",
            Instructions:
                "LiteManager Viewer (ROMViewer.exe) must be installed on this computer. Enter the target IP address, hostname or " +
                "NoIP ID. The target must run LiteManager Server and accept the connection. RemoteDesk Hub does not store the password."),
        new(
            RemoteToolKind.RustDesk,
            "RustDesk",
            RequiresConnectionTarget: true,
            ExecutableName: "rustdesk.exe",
            Instructions:
                "RustDesk must be installed on this computer. Enter the target's RustDesk ID. The target must accept the connection " +
                "or use its configured permanent password. RemoteDesk Hub does not store the password."),
        new(
            RemoteToolKind.ThirdParty,
            "Other third-party tool (manual)",
            RequiresConnectionTarget: true,
            ExecutableName: null,
            Instructions:
                "Use the arguments field from a support profile for a custom tool. RemoteDesk Hub records the session and displays " +
                "the connection details, but does not launch an unknown executable.")
    ];

    public static RemoteToolDefinition Get(RemoteToolKind kind) =>
        All.FirstOrDefault(t => t.Kind == kind) ?? All[0];

    /// <summary>Resolves the value that should be passed to the tool for this target.</summary>
    public static string? ResolveTarget(RemoteToolKind kind, string? ipAddress, string? connectionTarget)
    {
        var target = string.IsNullOrWhiteSpace(connectionTarget) ? ipAddress : connectionTarget;
        return kind switch
        {
            RemoteToolKind.QuickAssist => null,
            _ => string.IsNullOrWhiteSpace(target) ? null : target.Trim()
        };
    }

    /// <summary>True when the target is a TeamViewer/AnyDesk/RustDesk ID or a LiteManager connection name.</summary>
    public static bool RequiresConnectionTarget(RemoteToolKind kind) => Get(kind).RequiresConnectionTarget;

    /// <summary>Validates a target before it is placed into a process argument string.</summary>
    public static bool IsValidTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Length > 256)
        {
            return false;
        }

        return target.All(ch => !char.IsControl(ch) && ch != '"');
    }

    /// <summary>
    /// Builds a Windows command line for the selected tool. The target is quoted. The caller must
    /// validate the target with <see cref="IsValidTarget"/> first.
    /// </summary>
    public static string? BuildArguments(RemoteToolKind kind, string? target, string? extraArguments)
    {
        var arguments = kind switch
        {
            RemoteToolKind.Rdp when !string.IsNullOrWhiteSpace(target) => $"/v:{Quote(target)}",
            RemoteToolKind.QuickAssist => null,
            RemoteToolKind.WindowsRemoteAssistance when !string.IsNullOrWhiteSpace(target) => $"/offerra {Quote(target)}",
            RemoteToolKind.TeamViewer when !string.IsNullOrWhiteSpace(target) => $"--id {Quote(target)}",
            RemoteToolKind.AnyDesk when !string.IsNullOrWhiteSpace(target) => Quote(target),
            RemoteToolKind.LmViewer when !string.IsNullOrWhiteSpace(target) =>
                IPAddress.TryParse(target, out _)
                    ? $"/name:{Quote(target)} /fullcontrol"
                    : $"/NOIPID:{Quote(target)} /fullcontrol",
            RemoteToolKind.RustDesk when !string.IsNullOrWhiteSpace(target) => $"--connect {Quote(target)}",
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(extraArguments))
        {
            arguments = string.IsNullOrWhiteSpace(arguments)
                ? extraArguments.Trim()
                : $"{arguments} {extraArguments.Trim()}";
        }

        return arguments;
    }

    /// <summary>
    /// Candidate executable paths in priority order. The bare executable name is included last so
    /// Windows can resolve it through the PATH or App Paths registration.
    /// </summary>
    public static IReadOnlyList<string> GetExecutableCandidates(RemoteToolKind kind)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var candidates = kind switch
        {
            RemoteToolKind.Rdp => Paths(Path.Combine(system, "mstsc.exe")),
            RemoteToolKind.QuickAssist => Paths(Path.Combine(system, "quickassist.exe")),
            RemoteToolKind.WindowsRemoteAssistance => Paths(Path.Combine(system, "msra.exe")),
            RemoteToolKind.TeamViewer => Paths(
                Path.Combine(programFiles, "TeamViewer", "TeamViewer.exe"),
                Path.Combine(programFilesX86, "TeamViewer", "TeamViewer.exe")),
            RemoteToolKind.AnyDesk => Paths(
                Path.Combine(programFilesX86, "AnyDesk", "AnyDesk.exe"),
                Path.Combine(programFiles, "AnyDesk", "AnyDesk.exe")),
            RemoteToolKind.LmViewer => Paths(
                Path.Combine(programFilesX86, "LiteManager Pro - Viewer", "ROMViewer.exe"),
                Path.Combine(programFiles, "LiteManager Pro - Viewer", "ROMViewer.exe"),
                Path.Combine(programFilesX86, "LiteManager - Viewer", "ROMViewer.exe"),
                Path.Combine(programFiles, "LiteManager - Viewer", "ROMViewer.exe")),
            RemoteToolKind.RustDesk => Paths(
                Path.Combine(programFiles, "RustDesk", "rustdesk.exe"),
                Path.Combine(programFilesX86, "RustDesk", "rustdesk.exe"),
                Path.Combine(localAppData, "rustdesk", "rustdesk.exe")),
            _ => []
        };

        var name = Get(kind).ExecutableName;
        return string.IsNullOrWhiteSpace(name)
            ? candidates
            : [.. candidates, name];
    }

    private static string Quote(string value) => $"\"{value}\"";

    private static string[] Paths(params string[] paths) =>
        paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
