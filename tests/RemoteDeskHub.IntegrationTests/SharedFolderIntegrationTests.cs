using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Infrastructure.Services;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;
using RemoteDeskHub.Shared.Security;

namespace RemoteDeskHub.IntegrationTests;

public sealed class SharedFolderIntegrationTests
{
    [Fact]
    public async Task LocalFolder_ImportsChanges_HandlesConflictsAndDeletes()
    {
        await using var hub = await TestHub.CreateAsync();
        var root = Path.Combine(hub.RootDirectory, "hub-shared");
        var settings = hub.Get<ISettingsService>();
        await settings.SetAsync(SettingsKeys.SharedFolderEnabled, "true", "Shared folder");
        await settings.SetAsync(SettingsKeys.SharedFolderPath, root, "Shared folder");
        await settings.SetAsync(SettingsKeys.SharedFolderIntervalSeconds, "5", "Shared folder");

        Directory.CreateDirectory(root);
        var filePath = Path.Combine(root, "hello.txt");
        await File.WriteAllTextAsync(filePath, "first version");

        var sync = CreateSyncService(hub);
        await sync.RunOnceAsync();

        var dbFactory = hub.Get<IDbContextFactory<RemoteDeskDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var central = await db.SharedFolderFiles.SingleAsync(x => x.Path == "hello.txt");
            Assert.False(central.IsDeleted);
            Assert.Equal(Sha("first version"), central.Sha256);
            Assert.Equal("Hub local folder", central.SourceComputerName);

            var local = await db.SharedFolderLocalFiles.SingleAsync(x => x.Path == "hello.txt");
            Assert.Equal(central.Sha256, local.CentralSha256);

            var state = await db.SharedFolderSyncState.SingleAsync(x => x.Id == 1);
            Assert.Equal(1, state.Files);
        }

        Assert.True(File.Exists(BlobPath(hub, Sha("first version"))));

