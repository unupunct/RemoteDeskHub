using System.Net;
using System.Net.Sockets;

namespace RemoteDeskHub.Shared.Security;

/// <summary>Restricts discovery and agent management traffic to local / private networks.</summary>
public static class NetworkAddressGuard
{
    public static bool TryParseLanAddress(string? value, out IPAddress address, out string error)
    {
        address = IPAddress.None;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !IPAddress.TryParse(value.Trim(), out var parsed))
        {
            error = "Enter a valid IPv4 address.";
            return false;
        }

        if (parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "Only IPv4 LAN addresses are supported.";
            return false;
        }

        if (!IsPrivateOrLoopback(parsed))
        {
            error = "Only private LAN addresses (10/8, 172.16/12, 192.168/16, 169.254/16) or loopback are permitted.";
            return false;
        }

        address = parsed;
        return true;
    }

    public static bool IsPrivateOrLoopback(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        return bytes[0] == 10
               || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
               || (bytes[0] == 192 && bytes[1] == 168)
               || (bytes[0] == 169 && bytes[1] == 254);
    }

    public static bool IsWithinScanScope(IPAddress address)
        => IsPrivateOrLoopback(address) && address.GetAddressBytes()[3] is > 0 and < 255;
}
