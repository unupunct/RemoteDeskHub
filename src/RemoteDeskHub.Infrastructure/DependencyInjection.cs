using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Infrastructure.Services;

namespace RemoteDeskHub.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the hub's domain services, EF Core SQLite context factory and shared state.
    /// Networking services (certificate authority, agent transport and HTTP server) are registered
    /// by the composition root because they depend on ASP.NET Core.
    /// </summary>
    public static IServiceCollection AddRemoteDeskHubCore(this IServiceCollection services, string? dataRoot = null)
    {
        var paths = new AppPaths(dataRoot);
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISessionContext, SessionContext>();
        services.AddSingleton<IAppEvents, AppEvents>();

        services.AddDbContextFactory<RemoteDeskDbContext>(options =>
            options.UseSqlite($"Data Source={paths.DatabasePath};Cache=Shared;Pooling=True;Default Timeout=30"));

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IAuditService, AuditService>();
        services.AddSingleton<ReplayGuard>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        services.AddSingleton<IInventoryService, InventoryService>();
        services.AddSingleton<IMonitoringService, MonitoringService>();
        services.AddSingleton<ICommandService, CommandService>();
        services.AddSingleton<IComputerService, ComputerService>();
        services.AddSingleton<IProcessService, ProcessService>();
        services.AddSingleton<IServiceManagerService, ServiceManagerService>();
        services.AddSingleton<IFileTransferService, FileTransferService>();
        services.AddSingleton<SharedFolderRootManager>();
        services.AddSingleton<ISharedFolderService, SharedFolderService>();
        services.AddSingleton<IDiscoveryService, DiscoveryService>();
        services.AddSingleton<IEnrollmentService, EnrollmentService>();
        services.AddSingleton<IAgentLifecycleService, AgentLifecycleService>();
        services.AddSingleton<IRemoteAssistanceService, RemoteAssistanceService>();
        services.AddSingleton<IAutomationService, AutomationService>();
        services.AddSingleton<IReportService, ReportingService>();
        services.AddSingleton<IBackupService, BackupService>();

        return services;
    }
}
