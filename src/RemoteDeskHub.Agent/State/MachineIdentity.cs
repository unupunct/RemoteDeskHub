using Microsoft.Win32;

namespace RemoteDeskHub.Agent.State;

/// <summary>
/// Resolves the stable identity of the physical Windows installation. The IP address is never used
/// as an identity because DHCP, VPN and Wi-Fi changes would otherwise create duplicate computers.
/// </summary>
internal static class MachineIdentity
{
    private const string MachineGuidKey = @"SOFTWARE\Microsoft\Cryptography";
    private const string MachineGuidValue = "MachineGuid";

    /// <summary>
    /// Returns the registry MachineGuid when available. If the agent cannot read the registry, it
    /// falls back to the persisted installation ID so the identity is still stable across restarts.
    /// </summary>
    public static string GetStableMachineId(Guid installationId)
    {
        var registryValue = TryReadRegistryMachineGuid();
        return string.IsNullOrWhiteSpace(registryValue)
            ? $"installation-{installationId:N}"
            : Normalize(registryValue);
    }

    private static string? TryReadRegistryMachineGuid()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(MachineGuidKey, writable: false);
            return key?.GetValue(MachineGuidValue) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static string Normalize(string value)
    {
        var candidate = value.Trim().ToLowerInvariant();
        return candidate.Length <= 128
            ? candidate
            : candidate[..128];
    }
}
