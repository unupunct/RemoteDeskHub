using System.ServiceProcess;
using Microsoft.Win32;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Agent.Collectors;

/// <summary>Enumerates Windows services and their dependencies.</summary>
public sealed class ServiceCollector
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    public IReadOnlyList<RemoteServiceDto> Collect()
    {
        var results = new List<RemoteServiceDto>();
        ServiceController[] services;
        try
        {
            services = ServiceController.GetServices();
        }
        catch
        {
            return results;
        }

        foreach (var service in services)
        {
            using (service)
            {
                try
                {
                    results.Add(Map(service));
                }
                catch
                {
                    // Some services cannot be queried; skip them rather than failing the list.
                }
            }
        }

        return results.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public RemoteServiceDto? GetDependencies(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            return Map(service);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsValidServiceName(string name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= 256 && !name.Contains('\\') && !name.Contains('/');

    public static RemoteServiceDto Map(ServiceController service)
    {
        var dependencies = new List<string>();
        var dependents = new List<string>();
        try
        {
            dependencies.AddRange(service.ServicesDependedOn.Select(d => d.ServiceName));
        }
        catch
        {
            // Ignore dependency enumeration failures.
        }

        try
        {
            dependents.AddRange(service.DependentServices.Select(d => d.ServiceName));
        }
        catch
        {
            // Ignore.
        }

        var (startupType, account, imagePath) = ReadRegistry(service.ServiceName);

        return new RemoteServiceDto
        {
            Name = service.ServiceName,
            DisplayName = string.IsNullOrWhiteSpace(service.DisplayName) ? service.ServiceName : service.DisplayName,
            Description = null,
            Status = service.Status.ToString(),
            StartupType = startupType,
            Account = account,
            ExecutablePath = imagePath,
            CanStop = service.CanStop,
            CanPauseAndContinue = service.CanPauseAndContinue,
            IsCritical = CriticalServices.IsCritical(service.ServiceName),
            Dependencies = dependencies,
            DependentServices = dependents
        };
    }

    private static (string StartupType, string? Account, string? ImagePath) ReadRegistry(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesKey}\{name}");
            if (key is null)
            {
                return ("Unknown", null, null);
            }

            var start = key.GetValue("Start");
            var startupType = start switch
            {
                0 or 1 => "Boot/System",
                2 => "Automatic",
                3 => "Manual",
                4 => "Disabled",
                _ => "Unknown"
            };

            if (start is 2 && key.GetValue("DelayedAutostart") is int delayed && delayed == 1)
            {
                startupType = "Automatic (Delayed)";
            }

            return (startupType, key.GetValue("ObjectName") as string, key.GetValue("ImagePath") as string);
        }
        catch
        {
            return ("Unknown", null, null);
        }
    }
}
