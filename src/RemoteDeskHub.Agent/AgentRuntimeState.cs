using System.Security.Cryptography.X509Certificates;
using RemoteDeskHub.Agent.State;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Agent;

/// <summary>Mutable runtime state shared by the agent HTTP API and the heartbeat worker.</summary>
public sealed class AgentRuntimeState
{
    private volatile AgentRuntimeConfigDto _config = new()
    {
        AllowedFileRoots = [@"C:\Users", @"C:\ProgramData", @"C:\zzz"]
    };

    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public AgentIdentity? Identity { get; set; }
    public X509Certificate2? Certificate { get; set; }
    public AgentRuntimeConfigDto Config => _config;
    public IReadOnlyList<string> AllowedRoots => _config.AllowedFileRoots;
    public bool CommandsEnabled => _config.AllowRemoteCommands;
    public int MaxTransferChunkBytes => _config.MaxTransferChunkBytes <= 0 ? HubConstants.MaxTransferChunkBytes : _config.MaxTransferChunkBytes;
    public int MaxCommandTimeoutSeconds => _config.MaxCommandTimeoutSeconds <= 0 ? HubConstants.MaxCommandTimeoutSeconds : _config.MaxCommandTimeoutSeconds;
    public bool Revoked { get; set; }
    public bool Approved { get; set; }
    public bool ApiRunning { get; set; }

    public void Apply(AgentRuntimeConfigDto config)
    {
        _config = config;
        Revoked = config.Revoked;
    }

    public long UptimeSeconds => (long)(DateTime.UtcNow - StartedUtc).TotalSeconds;
}