        // A repeat cycle must be idempotent: no new version and no duplicate rows.
        await sync.RunOnceAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var central = await db.SharedFolderFiles.SingleAsync(x => x.Path == "hello.txt");
            Assert.Equal(1, central.Version);
            Assert.Equal(1, await db.SharedFolderFiles.CountAsync(x => x.Path == "hello.txt"));
        }

        await File.WriteAllTextAsync(filePath, "second version");
        await sync.RunOnceAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var central = await db.SharedFolderFiles.SingleAsync(x => x.Path == "hello.txt");
            Assert.Equal(Sha("second version"), central.Sha256);
            Assert.Equal(2, central.Version);
        }

        // Simultaneous edits: the central copy wins, the local copy becomes an ordinary conflict file.
        await File.WriteAllTextAsync(filePath, "local conflict");
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var central = await db.SharedFolderFiles.SingleAsync(x => x.Path == "hello.txt");
            central.Sha256 = Sha("central wins");
            central.SizeBytes = Encoding.UTF8.GetByteCount("central wins");
            central.Version++;
            central.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var centralBlob = BlobPath(hub, Sha("central wins"));
        await File.WriteAllTextAsync(centralBlob, "central wins");

        await sync.RunOnceAsync();

        Assert.Equal("central wins", await File.ReadAllTextAsync(filePath));
        var conflicts = Directory.GetFiles(root, "hello (conflict from *").ToList();
        Assert.Single(conflicts);
        Assert.Equal("local conflict", await File.ReadAllTextAsync(conflicts[0]));

        // The conflict copy is an ordinary file and is uploaded on the following cycle.
        await sync.RunOnceAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var conflictRelative = Path.GetRelativePath(root, conflicts[0]);
            Assert.True(await db.SharedFolderFiles.AnyAsync(x => x.Path == conflictRelative && !x.IsDeleted));
        }

        File.Delete(filePath);
        await sync.RunOnceAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var central = await db.SharedFolderFiles.SingleAsync(x => x.Path == "hello.txt");
            Assert.True(central.IsDeleted);
            Assert.False(await db.SharedFolderLocalFiles.AnyAsync(x => x.Path == "hello.txt"));
        }
    }

    [Fact]
    public async Task AgentManifest_PullsFilesAndPushesHubEdits()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await TestHub.CreateAsync(services =>
            services.Replace(ServiceDescriptor.Singleton<IAgentTransport>(transport)));
        var root = Path.Combine(hub.RootDirectory, "hub-shared");
        var settings = hub.Get<ISettingsService>();
        await settings.SetAsync(SettingsKeys.SharedFolderEnabled, "true", "Shared folder");
        await settings.SetAsync(SettingsKeys.SharedFolderPath, root, "Shared folder");
        Directory.CreateDirectory(root);

        await hub.CreateApprovedComputerAsync("REMOTE-PC-01");
        var relative = Path.Combine("docs", "remote.txt");
        var agentPath = Path.Combine(root, relative);
        transport.Files[agentPath] = Encoding.UTF8.GetBytes("from agent");
        transport.SharedFolder = new SharedFolderManifestDto
        {
            Enabled = true,
            Root = root,
            Entries =
            [
                new SharedFolderManifestEntryDto
                {
                    Path = relative,
                    Sha256 = Sha("from agent"),
                    SizeBytes = Encoding.UTF8.GetByteCount("from agent"),
                    LastWriteUtc = DateTimeOffset.UtcNow
                }
            ]
        };

        var sync = CreateSyncService(hub);
        await sync.RunOnceAsync();

        await using (var db = await hub.Get<IDbContextFactory<RemoteDeskDbContext>>().CreateDbContextAsync())
        {
            var central = await db.SharedFolderFiles.SingleAsync(x => x.Path == relative);
            Assert.Equal(Sha("from agent"), central.Sha256);
            var agentRow = await db.SharedFolderAgentFiles.SingleAsync(x => x.Path == relative);
            Assert.Equal(central.Sha256, agentRow.CentralSha256);
        }

        // The hub's local folder participates too, so the pulled file must appear there.
        Assert.Equal("from agent", await File.ReadAllTextAsync(Path.Combine(root, relative)));

        // Editing the hub-local copy pushes through the central store to the agent.
        await File.WriteAllTextAsync(Path.Combine(root, relative), "from hub");
        await sync.RunOnceAsync();

        await using (var db = await hub.Get<IDbContextFactory<RemoteDeskDbContext>>().CreateDbContextAsync())
        {
            var central = await db.SharedFolderFiles.SingleAsync(x => x.Path == relative);
            Assert.Equal(Sha("from hub"), central.Sha256);
            var agentRow = await db.SharedFolderAgentFiles.SingleAsync(x => x.Path == relative);
            Assert.Equal(central.Sha256, agentRow.CentralSha256);
        }

        Assert.Equal("from hub", Encoding.UTF8.GetString(transport.Files[agentPath]));
    }

    [Fact]
    public async Task AddLocalFilesAndFolders_CopiesIntoSeparateSharedRoot()    {
        await using var hub = await TestHub.CreateAsync();
        var root = Path.Combine(hub.RootDirectory, "hub-shared");
        var source = Path.Combine(hub.RootDirectory, "source");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        var sourceFile = Path.Combine(source, "report.txt");
        var nestedFile = Path.Combine(source, "nested", "data.bin");
        await File.WriteAllTextAsync(sourceFile, "report");
        await File.WriteAllTextAsync(nestedFile, "data");

        var settings = hub.Get<ISettingsService>();
        await settings.SetAsync(SettingsKeys.SharedFolderPath, root, "Shared folder");
        var service = hub.Get<ISharedFolderService>();

        var result = await service.AddLocalFilesAsync("admin", [sourceFile]);
        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(root, "report.txt")));

        // Adding the same file again preserves both versions instead of overwriting the first.
        var duplicate = await service.AddLocalFilesAsync("admin", [sourceFile]);
        Assert.True(duplicate.Success, duplicate.Error);
        Assert.True(File.Exists(Path.Combine(root, "report (2).txt")));

        var folderResult = await service.AddLocalFolderAsync("admin", source);
        Assert.True(folderResult.Success, folderResult.Error);
        Assert.True(File.Exists(Path.Combine(root, "source", "nested", "data.bin")));
    }

    private static SharedFolderSyncService CreateSyncService(TestHub hub) =>
        new(
            hub.Get<IDbContextFactory<RemoteDeskDbContext>>(),
            hub.Get<IAgentTransport>(),
            hub.Get<IAppPaths>(),
            hub.Get<ISettingsService>(),
            hub.Get<IAuditService>(),
            hub.Get<IAppEvents>(),
            hub.Get<IClock>(),
            hub.Get<SharedFolderRootManager>(),
            NullLogger<SharedFolderSyncService>.Instance);

    private static string Sha(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string BlobPath(TestHub hub, string sha) =>
        Path.Combine(hub.Paths.SharedFolderBlobsDirectory, sha.ToUpperInvariant());
}
