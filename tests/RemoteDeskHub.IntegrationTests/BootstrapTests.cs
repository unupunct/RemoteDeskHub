using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.IntegrationTests;

public sealed class BootstrapTests
{
    [Fact]
    public async Task NewDatabase_IsFirstRun_AndCreatesSchema()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);

        Assert.True(File.Exists(hub.Paths.DatabasePath));
        Assert.True(await hub.Get<IDatabaseInitializer>().IsFirstRunAsync());
    }

    [Fact]
    public async Task AfterAdministratorCreated_IsNoLongerFirstRun()
    {
        await using var hub = await TestHub.CreateAsync();

        Assert.False(await hub.Get<IDatabaseInitializer>().IsFirstRunAsync());
    }

    [Fact]
    public async Task Seeding_CreatesGroupsAlertRulesAndSettings()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        var factory = hub.Get<IDbContextFactory<RemoteDeskDbContext>>();

        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(5, await db.ComputerGroups.CountAsync());
        Assert.Equal(5, await db.AlertRules.CountAsync());
        Assert.True(await db.ApplicationSettings.CountAsync() >= 25);
        Assert.Equal(1, await db.SchemaVersions.CountAsync());
    }

    [Fact]
    public async Task Initialization_IsIdempotent()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        var initializer = hub.Get<IDatabaseInitializer>();
        var factory = hub.Get<IDbContextFactory<RemoteDeskDbContext>>();

        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(5, await db.ComputerGroups.CountAsync());
        Assert.Equal(5, await db.AlertRules.CountAsync());
    }

    [Fact]
    public async Task Settings_SetAndGetTypedValues()
    {
        await using var hub = await TestHub.CreateAsync();
        var settings = hub.Get<ISettingsService>();

        Assert.Equal(HubConstants.DefaultHubPort, await settings.GetAsync(SettingsKeys.HubPort, 7443));

        await settings.SetAsync(SettingsKeys.AlertCpuThreshold, "77.5", "Monitoring");
        Assert.Equal(77.5, await settings.GetAsync(SettingsKeys.AlertCpuThreshold, 90d));
        Assert.Equal("77.5", await settings.GetAsync(SettingsKeys.AlertCpuThreshold));
    }

    [Fact]
    public async Task Settings_InvalidValue_FallsBackToDefault()
    {
        await using var hub = await TestHub.CreateAsync();
        var settings = hub.Get<ISettingsService>();

        await settings.SetAsync(SettingsKeys.HubPort, "not-a-number", "Network");

        Assert.Equal(7443, await settings.GetAsync(SettingsKeys.HubPort, 7443));
    }

    [Fact]
    public async Task Backup_CreateAndVerify()
    {
        await using var hub = await TestHub.CreateAsync();
        var backups = hub.Get<IBackupService>();

        var created = await backups.CreateBackupAsync(TestHub.AdminUserName);
        Assert.True(created.Success, created.Error);
        Assert.True(File.Exists(Path.Combine(hub.Paths.BackupsDirectory, created.Value!)));

        var listed = await backups.ListBackupsAsync();
        Assert.Contains(listed, file => file.FullName == created.Value);

        var verified = await backups.VerifyDatabaseAsync();
        Assert.True(verified.Success, verified.Error);
    }

    [Fact]
    public async Task Restore_IsStaged_AndDoesNotOverwriteLiveDatabase()
    {
        await using var hub = await TestHub.CreateAsync();
        var backups = hub.Get<IBackupService>();

        var created = await backups.CreateBackupAsync(TestHub.AdminUserName);
        Assert.True(created.Success, created.Error);

        var restore = await backups.RestoreBackupAsync(TestHub.AdminUserName, Path.GetFileName(created.Value!));
        Assert.True(restore.Success, restore.Error);
        Assert.Contains("restart", restore.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(hub.Paths.DatabasePath + ".restore-pending"));
        Assert.True(File.Exists(hub.Paths.DatabasePath + ".restore-pending.marker"));

        // The live database is untouched until the next startup.
        Assert.True((await backups.VerifyDatabaseAsync()).Success);
    }

    [Fact]
    public async Task Restore_StagedDatabase_IsAppliedOnNextStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemoteDeskHubTests", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var hub = await TestHub.CreateAsync(dataRoot: root, deleteOnDispose: false))
            {
                await hub.CreateApprovedComputerAsync("BEFORE-PC", "192.168.1.11");
                var backups = hub.Get<IBackupService>();
                var created = await backups.CreateBackupAsync(TestHub.AdminUserName);
                Assert.True(created.Success, created.Error);

                await hub.CreateApprovedComputerAsync("AFTER-PC", "192.168.1.12");
                var restore = await backups.RestoreBackupAsync(
                    TestHub.AdminUserName, Path.GetFileName(created.Value!));
                Assert.True(restore.Success, restore.Error);
            }

            // The first hub is disposed; in a real restart the process exit also releases the
            // SQLite pool. Clear it explicitly to simulate a fresh process in-process.
            SqliteConnection.ClearAllPools();

            await using (var hub = await TestHub.CreateAsync(
                             createAdministrator: false, dataRoot: root, deleteOnDispose: false))
            {
                await using var db = await hub.Get<IDbContextFactory<RemoteDeskDbContext>>().CreateDbContextAsync();
                Assert.True(await db.Computers.AnyAsync(c => c.Name == "BEFORE-PC"));
                Assert.False(await db.Computers.AnyAsync(c => c.Name == "AFTER-PC"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
