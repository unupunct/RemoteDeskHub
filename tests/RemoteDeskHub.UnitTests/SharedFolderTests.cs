using System.Security.Cryptography;
using RemoteDeskHub.Infrastructure.Services;
using RemoteDeskHub.Shared.Protocol;
using RemoteDeskHub.Shared.Security;

namespace RemoteDeskHub.UnitTests;

public sealed class SharedFolderPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void FirstSync_PullsEveryAgentFile()
    {
        var entry = Entry("docs/report.txt", "AAAA");
        var plan = SharedFolderPlanner.Plan([], [], [entry], "WORKSTATION", Now);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(SharedFolderAction.PullFromAgent, operation.Action);
        Assert.Equal(entry.Path, operation.Path);
    }

    [Fact]
    public void MatchingHashes_AreNoChange()
    {
        var central = CentralFile("a.txt", "AAAA");
        var previous = AgentFile("a.txt", "AAAA", "AAAA");
        var plan = SharedFolderPlanner.Plan([central], [previous], [Entry("a.txt", "AAAA")], "WORKSTATION", Now);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(SharedFolderAction.NoChange, operation.Action);
    }

    [Fact]
    public void LocalEdit_WithUnchangedCentral_PullsLocalVersion()
    {
        var central = CentralFile("a.txt", "AAAA");
        var previous = AgentFile("a.txt", "BBBB", "AAAA");
        var plan = SharedFolderPlanner.Plan([central], [previous], [Entry("a.txt", "CCCC")], "WORKSTATION", Now);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(SharedFolderAction.PullFromAgent, operation.Action);
    }

    [Fact]
    public void CentralEdit_WithUnchangedLocal_PushesCentralVersion()
    {
        var central = CentralFile("a.txt", "CCCC");
        var previous = AgentFile("a.txt", "BBBB", "AAAA");
        var plan = SharedFolderPlanner.Plan([central], [previous], [Entry("a.txt", "BBBB")], "WORKSTATION", Now);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(SharedFolderAction.PushToAgent, operation.Action);
    }

    [Fact]
    public void SimultaneousEdit_PreservesLocalAsConflictCopy()
    {
        var central = CentralFile("docs/report.txt", "CCCC");
        var previous = AgentFile("docs/report.txt", "BBBB", "AAAA");
        var plan = SharedFolderPlanner.Plan(
            [central],
            [previous],
            [Entry("docs/report.txt", "DDDD")],
            "WORKSTATION",
            Now);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(SharedFolderAction.ConflictPushToAgent, operation.Action);
        Assert.NotNull(operation.ConflictPath);
        Assert.Contains("conflict from WORKSTATION", operation.ConflictPath);
        Assert.StartsWith(@"docs\report ", operation.ConflictPath);
    }

    [Fact]
    public void LocalDelete_WithUnchangedCentral_MarksTombstone()
    {
        var central = CentralFile("a.txt", "AAAA");
        var previous = AgentFile("a.txt", "AAAA", "AAAA");
        var plan = SharedFolderPlanner.Plan([central], [previous], [], "WORKSTATION", Now);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(SharedFolderAction.MarkDeleted, operation.Action);
    }

    [Fact]
    public void LocalDelete_WithChangedCentral_RestoresCentralVersion()
    {
        var central = CentralFile("a.txt", "CCCC");
        var previous = AgentFile("a.txt", "AAAA", "AAAA");
        var plan = SharedFolderPlanner.Plan([central], [previous], [], "WORKSTATION", Now);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(SharedFolderAction.PushToAgent, operation.Action);
    }

    [Fact]
    public void DeletedCentral_WithUnchangedLocal_DeletesLocalFile()
    {
        var central = CentralFile("a.txt", "AAAA", deleted: true);
        var previous = AgentFile("a.txt", "AAAA", "AAAA");
        var plan = SharedFolderPlanner.Plan([central], [previous], [Entry("a.txt", "AAAA")], "WORKSTATION", Now);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(SharedFolderAction.DeleteOnAgent, operation.Action);
    }

    [Fact]
    public void DeletedCentral_WithChangedLocal_PreservesConflictCopy()
    {
        var central = CentralFile("a.txt", "AAAA", deleted: true);
        var previous = AgentFile("a.txt", "BBBB", "AAAA");
        var plan = SharedFolderPlanner.Plan([central], [previous], [Entry("a.txt", "CCCC")], "WORKSTATION", Now);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(SharedFolderAction.PreserveConflictAndDelete, operation.Action);
        Assert.Contains("conflict from WORKSTATION", operation.ConflictPath);
    }

    [Fact]
    public void DeletedCentral_WithUnobservedLocalFile_PreservesConflictCopy()
    {
        var central = CentralFile("a.txt", "AAAA", deleted: true);
        var plan = SharedFolderPlanner.Plan([central], [], [Entry("a.txt", "CCCC")], "WORKSTATION", Now);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(SharedFolderAction.PreserveConflictAndDelete, operation.Action);
    }

    [Fact]
    public void UnobservedCentralFile_IsPushed()
    {
        var central = CentralFile("new.txt", "AAAA");
        var plan = SharedFolderPlanner.Plan([central], [], [], "WORKSTATION", Now);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(SharedFolderAction.PushToAgent, operation.Action);
    }

    [Fact]
    public void MissingCentralRow_RemovesStaleAgentRow()
    {
        var previous = AgentFile("stale.txt", "AAAA", "AAAA");
        var plan = SharedFolderPlanner.Plan([], [previous], [], "WORKSTATION", Now);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(SharedFolderAction.RemoveAgentRow, operation.Action);
    }

    [Fact]
    public void SkippedPath_IsNotTreatedAsDeletion()
    {
        var central = CentralFile("locked.txt", "AAAA");
        var previous = AgentFile("locked.txt", "AAAA", "AAAA");
        var plan = SharedFolderPlanner.Plan(
            [central],
            [previous],
            [],
            "WORKSTATION",
            Now,
            ["locked.txt"]);

        Assert.Empty(plan.Operations);
    }

    [Fact]
    public void ConflictPath_IsUniqueAndSanitized()
    {
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"docs\report (conflict from PC 20261116-103000).txt"
        };

        var conflict = SharedFolderPlanner.BuildConflictPath(
            @"docs\report.txt",
            "PC<>|",
            Now,
            occupied);

        Assert.Equal(@"docs\report (conflict from PC 20261116-103000)-2.txt", conflict);
    }

    private static SharedFolderManifestEntryDto Entry(string path, string sha) => new()
    {
        Path = path.Replace('/', Path.DirectorySeparatorChar),
        Sha256 = sha,
        SizeBytes = 10,
        LastWriteUtc = Now
    };

    private static CentralSharedFile CentralFile(string path, string sha, bool deleted = false) =>
        new(path, sha, 10, Now.UtcDateTime, 1, deleted, "HUB");

    private static AgentSharedFile AgentFile(string path, string sha, string centralSha) =>
        new(path, sha, centralSha, 10, Now.UtcDateTime);
}

