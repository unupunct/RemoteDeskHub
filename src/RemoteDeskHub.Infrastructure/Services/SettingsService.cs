using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class SettingsService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    ISessionContext session,
    IClock clock) : ISettingsService
{
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _loaded;

    public async Task SeedDefaultsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ApplicationSettings.Select(s => s.Key).ToListAsync(cancellationToken);
        foreach (var (key, value, category) in DatabaseInitializer.GetDefaultSettings())
        {
            if (!existing.Contains(key))
            {
                db.ApplicationSettings.Add(new ApplicationSetting { Key = key, Value = value, Category = category, UpdatedUtc = clock.UtcNow });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        _loaded = false;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _cache.TryGetValue(key, out var value) ? value : null;
    }

    public async Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken cancellationToken = default)
    {
        var raw = await GetAsync(key, cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        try
        {
            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            if (target == typeof(string))
            {
                return (T)(object)raw;
            }

            if (target == typeof(bool))
            {
                return (T)(object)bool.Parse(raw);
            }

            if (target == typeof(int))
            {
                return (T)(object)int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            }

            if (target == typeof(long))
            {
                return (T)(object)long.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            }

            if (target == typeof(double))
            {
                return (T)(object)double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            }

            if (target == typeof(TimeSpan))
            {
                return (T)(object)TimeSpan.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            }

            return (T)Convert.ChangeType(raw, target, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    public async Task SetUserAsync(string actor, string key, string? value, string category, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageSettings, actor);
        await SetAsync(key, value, category, cancellationToken);
    }

    public async Task SetAsync(string key, string? value, string category, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.ApplicationSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (entry is null)
        {
            entry = new ApplicationSetting { Key = key, Category = category };
            db.ApplicationSettings.Add(entry);
        }

        entry.Value = value;
        entry.UpdatedUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        _cache[key] = value;
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ApplicationSettings.AsNoTracking()
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.ApplicationSettings.AsNoTracking().ToListAsync(cancellationToken);
        foreach (var item in items)
        {
            _cache[item.Key] = item.Value;
        }

        _loaded = true;
    }
}
