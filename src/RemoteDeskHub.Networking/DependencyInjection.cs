using Microsoft.Extensions.DependencyInjection;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Networking.Security;

namespace RemoteDeskHub.Networking;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the hub certificate authority, HTTPS server and agent transport.</summary>
    public static IServiceCollection AddRemoteDeskHubNetworking(this IServiceCollection services)
    {
        services.AddSingleton<ICertificateAuthority, CertificateAuthority>();
        services.AddSingleton<AgentClient>();
        services.AddSingleton<IAgentTransport>(sp => sp.GetRequiredService<AgentClient>());
        services.AddSingleton<IAgentConnectionInfoProvider>(sp => sp.GetRequiredService<AgentClient>());
        services.AddSingleton<IHubServer, HubServer>();
        return services;
    }
}
