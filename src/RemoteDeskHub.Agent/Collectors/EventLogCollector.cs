using System.Diagnostics.Eventing.Reader;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Agent.Collectors;

/// <summary>Reads the newest entries from a Windows event log.</summary>
public sealed class EventLogCollector
{
    private static readonly HashSet<string> AllowedLogs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Application", "System", "Security", "Setup",
        "Microsoft-Windows-WindowsUpdateClient/Operational",
        "Microsoft-Windows-TaskScheduler/Operational",
        "Microsoft-Windows-Kernel-Power/Thermal-Operational"
    };

    public IReadOnlyList<RemoteEventLogDto> Collect(string logName, int maxEntries)
    {
        if (!AllowedLogs.Contains(logName))
        {
            throw new ArgumentException("That event log is not in the permitted list.", nameof(logName));
        }

        var limit = Math.Clamp(maxEntries, 1, 500);
        var results = new List<RemoteEventLogDto>();
        try
        {
            var query = new EventLogQuery(logName, PathType.LogName) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            EventRecord? record;
            while (results.Count < limit && (record = reader.ReadEvent()) is not null)
            {
                using (record)
                {
                    results.Add(Map(record, logName));
                }
            }
        }
        catch (EventLogNotFoundException)
        {
            throw new InvalidOperationException($"The event log '{logName}' does not exist on this computer.");
        }

        return results;
    }

    private static RemoteEventLogDto Map(EventRecord record, string logName)
    {
        string message;
        try
        {
            message = record.FormatDescription() ?? "(no description)";
        }
        catch
        {
            message = "(description unavailable)";
        }

        return new RemoteEventLogDto
        {
            LogName = logName,
            TimeCreatedUtc = record.TimeCreated?.ToUniversalTime() ?? DateTimeOffset.MinValue,
            Level = record.Level switch
            {
                1 => "Critical",
                2 => "Error",
                3 => "Warning",
                4 => "Information",
                5 => "Verbose",
                _ => record.LevelDisplayName ?? "Unknown"
            },
            Provider = record.ProviderName,
            EventId = record.Id,
            Message = message.Length > 8000 ? message[..8000] : message
        };
    }
}
