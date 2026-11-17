using System.Diagnostics;

namespace RemoteDeskHub.Agent;

/// <summary>Writes service lifecycle and failure events to the Windows Application event log.</summary>
public static class AgentEventLog
{
    private const string Source = "RemoteDesk Agent";
    private const string LogName = "Application";

    public static void EnsureSource()
    {
        try
        {
            if (!EventLog.SourceExists(Source))
            {
                EventLog.CreateEventSource(Source, LogName);
            }
        }
        catch
        {
            // Creating the source requires administrator rights; the installer performs this step.
        }
    }

    public static void Write(string message, EventLogEntryType type = EventLogEntryType.Information)
    {
        try
        {
            EventLog.WriteEntry(Source, message, type);
        }
        catch
        {
            // Never let event-log failures break the agent.
        }
    }
}
