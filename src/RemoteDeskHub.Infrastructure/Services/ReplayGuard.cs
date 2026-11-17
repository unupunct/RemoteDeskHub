using System.Collections.Concurrent;
using RemoteDeskHub.Core.Abstractions;

namespace RemoteDeskHub.Infrastructure.Services;

/// <summary>
/// Bounded in-memory anti-replay cache for authenticated agent requests. A request is accepted
/// once per nonce inside the retention window, and only when its timestamp is close enough to the
/// hub clock. The cache is intentionally small and self-pruning; a hub restart clears it, which is
/// acceptable because the heartbeat payload is idempotent and the certificate identity is checked
/// on every request.
/// </summary>
public sealed class ReplayGuard(IClock clock)
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private long _lastSweepTicks = DateTime.MinValue.Ticks;

    public bool TryAccept(string scope, string? nonce, DateTimeOffset timestampUtc, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(nonce) || nonce.Length > 128)
        {
            error = "A request nonce of 1 to 128 characters is required.";
            return false;
        }

        var now = clock.UtcNow;
        var timestamp = timestampUtc.UtcDateTime;
        if (timestamp < now - MaxClockSkew || timestamp > now + MaxClockSkew)
        {
            error = "The request timestamp is outside the permitted clock skew.";
            return false;
        }

        var key = scope + "|" + nonce;
        if (_seen.TryAdd(key, now.Add(Retention)))
        {
            Sweep(now);
            return true;
        }

        if (_seen.TryGetValue(key, out var expires) && expires <= now)
        {
            _seen[key] = now.Add(Retention);
            return true;
        }

        error = "This request nonce has already been processed.";
        return false;
    }

    private void Sweep(DateTime now)
    {
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.Ticks - last < TimeSpan.TicksPerMinute)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastSweepTicks, now.Ticks, last) != last)
        {
            return;
        }

        foreach (var pair in _seen)
        {
            if (pair.Value <= now)
            {
                _seen.TryRemove(pair.Key, out _);
            }
        }
    }
}
