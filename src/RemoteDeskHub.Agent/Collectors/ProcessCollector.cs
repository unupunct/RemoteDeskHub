using System.Diagnostics;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Agent.Collectors;

/// <summary>Enumerates running processes with a short CPU sampling window.</summary>
public sealed class ProcessCollector
{
    public IReadOnlyList<RemoteProcessDto> Collect()
    {
        var processes = Process.GetProcesses();
        var samples = new Dictionary<int, TimeSpan>();
        foreach (var process in processes)
        {
            try
            {
                samples[process.Id] = process.TotalProcessorTime;
            }
            catch
            {
                samples[process.Id] = TimeSpan.Zero;
            }
        }

        var stopwatch = Stopwatch.StartNew();
        Thread.Sleep(300);
        stopwatch.Stop();
        var elapsed = Math.Max(0.05, stopwatch.Elapsed.TotalSeconds);
        var cores = Math.Max(1, Environment.ProcessorCount);

        var results = new List<RemoteProcessDto>(processes.Length);
        foreach (var process in processes)
        {
            try
            {
                var cpuPercent = 0.0;
                if (samples.TryGetValue(process.Id, out var previous))
                {
                    try
                    {
                        var delta = process.TotalProcessorTime - previous;
                        cpuPercent = Math.Clamp(delta.TotalSeconds / elapsed / cores * 100.0, 0, 100);
                    }
                    catch
                    {
                        // Process exited during sampling.
                    }
                }

                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    // Access is denied for protected and other-user processes.
                }

                DateTimeOffset? startUtc = null;
                try
                {
                    startUtc = process.StartTime.ToUniversalTime();
                }
                catch
                {
                    // Start time is unavailable for system processes.
                }

                int sessionId = 0;
                try
                {
                    sessionId = process.SessionId;
                }
                catch
                {
                    // Ignore.
                }

                var name = process.ProcessName;
                results.Add(new RemoteProcessDto
                {
                    Name = name,
                    Pid = process.Id,
                    SessionId = sessionId,
                    CpuPercent = Math.Round(cpuPercent, 1),
                    WorkingSetBytes = SafeLong(() => process.WorkingSet64),
                    PrivateMemoryBytes = SafeLong(() => process.PrivateMemorySize64),
                    UserName = null,
                    StartTimeUtc = startUtc,
                    ExecutablePath = path,
                    CommandLine = null,
                    Status = SafeStatus(process),
                    IsProtected = ProtectedProcesses.IsProtected(name)
                });
            }
            catch
            {
                // The process exited between enumeration and inspection.
            }
            finally
            {
                process.Dispose();
            }
        }

        return results
            .OrderByDescending(p => p.CpuPercent)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static long SafeLong(Func<long> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return 0;
        }
    }

    private static string SafeStatus(Process process)
    {
        try
        {
            return process.HasExited ? "Exited" : "Running";
        }
        catch
        {
            return "Unknown";
        }
    }
}
