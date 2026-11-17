using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class AuditService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    ISettingsService settings,
    ISessionContext session,
    IClock clock,
    IAppPaths paths,
    ILogger<AuditService> logger) : IAuditService
{
    private static readonly SemaphoreSlim ChainLock = new(1, 1);
    private const string ProcessLockFileName = "audit.lock";

    public async Task LogAsync(
        string actor,
        string operation,
        string? computerName = null,
        Guid? computerId = null,
        string result = "Success",
        long durationMs = 0,
        string? error = null,
        string? details = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            actor = HubConstants.AuditActorSystem;
        }

        await ChainLock.WaitAsync(cancellationToken);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken);
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var previous = await db.AuditLogs.AsNoTracking()
                .OrderByDescending(a => a.Id)
                .Select(a => a.Hash)
                .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

            var entry = new AuditLog
            {
                TimestampUtc = clock.UtcNow,
                Actor = Truncate(actor, 128)!,
                Operation = Truncate(operation, 128)!,
                ComputerId = computerId,
                ComputerName = Truncate(computerName, 128),
                Result = Truncate(result, 32)!,
                DurationMs = Math.Max(0, durationMs),
                Error = Truncate(error, 4000),
                Details = Truncate(details, 8000),
                IpAddress = Truncate(ipAddress, 64),
                PrevHash = previous
            };
            entry.Hash = ComputeHash(entry);
            db.AuditLogs.Add(entry);
            await db.SaveChangesAsync(cancellationToken);
            await AppendAnchorAsync(entry, cancellationToken);
        }
        catch (Exception ex)
        {
            // Auditing must never break the operation that is being audited, but a failed audit
            // write must leave a diagnostic trace instead of being swallowed silently.
            logger.LogError(ex, "Could not write audit entry {Operation} for actor {Actor}.", operation, actor);
        }
        finally
        {
            ChainLock.Release();
        }
    }

    public async Task<IReadOnlyList<AuditView>> QueryAsync(DateTime? fromUtc, DateTime? toUtc, string? search, int limit, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewAudit);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.AuditLogs.AsNoTracking().AsQueryable();
        if (fromUtc.HasValue)
        {
            query = query.Where(a => a.TimestampUtc >= fromUtc.Value);
        }

        if (toUtc.HasValue)
        {
            query = query.Where(a => a.TimestampUtc <= toUtc.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(a =>
                EF.Functions.Like(a.Actor, pattern) ||
                EF.Functions.Like(a.Operation, pattern) ||
                EF.Functions.Like(a.ComputerName ?? string.Empty, pattern) ||
                EF.Functions.Like(a.Details ?? string.Empty, pattern));
        }

        return await query
            .OrderByDescending(a => a.Id)
            .Take(Math.Clamp(limit, 1, 5000))
            .Select(a => new AuditView
            {
                Id = a.Id,
                TimestampUtc = a.TimestampUtc,
                Actor = a.Actor,
                ComputerName = a.ComputerName,
                Operation = a.Operation,
                Result = a.Result,
                DurationMs = a.DurationMs,
                Error = a.Error,
                Details = a.Details,
                IpAddress = a.IpAddress
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<(bool Valid, string Message)> VerifyChainAsync(CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewAudit);
        await using var processLock = await AcquireProcessLockAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entries = await db.AuditLogs.AsNoTracking().OrderBy(a => a.Id).ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            return (true, "The audit log is empty.");
        }

        for (var i = 1; i < entries.Count; i++)
        {
            var expected = entries[i - 1].Hash;
            if (!string.Equals(entries[i].PrevHash, expected, StringComparison.Ordinal))
            {
                return (false, $"Chain break detected at audit record #{entries[i].Id}.");
            }

            if (!string.Equals(entries[i].Hash, ComputeHash(entries[i]), StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Audit record #{entries[i].Id} was modified after it was written.");
            }
        }

        if (!string.Equals(entries[0].Hash, ComputeHash(entries[0]), StringComparison.OrdinalIgnoreCase))
        {
            return (false, "The first audit record was modified after it was written.");
        }

        return (true, $"{entries.Count} audit records verified. Hash chain is intact.");
    }

    public async Task<int> PurgeOldAsync(CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageSettings);
        // Retention pruning is always an explicit administrator action. The configured retention
        // window decides which records are eligible; a purge record is appended so the remaining
        // hash chain still verifies.
        var retentionDays = await settings.GetAsync(SettingsKeys.AuditRetentionDays, 365, cancellationToken);
        var cutoff = clock.UtcNow.AddDays(-Math.Clamp(retentionDays, 1, 3650));

        await ChainLock.WaitAsync(cancellationToken);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken);
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var expired = await db.AuditLogs.Where(a => a.TimestampUtc < cutoff).ToListAsync(cancellationToken);
            if (expired.Count == 0)
            {
                return 0;
            }

            db.AuditLogs.RemoveRange(expired);
            await db.SaveChangesAsync(cancellationToken);

            var previous = await db.AuditLogs.AsNoTracking()
                .OrderByDescending(a => a.Id)
                .Select(a => a.Hash)
                .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

            var entry = new AuditLog
            {
                TimestampUtc = clock.UtcNow,
                Actor = HubConstants.AuditActorSystem,
                Operation = "Audit.Purged",
                Result = "Success",
                Details = $"Purged {expired.Count} audit entries older than {cutoff:O} (retention {retentionDays} days).",
                PrevHash = previous
            };
            entry.Hash = ComputeHash(entry);
            db.AuditLogs.Add(entry);
            await db.SaveChangesAsync(cancellationToken);
            await AppendAnchorAsync(entry, cancellationToken);

            return expired.Count;
        }
        finally
        {
            ChainLock.Release();
        }
    }

    private async Task AppendAnchorAsync(AuditLog entry, CancellationToken cancellationToken)
    {
        string? anchorPath = null;
        try
        {
            anchorPath = await settings.GetAsync(SettingsKeys.AuditAnchorPath, string.Empty, cancellationToken);
            if (string.IsNullOrWhiteSpace(anchorPath))
            {
                return;
            }

            anchorPath = anchorPath.Trim();
            var directory = Path.GetDirectoryName(anchorPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var record = new
            {
                timestampUtc = new DateTime(entry.TimestampUtc.Ticks, DateTimeKind.Utc),
                actor = entry.Actor,
                operation = entry.Operation,
                computerName = entry.ComputerName,
                computerId = entry.ComputerId,
                result = entry.Result,
                durationMs = entry.DurationMs,
                error = entry.Error,
                details = entry.Details,
                ipAddress = entry.IpAddress,
                prevHash = entry.PrevHash,
                hash = entry.Hash
            };

            // The off-box anchor is append-only JSON Lines. It intentionally duplicates the
            // already-redacted audit fields; no tool passwords, tokens or pairing secrets are
            // written here.
            await File.AppendAllTextAsync(
                anchorPath,
                JsonDefaults.Serialize(record) + Environment.NewLine,
                Encoding.UTF8,
                cancellationToken);
        }
        catch (Exception ex)
        {
            // A failed anchor must not break the audited operation. The database chain remains
            // authoritative; this warning records that the copy did not reach the configured path.
            logger.LogWarning(ex, "Could not append audit record {Operation} to the configured anchor path {AnchorPath}.",
                entry.Operation, anchorPath);
        }
    }

    private async Task<FileStream> AcquireProcessLockAsync(CancellationToken cancellationToken)
    {
        // The desktop console and the headless hub host are separate processes that share the
        // same SQLite audit chain. A lock file serialises the read-previous/append sequence across
        // processes and is released by Windows if either process crashes.
        return await ProcessFileLock.AcquireAsync(paths.RootDirectory, ProcessLockFileName, cancellationToken);
    }

    public static string ComputeHash(AuditLog entry)
    {
        // SQLite stores DateTime without its Kind, so normalise the timestamp to a UTC round-trip
        // format. Without this, every chain verification after a database read would fail.
        var timestamp = new DateTime(entry.TimestampUtc.Ticks, DateTimeKind.Utc);
        var canonical = string.Join('|',
            timestamp.ToString("O"),
            entry.Actor,
            entry.Operation,
            entry.ComputerName ?? string.Empty,
            entry.Result,
            entry.DurationMs.ToString(),
            entry.Error ?? string.Empty,
            entry.Details ?? string.Empty,
            entry.PrevHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];
}