public sealed class SharedFolderScannerTests
{
    [Fact]
    public async Task ScanAsync_ReturnsRelativeHashesAndSkipsStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemoteDeskSharedFolder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        Directory.CreateDirectory(Path.Combine(root, ".rdh-sync"));
        await File.WriteAllTextAsync(Path.Combine(root, "readme.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(root, "docs", "report.txt"), "report");
        await File.WriteAllTextAsync(Path.Combine(root, ".rdh-sync", "hidden.part"), "staging");

        try
        {
            var scanner = new SharedFolderScanner();
            var manifest = await scanner.ScanAsync(root, 100, 1024 * 1024);

            Assert.True(string.IsNullOrWhiteSpace(manifest.Error), manifest.Error);
            Assert.False(manifest.IsTruncated);
            Assert.Equal(2, manifest.Entries.Count);
            Assert.DoesNotContain(manifest.Entries, entry => entry.Path.Contains(".rdh-sync", StringComparison.OrdinalIgnoreCase));

            var readme = manifest.Entries.Single(entry => entry.Path == "readme.txt");
            var expected = Convert.ToHexString(SHA256.HashData("hello"u8));
            Assert.Equal(expected, readme.Sha256);
            Assert.Equal(5, readme.SizeBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_OversizedFileIsReportedAsSkipped()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemoteDeskSharedFolder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "large.bin"), "0123456789");
        await File.WriteAllTextAsync(Path.Combine(root, "small.txt"), "ok");

        try
        {
            var scanner = new SharedFolderScanner();
            var manifest = await scanner.ScanAsync(root, 100, 4);

            Assert.Single(manifest.Entries);
            Assert.Contains("large.bin", manifest.SkippedPaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_TruncatesWhenFileLimitReached()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemoteDeskSharedFolder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "one.txt"), "1");
        await File.WriteAllTextAsync(Path.Combine(root, "two.txt"), "2");

        try
        {
            var scanner = new SharedFolderScanner();
            var manifest = await scanner.ScanAsync(root, 1, 1024);

            Assert.True(manifest.IsTruncated);
            Assert.Single(manifest.Entries);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_InvalidRootReturnsError()
    {
        var scanner = new SharedFolderScanner();
        var manifest = await scanner.ScanAsync("relative-root", 100, 1024);

        Assert.False(string.IsNullOrWhiteSpace(manifest.Error));
        Assert.True(manifest.IsTruncated);
    }
}

public sealed class SharedFolderPathGuardTests
{
    private const string Root = @"C:\RemoteDeskTestRoot";

    [Theory]
    [InlineData(@"docs\report.txt", true)]
    [InlineData("docs/report.txt", true)]
    [InlineData(@"a.txt", true)]
    [InlineData(@"..\escape.txt", false)]
    [InlineData(@"docs\..\..\escape.txt", false)]
    [InlineData(@"\Windows\win.ini", false)]
    [InlineData(@"C:\Windows\win.ini", false)]
    [InlineData(@"file.txt:secret", false)]
    [InlineData(@".rdh-sync\staging.part", false)]
    [InlineData(@"sub\CON.txt", false)]
    [InlineData(@"trailing.", false)]
    [InlineData(@"trailing ", false)]
    [InlineData("", false)]
    public void TryResolveRelativeWithinRoot_ValidatesRelativePaths(string relative, bool expected)
    {
        var result = PathGuard.TryResolveRelativeWithinRoot(Root, relative, out var full, out var normalized, out _);

        Assert.Equal(expected, result);
        if (expected)
        {
            Assert.StartsWith(Root, full, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(normalized));
        }
    }
}
