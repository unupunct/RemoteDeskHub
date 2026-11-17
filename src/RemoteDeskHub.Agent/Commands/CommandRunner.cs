using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Agent.Commands;

/// <summary>
/// Executes remote shell commands with hard timeouts, cancellation and bounded output.
/// Commands run under the agent's service account; the agent never elevates itself.
/// </summary>
public sealed class CommandRunner(ILogger<CommandRunner> logger)
{
    private readonly ConcurrentDictionary<Guid, RunningCommand> _running = new();

    public async Task<CommandResultDto> ExecuteAsync(CommandRequestDto request, int maxTimeoutSeconds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
        {
            throw new ArgumentException("A command is required.", nameof(request));
        }

        if (request.ExpiresUtc < DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(5)))
        {
            throw new InvalidOperationException("The command request has expired and was rejected (replay protection).");
        }

        var timeoutSeconds = Math.Clamp(request.TimeoutSeconds <= 0 ? HubConstants.DefaultCommandTimeoutSeconds : request.TimeoutSeconds,
            1, Math.Clamp(maxTimeoutSeconds, 1, HubConstants.MaxCommandTimeoutSeconds));

        var (fileName, arguments) = BuildProcessStart(request);
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var entry = new RunningCommand(linked);
        _running[request.CorrelationId] = entry;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The command process could not be started.");
            }

            logger.LogInformation("Command {CorrelationId} started ({Shell}) for request issued by {IssuedBy}.",
                request.CorrelationId, request.Shell, request.IssuedBy ?? "unknown");

            var stdoutTask = ReadBoundedAsync(process.StandardOutput, HubConstants.MaxCommandOutputChars, linked.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, HubConstants.MaxCommandOutputChars, linked.Token);

            var timedOut = false;
            var cancelled = false;
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = !entry.UserCancelled && !cancellationToken.IsCancellationRequested;
                cancelled = entry.UserCancelled;
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            string stdout;
            string stderr;
            var truncated = false;
            try
            {
                var stdoutResult = await stdoutTask.ConfigureAwait(false);
                var stderrResult = await stderrTask.ConfigureAwait(false);
                stdout = stdoutResult.Text;
                stderr = stderrResult.Text;
                truncated = stdoutResult.Truncated || stderrResult.Truncated;
            }
            catch (OperationCanceledException)
            {
                stdout = string.Empty;
                stderr = string.Empty;
            }

            stopwatch.Stop();
            var exitCode = safeExitCode(process);
            return new CommandResultDto
            {
                CorrelationId = request.CorrelationId,
                ExitCode = timedOut || cancelled ? -1 : exitCode,
                StdOut = stdout,
                StdErr = stderr,
                StartedUtc = startedUtc,
                CompletedUtc = DateTimeOffset.UtcNow,
                TimedOut = timedOut,
                Cancelled = cancelled,
                Error = timedOut
                    ? $"The command exceeded the {timeoutSeconds} second timeout and was terminated."
                    : cancelled ? "The command was cancelled by the operator." : null,
                ExecutionIdentity = $"{Environment.UserDomainName}\\{Environment.UserName}",
                DurationMs = (long)stopwatch.Elapsed.TotalMilliseconds,
                OutputTruncated = truncated
            };
        }
        finally
        {
            _running.TryRemove(request.CorrelationId, out _);
        }

        static int safeExitCode(Process process)
        {
            try
            {
                return process.ExitCode;
            }
            catch
            {
                return -1;
            }
        }
    }

    /// <summary>
    /// Reads a process stream while retaining at most <paramref name="maxChars"/> characters. The
    /// stream is still drained after the cap so a chatty child process cannot block on a full pipe
    /// and consume unbounded memory.
    /// </summary>
    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(
        StreamReader reader, int maxChars, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maxChars, 16 * 1024));
        var buffer = new char[8192];
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = maxChars - builder.Length;
            if (remaining > 0)
            {
                var take = Math.Min(read, remaining);
                builder.Append(buffer, 0, take);
                truncated |= take < read;
            }
            else
            {
                truncated = true;
            }
        }

        return (builder.ToString(), truncated);
    }

    public bool Cancel(Guid correlationId)
    {
        if (!_running.TryGetValue(correlationId, out var entry))
        {
            return false;
        }

        entry.UserCancelled = true;
        try
        {
            entry.Cancellation.Cancel();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (string FileName, IReadOnlyList<string> Arguments) BuildProcessStart(CommandRequestDto request)
    {
        if (request.Shell == CommandShell.Cmd)
        {
            return ("cmd.exe", new[] { "/d", "/s", "/c", request.Command });
        }

        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(powershell))
        {
            powershell = "powershell.exe";
        }

        return (powershell, new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-OutputFormat", "Text", "-Command", request.Command
        });
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have exited between the check and the kill.
        }
    }

    private sealed class RunningCommand(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public bool UserCancelled { get; set; }
    }
}
