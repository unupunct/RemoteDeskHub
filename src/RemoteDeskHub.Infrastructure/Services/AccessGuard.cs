using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;

namespace RemoteDeskHub.Infrastructure.Services;

internal static class AccessGuard
{
    public static void Require(ISessionContext session, RemoteDeskHub.Core.Security.Permission permission, string? actor = null)
    {
        if (string.Equals(actor, RemoteDeskHub.Shared.HubConstants.AuditActorSystem, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var user = session.CurrentUser;
        if (user is null)
        {
            throw new UnauthorizedAccessException("No user is signed in to RemoteDesk Hub.");
        }

        if (!RemoteDeskHub.Core.Security.Permissions.HasPermission(user.Role, permission))
        {
            throw new UnauthorizedAccessException($"The role '{user.Role}' is not permitted to perform this action.");
        }
    }
}

internal static class DbTime
{
    public static DateTime UtcNow => DateTime.UtcNow;

    public static DateTime? ToUtc(DateTime? value) => value?.ToUniversalTime();
}
