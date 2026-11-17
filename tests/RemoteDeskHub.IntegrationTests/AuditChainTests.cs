using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Infrastructure.Services;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.IntegrationTests;

public sealed class AuditChainTests
{
    [Fact]
    public async Task EmptyLog_Verifies()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        hub.Session.SignIn(new SessionUser(Guid.NewGuid(), "test-admin", UserRole.Administrator, DateTime.UtcNow));
        var audit = hub.Get<IAuditService>();

        var (valid, message) = await audit.VerifyChainAsync();

        Assert.True(valid);
        Assert.Contains("empty", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Entries_AreHashLinked()
    {
        await using var hub = await TestHub.CreateAsync();
        var audit = hub.Get<IAuditService>();

        await audit.LogAsync("admin", "Test.One", details: "first");
        await audit.LogAsync("admin", "Test.Two", details: "second");
        await audit.LogAsync("admin", "Test.Three", details: "third");

        await using var db = await hub.Get<IDbContextFactory<RemoteDeskDbContext>>().CreateDbContextAsync();
        var entries = await db.AuditLogs.OrderBy(a => a.Id).ToListAsync();

        Assert.True(entries.Count >= 3);
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.Equal(entries[i - 1].Hash, entries[i].PrevHash);
            Assert.Equal(entries[i].Hash, AuditService.ComputeHash(entries[i]));
        }

        var (valid, message) = await audit.VerifyChainAsync();
        Assert.True(valid, message);
    }

    [Fact]
    public async Task TamperedEntry_BreaksChain()
    {
        await using var hub = await TestHub.CreateAsync();
        var audit = hub.Get<IAuditService>();
        await audit.LogAsync("admin", "Test.Before");
        await audit.LogAsync("admin", "Test.After");

        await using (var db = await hub.Get<IDbContextFactory<RemoteDeskDbContext>>().CreateDbContextAsync())
        {
            var target = await db.AuditLogs.OrderBy(a => a.Id).Skip(1).FirstAsync();
            target.Details = "tampered evidence";
            await db.SaveChangesAsync();
        }

        var (valid, message) = await audit.VerifyChainAsync();

        Assert.False(valid);
        Assert.Contains("modified", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeletedEntry_BreaksChain()
    {
        await using var hub = await TestHub.CreateAsync();
        var audit = hub.Get<IAuditService>();
        await audit.LogAsync("admin", "Test.One");
        await audit.LogAsync("admin", "Test.Two");
        await audit.LogAsync("admin", "Test.Three");

        await using (var db = await hub.Get<IDbContextFactory<RemoteDeskDbContext>>().CreateDbContextAsync())
        {
            var middle = await db.AuditLogs.OrderBy(a => a.Id).Skip(1).FirstAsync();
            db.AuditLogs.Remove(middle);
            await db.SaveChangesAsync();
        }

        var (valid, message) = await audit.VerifyChainAsync();

        Assert.False(valid);
        Assert.Contains("Chain break", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Query_FiltersBySearchAndTime()
    {
        await using var hub = await TestHub.CreateAsync();
        var audit = hub.Get<IAuditService>();
        var start = hub.Clock.UtcNow;

        await audit.LogAsync("admin", "Computer.Added", computerName: "PC-ALPHA", details: "added alpha");
        await audit.LogAsync("admin", "Remote.CommandExecuted", computerName: "PC-BETA", details: "ran whoami");

        hub.Clock.Advance(TimeSpan.FromSeconds(5));
        await audit.LogAsync("admin", "Computer.Removed", computerName: "PC-GAMMA", details: "removed gamma");

        var alpha = await audit.QueryAsync(null, null, "ALPHA", 50);
        Assert.Single(alpha);
        Assert.Equal("Computer.Added", alpha[0].Operation);

        var recent = await audit.QueryAsync(hub.Clock.UtcNow.AddSeconds(-1), null, null, 50);
        Assert.Single(recent);
        Assert.Equal("Computer.Removed", recent[0].Operation);

        var all = await audit.QueryAsync(null, null, null, 50);
        Assert.True(all.Count >= 3);
        Assert.True(all[0].Id > all[^1].Id || all[0].TimestampUtc >= all[^1].TimestampUtc);
        Assert.True(start <= hub.Clock.UtcNow);
    }

    [Fact]
    public async Task PurgeOld_KeepsEntriesWithinRetention()
    {
        await using var hub = await TestHub.CreateAsync();
        var audit = hub.Get<IAuditService>();
        await audit.LogAsync("admin", "Test.Purge.Keep");

        var purged = await audit.PurgeOldAsync();

        Assert.Equal(0, purged);
        Assert.NotEmpty(await audit.QueryAsync(null, null, "Test.Purge.Keep", 10));
    }

    [Fact]
    public async Task PurgeOld_RemovesExpiredEntries_AndKeepsChainValid()
    {
        await using var hub = await TestHub.CreateAsync();
        var audit = hub.Get<IAuditService>();
        await audit.LogAsync("admin", "Test.Purge.Old");
        hub.Clock.Advance(TimeSpan.FromDays(800));
        await audit.LogAsync("admin", "Test.Purge.New");

        var purged = await audit.PurgeOldAsync();

        Assert.True(purged >= 1, $"Expected at least one purged entry, got {purged}.");
        Assert.DoesNotContain(await audit.QueryAsync(null, null, "Test.Purge.Old", 10), e => e.Operation == "Test.Purge.Old");
        Assert.Contains(await audit.QueryAsync(null, null, "Test.Purge.New", 10), e => e.Operation == "Test.Purge.New");
        Assert.Contains(await audit.QueryAsync(null, null, "Audit.Purged", 10), e => e.Operation == "Audit.Purged");
        var chain = await audit.VerifyChainAsync();
        Assert.True(chain.Valid, chain.Message);
    }

    [Fact]
    public async Task Actor_DefaultsToSystemWhenBlank()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        hub.Session.SignIn(new SessionUser(Guid.NewGuid(), "test-admin", UserRole.Administrator, DateTime.UtcNow));
        var audit = hub.Get<IAuditService>();

        await audit.LogAsync("   ", "Test.System");

        var entries = await audit.QueryAsync(null, null, "Test.System", 10);
        Assert.Equal(HubConstants.AuditActorSystem, entries[0].Actor);
    }
}
