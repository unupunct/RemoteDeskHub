using Microsoft.Win32;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Agent.Collectors;

/// <summary>Reads installed software from the standard uninstall registry keys (read-only).</summary>
public sealed class SoftwareCollector
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public SoftwareInventoryDto Collect()
    {
        var items = new List<SoftwareItemDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ReadHive(RegistryHive.LocalMachine, RegistryView.Registry64, "x64", UninstallPath, items, seen);
        ReadHive(RegistryHive.LocalMachine, RegistryView.Registry32, "x86", UninstallPath, items, seen);
        ReadHive(RegistryHive.CurrentUser, RegistryView.Default, "x64", UninstallPath, items, seen);

        return new SoftwareInventoryDto
        {
            Items = items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            CollectedUtc = DateTimeOffset.UtcNow
        };
    }

    private static void ReadHive(RegistryHive hive, RegistryView view, string architecture, string path,
        List<SoftwareItemDto> items, HashSet<string> seen)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(path);
            if (uninstall is null)
            {
                return;
            }

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(subKeyName);
                    if (key is null)
                    {
                        continue;
                    }

                    var name = key.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    if (Convert.ToInt32(key.GetValue("SystemComponent") ?? 0, System.Globalization.CultureInfo.InvariantCulture) == 1)
                    {
                        continue;
                    }

                    var version = key.GetValue("DisplayVersion") as string;
                    var publisher = key.GetValue("Publisher") as string;
                    var identity = $"{name}|{version}|{publisher}|{architecture}";
                    if (!seen.Add(identity))
                    {
                        continue;
                    }

                    long? size = null;
                    if (key.GetValue("EstimatedSize") is int kb && kb > 0)
                    {
                        size = kb * 1024L;
                    }

                    items.Add(new SoftwareItemDto
                    {
                        Name = name.Trim(),
                        Publisher = string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim(),
                        Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
                        InstallDate = key.GetValue("InstallDate") as string,
                        InstallLocation = key.GetValue("InstallLocation") as string,
                        EstimatedSizeBytes = size,
                        Architecture = architecture,
                        RegistryKey = $"{hive}\\{path}\\{subKeyName}"
                    });
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    // A single unreadable product must not fail the whole inventory.
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // The hive or key may be unavailable.
        }
    }
}
