using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Desktop.Services;

public sealed record RemoteToolLaunchResult(int? ProcessId, string ExecutablePath);

public interface IRemoteToolLauncher
{
    Task<OperationResult<RemoteToolLaunchResult>> LaunchAsync(
        RemoteAssistanceLaunchInfo info,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Starts the operator-side remote-assistance client. The hub never passes credentials and never
/// runs the target-side agent; this only opens the configured viewer/tool on this computer.
/// </summary>
public sealed class RemoteToolLauncher : IRemoteToolLauncher
{
    public Task<OperationResult<RemoteToolLaunchResult>> LaunchAsync(
        RemoteAssistanceLaunchInfo info,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(OperationResult<RemoteToolLaunchResult>.Fail("Launch cancelled."));
        }

        if (info.Tool == RemoteToolKind.ThirdParty)
        {
            return Task.FromResult(OperationResult<RemoteToolLaunchResult>.Fail(
                "The manual third-party tool cannot be started automatically. Select a named tool or use the prepared details."));
        }

        var executable = ResolveExecutable(info.Tool);
        if (string.IsNullOrWhiteSpace(executable))
        {
            var name = RemoteToolCatalog.Get(info.Tool).ExecutableName ?? info.ToolDisplayName;
            return Task.FromResult(OperationResult<RemoteToolLaunchResult>.Fail(
                $"{info.ToolDisplayName} was not found on this computer. Install it, add {name} to PATH, or launch it manually with the prepared details."));
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = info.Arguments ?? string.Empty,
                UseShellExecute = true,
                WorkingDirectory = Path.IsPathRooted(executable)
                    ? Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
                    : Environment.CurrentDirectory
            };

            var process = Process.Start(startInfo);
            return Task.FromResult(OperationResult<RemoteToolLaunchResult>.Ok(
                new RemoteToolLaunchResult(process?.Id, executable)));
        }
        catch (Win32Exception ex)
        {
            return Task.FromResult(OperationResult<RemoteToolLaunchResult>.Fail(
                $"Windows could not start {info.ToolDisplayName}: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(OperationResult<RemoteToolLaunchResult>.Fail(
                $"The remote tool could not be started: {ex.Message}"));
        }
    }

    private static string? ResolveExecutable(RemoteToolKind tool)
    {
        foreach (var candidate in RemoteToolCatalog.GetExecutableCandidates(tool))
        {
            if (Path.IsPathRooted(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (tool == RemoteToolKind.AnyDesk)
        {
            var custom = FindAnyDeskCustomClient();
            if (!string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }
        }

        // Let ShellExecute resolve the bare executable through App Paths and PATH.
        return RemoteToolCatalog.Get(tool).ExecutableName;
    }

    private static string? FindAnyDeskCustomClient()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };

        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(root, "AnyDesk-*"))
                {
                    var match = Directory.EnumerateFiles(directory, "AnyDesk*.exe", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(match))
                    {
                        return match;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // A locked-down Program Files folder is not fatal; try the next root.
            }
            catch (IOException)
            {
                // Best effort discovery only.
            }
        }

        return null;
    }
}
