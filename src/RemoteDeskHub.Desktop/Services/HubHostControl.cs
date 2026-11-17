using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Desktop.Services;

/// <summary>
/// Desktop-side control for the separate headless hub host process. The host owns the Kestrel
/// listener and background work; the console can close or crash without stopping the hub. A named
/// ready event and port file are the handshake; a stop event requests an orderly shutdown.
/// </summary>
public sealed class HubHostControl(
    IAppPaths paths,
    ILogger<HubHostControl> logger) : IHubServer
{
    private const string ReadyEventName = @"Local\RemoteDeskHub.HubHost.Ready";
    private const string StopEventName = @"Local\RemoteDeskHub.HubHost.Stop";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);

    private string PortFilePath => Path.Combine(paths.RootDirectory, "hub-host.port");
    private string PidFilePath => Path.Combine(paths.RootDirectory, "hub-host.pid");
    private bool _startedByThisConsole;

    public bool IsRunning => IsReady();

    /// <summary>True when this console launched the host and is therefore allowed to stop it on exit.</summary>
    public bool StartedByThisConsole => _startedByThisConsole;

    public int BoundPort
    {
        get
        {
            try
            {
                if (File.Exists(PortFilePath) &&
                    int.TryParse(File.ReadAllText(PortFilePath).Trim(), out var port) &&
                    port is > 0 and <= 65535)
                {
                    return port;
                }
            }
            catch
            {
                // Fall through to the configured default.
            }

            return HubConstants.DefaultHubPort;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsReady())
        {
            return;
        }

        var executable = Path.Combine(AppContext.BaseDirectory, "RemoteDeskHub.HubHost.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                "The headless hub host executable was not found next to the console.", executable);
        }

        logger.LogInformation("Starting headless hub host: {Executable}", executable);
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("--data");
        startInfo.ArgumentList.Add(paths.RootDirectory);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The hub host process could not be started.");

        var deadline = DateTime.UtcNow + ReadyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReady())
            {
                _startedByThisConsole = true;
                logger.LogInformation("Headless hub host is ready on port {Port}.", BoundPort);
                return;
            }

            if (process.HasExited && !IsReady())
            {
                // A second host may have won the single-instance race; give it a moment to become
                // ready before treating the exit as a failure.
                await Task.Delay(250, cancellationToken);
                if (IsReady())
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"The hub host exited before becoming ready (exit code {process.ExitCode}).");
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException($"The hub host did not become ready within {ReadyTimeout.TotalSeconds:0} seconds.");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsReady())
        {
            return;
        }

        try
        {
            using var stopEvent = EventWaitHandle.OpenExisting(StopEventName);
            stopEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Could not signal the hub host to stop.");
            return;
        }

        var deadline = DateTime.UtcNow + StopTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsReady())
            {
                _startedByThisConsole = false;
                logger.LogInformation("Headless hub host stopped.");
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        // The host did not honour the stop event. Kill it by PID as a last resort so Settings can
        // restart the listener on a new port.
        KillHostProcess();
        if (!IsReady())
        {
            _startedByThisConsole = false;
            logger.LogWarning("Headless hub host was terminated after the stop timeout.");
            return;
        }

        throw new TimeoutException($"The hub host did not stop within {StopTimeout.TotalSeconds:0} seconds.");
    }

    private bool IsReady()
    {
        try
        {
            using var ready = EventWaitHandle.OpenExisting(ReadyEventName);
            return ready.WaitOne(0);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void KillHostProcess()
    {
        try
        {
            if (!File.Exists(PidFilePath) || !int.TryParse(File.ReadAllText(PidFilePath).Trim(), out var pid))
            {
                return;
            }

            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not terminate the hub host process.");
        }
    }
}
