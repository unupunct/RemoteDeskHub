using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Agent.State;

/// <summary>
/// Durable local buffer for telemetry samples that could not be delivered while the hub was
/// offline. Samples are flushed oldest-first after the agent reconnects. The queue is bounded so a
/// long outage cannot exhaust the disk.
/// </summary>
public sealed class AgentTelemetryQueue(AgentOptions options, ILogger<AgentTelemetryQueue> logger)
{
    private const int MaxSamples = 10_000;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<AgentMetricsDto>? _samples;
    private string QueuePath => Path.Combine(options.WorkDirectory, "telemetry-queue.json");

    public async Task EnqueueAsync(AgentMetricsDto sample, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var samples = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            samples.Add(sample);
            if (samples.Count > MaxSamples)
            {
                var overflow = samples.Count - MaxSamples;
                samples.RemoveRange(0, overflow);
                logger.LogWarning("Telemetry queue exceeded {Max} samples; discarded the {Overflow} oldest sample(s).",
                    MaxSamples, overflow);
            }

            await SaveUnlockedAsync(samples, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AgentMetricsDto>> PeekAsync(int max, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var samples = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            return samples.Take(Math.Clamp(max, 1, MaxSamples)).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AcknowledgeAsync(int count, CancellationToken cancellationToken = default)
    {
        if (count <= 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var samples = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            samples.RemoveRange(0, Math.Min(count, samples.Count));
            await SaveUnlockedAsync(samples, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<AgentMetricsDto>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (_samples is not null)
        {
            return _samples;
        }

        options.EnsureDirectories();
        if (!File.Exists(QueuePath))
        {
            _samples = [];
            return _samples;
        }

        try
        {
            var json = await File.ReadAllTextAsync(QueuePath, cancellationToken).ConfigureAwait(false);
            _samples = JsonSerializer.Deserialize<List<AgentMetricsDto>>(json, JsonDefaults.Options) ?? [];
            if (_samples.Count > MaxSamples)
            {
                _samples.RemoveRange(0, _samples.Count - MaxSamples);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The local telemetry queue at {Path} could not be read; starting a new queue.",
                QueuePath);
            _samples = [];
        }

        return _samples;
    }

    private async Task SaveUnlockedAsync(List<AgentMetricsDto> samples, CancellationToken cancellationToken)
    {
        options.EnsureDirectories();
        if (samples.Count == 0)
        {
            if (File.Exists(QueuePath))
            {
                File.Delete(QueuePath);
            }

            return;
        }

        var json = JsonSerializer.Serialize(samples, JsonDefaults.Options);
        var temp = QueuePath + ".tmp";
        await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temp, QueuePath, true);
    }
}
