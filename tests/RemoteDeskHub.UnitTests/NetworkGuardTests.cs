using System.Net;
using RemoteDeskHub.Shared.Security;

namespace RemoteDeskHub.UnitTests;

public sealed class NetworkAddressGuardTests
{
    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("10.255.255.254", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.1.50", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("127.0.0.1", true)]
    public void TryParseLanAddress_AcceptsPrivateAndLoopback(string value, bool expected)
    {
        Assert.Equal(expected, NetworkAddressGuard.TryParseLanAddress(value, out var address, out _));
        if (expected)
        {
            Assert.Equal(value, address.ToString());
        }
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("192.169.1.1")]
    [InlineData("11.0.0.1")]
    [InlineData("2001:db8::1")]
    [InlineData("not-an-ip")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseLanAddress_RejectsPublicOrInvalid(string? value)
    {
        Assert.False(NetworkAddressGuard.TryParseLanAddress(value, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("127.0.0.2", true)]
    [InlineData("192.168.1.0", false)]
    [InlineData("192.168.1.255", false)]
    [InlineData("8.8.8.8", false)]
    public void IsWithinScanScope_ExcludesNetworkAndBroadcast(string value, bool expected)
    {
        Assert.Equal(expected, NetworkAddressGuard.IsWithinScanScope(IPAddress.Parse(value)));
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("192.168.0.5", true)]
    [InlineData("1.1.1.1", false)]
    public void IsPrivateOrLoopback_ClassifiesRanges(string value, bool expected)
    {
        Assert.Equal(expected, NetworkAddressGuard.IsPrivateOrLoopback(IPAddress.Parse(value)));
    }
}

public sealed class ProtectedResourceTests
{
    [Theory]
    [InlineData("lsass")]
    [InlineData("lsass.exe")]
    [InlineData("LSASS.EXE")]
    [InlineData("csrss")]
    [InlineData("winlogon")]
    [InlineData("wininit")]
    [InlineData("services")]
    [InlineData("smss")]
    [InlineData("svchost")]
    [InlineData("System")]
    public void ProtectedProcesses_BlocksWindowsCore(string name)
    {
        Assert.True(RemoteDeskHub.Shared.ProtectedProcesses.IsProtected(name));
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("notepad.exe")]
    [InlineData("chrome")]
    [InlineData("")]
    public void ProtectedProcesses_AllowsOrdinaryProcesses(string name)
    {
        Assert.False(RemoteDeskHub.Shared.ProtectedProcesses.IsProtected(name));
    }

    [Theory]
    [InlineData("RpcSs")]
    [InlineData("rpcss")]
    [InlineData("DcomLaunch")]
    [InlineData("Winmgmt")]
    [InlineData("EventLog")]
    [InlineData("MpsSvc")]
    public void CriticalServices_BlocksCoreServices(string name)
    {
        Assert.True(RemoteDeskHub.Shared.CriticalServices.IsCritical(name));
    }

    [Theory]
    [InlineData("Spooler")]
    [InlineData("RemoteDeskAgent")]
    [InlineData("")]
    public void CriticalServices_AllowsOrdinaryServices(string name)
    {
        Assert.False(RemoteDeskHub.Shared.CriticalServices.IsCritical(name));
    }
}
