using RemoteDeskHub.Infrastructure.Services;

namespace RemoteDeskHub.UnitTests;

public sealed class CommandSafetyTests
{
    [Theory]
    [InlineData("format C: /q")]
    [InlineData("diskpart /s wipe.txt")]
    [InlineData("bcdedit /set {default} safeboot minimal")]
    [InlineData("wevtutil cl Security")]
    [InlineData("sc delete RemoteDeskAgent")]
    [InlineData("reg delete HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run /f")]
    [InlineData("net localgroup administrators attacker /add")]
    public void CriticalCommands_AreDetected(string command)
    {
        Assert.True(CommandSafety.IsCritical(command));
    }

    [Theory]
    [InlineData("Get-Process")]
    [InlineData("hostname")]
    [InlineData("Get-Service Spooler")]
    [InlineData("ipconfig /all")]
    public void OrdinaryCommands_AreNotCritical(string command)
    {
        Assert.False(CommandSafety.IsCritical(command));
    }

    [Fact]
    public void Allowlist_RequiresExactWhitespaceNormalisedMatch()
    {
        const string allowlist = "format C: /q\n# comment\ndiskpart /s wipe.txt";

        Assert.True(CommandSafety.IsAllowlisted("  format   C:   /q  ", allowlist));
        Assert.True(CommandSafety.IsAllowlisted("DISKPART /s wipe.txt", allowlist));
        Assert.False(CommandSafety.IsAllowlisted("format C:", allowlist));
        Assert.False(CommandSafety.IsAllowlisted("diskpart", allowlist));
        Assert.False(CommandSafety.IsAllowlisted("format C: /q", "# format C: /q"));
    }

    [Fact]
    public void DestructiveCommands_AreDetectedSeparatelyFromCriticalBlocklist()
    {
        Assert.True(CommandSafety.IsPotentiallyDestructive("Stop-Service Spooler"));
        Assert.True(CommandSafety.IsPotentiallyDestructive("Remove-Item C:\\Temp\\file.txt"));
        Assert.False(CommandSafety.IsCritical("Stop-Service Spooler"));
        Assert.False(CommandSafety.IsPotentiallyDestructive("Get-Process"));
    }
}
