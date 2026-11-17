using RemoteDeskHub.Shared.Security;

namespace RemoteDeskHub.UnitTests;

public sealed class PathGuardTests
{
    private const string Root = @"C:\RemoteDeskTestRoot";

    [Theory]
    [InlineData(@"C:\RemoteDeskTestRoot\file.txt", true)]
    [InlineData(@"C:\RemoteDeskTestRoot\sub\deep\file.txt", true)]
    [InlineData(@"C:\", true)]
    [InlineData("relative\\path.txt", false)]
    [InlineData(@"\\server\share\file.txt", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(@"\Windows\System32", false)]
    public void IsValidAbsolutePath_RequiresLocalDrivePath(string path, bool expected)
    {
        Assert.Equal(expected, PathGuard.IsValidAbsolutePath(path, out _));
    }

    [Theory]
    [InlineData(@"C:\System Volume Information\x.dat")]
    [InlineData(@"C:\$Recycle.Bin\deleted.txt")]
    [InlineData(@"C:\Recovery\file.txt")]
    [InlineData(@"C:\Boot\bcd")]
    [InlineData(@"C:\EFI\Microsoft\bootmgfw.efi")]
    public void IsValidAbsolutePath_BlocksProtectedLocations(string path)
    {
        Assert.False(PathGuard.IsValidAbsolutePath(path, out var error));
        Assert.Contains("protected", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsValidAbsolutePath_NormalisesParentSegmentsBeforeBlockedCheck()
    {
        // ".." is resolved first, so a path that resolves into a blocked location is rejected.
        Assert.False(PathGuard.IsValidAbsolutePath(
            @"C:\RemoteDeskTestRoot\..\System Volume Information\index.dat", out var error));
        Assert.Contains("protected", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsValidAbsolutePath_RejectsAlternateDataStreams()
    {
        Assert.False(PathGuard.IsValidAbsolutePath(@"C:\RemoteDeskTestRoot\file.txt:secret", out var error));
        Assert.Contains("Alternate data streams", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryNormalizeWithinRoots_RejectsSymlinkedFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemoteDeskPathGuard", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(Path.GetTempPath(), $"RemoteDeskTarget-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(root);
        File.WriteAllText(target, "test");
        var link = Path.Combine(root, "link.txt");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Creating symlinks requires developer mode or elevation on some Windows builds.
            Directory.Delete(root, recursive: true);
            File.Delete(target);
            return;
        }

        try
        {
            var ok = PathGuard.TryNormalizeWithinRoots(link, [root], out _, out var error);
            Assert.False(ok);
            Assert.Contains("Reparse", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(target);
        }
    }

    [Fact]
    public void TryNormalizeWithinRoots_AcceptsPathInsideRoot()
    {
        var ok = PathGuard.TryNormalizeWithinRoots(
            Root + @"\docs\report.txt", [Root], out var normalized, out var error);

        Assert.True(ok, error);
        Assert.StartsWith(Root, normalized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryNormalizeWithinRoots_RejectsSiblingPrefix()
    {
        // "C:\RemoteDeskTestRootEvil" must not be treated as inside "C:\RemoteDeskTestRoot".
        var ok = PathGuard.TryNormalizeWithinRoots(
            @"C:\RemoteDeskTestRootEvil\stolen.txt", [Root], out _, out var error);

        Assert.False(ok);
        Assert.Contains("outside", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryNormalizeWithinRoots_RejectsPathOutsideAllRoots()
    {
        var ok = PathGuard.TryNormalizeWithinRoots(
            @"C:\OtherPlace\file.txt", [Root, @"C:\SecondRoot"], out _, out var error);

        Assert.False(ok);
        Assert.Contains("outside", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryNormalizeWithinRoots_RejectsWhenNoRootsConfigured()
    {
        var ok = PathGuard.TryNormalizeWithinRoots(Root + @"\a.txt", [], out _, out var error);

        Assert.False(ok);
        Assert.Contains("No permitted file roots", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryNormalizeWithinRoots_RejectsTraversalAboveRoot()
    {
        var ok = PathGuard.TryNormalizeWithinRoots(
            Root + @"\..\Windows\win.ini", [Root], out _, out var error);

        Assert.False(ok);
        Assert.Contains("outside", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"C:\RemoteDeskTestRoot\a.txt", Root, true)]
    [InlineData(@"C:\RemoteDeskTestRoot", Root, true)]
    [InlineData(@"C:\RemoteDeskTestRootEvil\a.txt", Root, false)]
    [InlineData(@"D:\RemoteDeskTestRoot\a.txt", Root, false)]
    public void IsWithinRoot_ComparesNormalisedRoots(string path, string root, bool expected)
    {
        Assert.Equal(expected, PathGuard.IsWithinRoot(path, root));
    }

    [Fact]
    public void IsWithinRoot_HandlesInvalidInput()
    {
        Assert.False(PathGuard.IsWithinRoot("::invalid::", Root));
    }
}
