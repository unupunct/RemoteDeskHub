using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Core.Security;

public enum Permission
{
    ViewDashboard = 0,
    ViewComputers = 1,
    ViewMonitoring = 2,
    ViewReports = 3,
    ViewAudit = 4,
    RunCommands = 5,
    ManageProcesses = 6,
    ManageServices = 7,
    TransferFiles = 8,
    DeleteRemoteFiles = 9,
    LaunchRemoteAssistance = 10,
    ManageGroups = 11,
    ManageAutomation = 12,
    ManageAlerts = 13,
    ManageComputers = 14,
    EnrollAgents = 15,
    ManageSettings = 16,
    ManageUsers = 17,
    BackupDatabase = 18,
    ExportData = 19,
    ApproveCommands = 20
}

public static class Permissions
{
    private static readonly HashSet<Permission> ViewerPermissions =
    [
        Permission.ViewDashboard, Permission.ViewComputers, Permission.ViewMonitoring,
        Permission.ViewReports, Permission.ExportData
    ];

    private static readonly HashSet<Permission> OperatorPermissions =
    [
        Permission.ViewDashboard, Permission.ViewComputers, Permission.ViewMonitoring,
        Permission.ViewReports, Permission.ExportData, Permission.RunCommands,
        Permission.ManageProcesses, Permission.ManageServices, Permission.TransferFiles,
        Permission.LaunchRemoteAssistance, Permission.ManageAutomation, Permission.ManageAlerts,
        Permission.ViewAudit
    ];

    public static bool HasPermission(UserRole role, Permission permission)
    {
        if (role == UserRole.Administrator)
        {
            return true;
        }

        if (role == UserRole.Operator)
        {
            return OperatorPermissions.Contains(permission);
        }

        return ViewerPermissions.Contains(permission);
    }

    public static string Describe(UserRole role) => role switch
    {
        UserRole.Administrator => "Full control, including security, users and settings.",
        UserRole.Operator => "Day-to-day administration of computers, commands and monitoring.",
        _ => "Read-only access to dashboards, inventory and reports."
    };
}

public sealed record SessionUser(Guid UserId, string UserName, UserRole Role, DateTime SignedInUtc)
{
    public bool Can(Permission permission) => Permissions.HasPermission(Role, permission);
}

public interface ISessionContext
{
    SessionUser? CurrentUser { get; }
    bool IsAuthenticated { get; }
    void SignIn(SessionUser user);
    void SignOut();
}

public sealed class SessionContext : ISessionContext
{
    public SessionUser? CurrentUser { get; private set; }
    public bool IsAuthenticated => CurrentUser is not null;
    public void SignIn(SessionUser user) => CurrentUser = user;
    public void SignOut() => CurrentUser = null;
}
