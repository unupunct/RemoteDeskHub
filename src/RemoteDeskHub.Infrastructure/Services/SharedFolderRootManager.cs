using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace RemoteDeskHub.Infrastructure.Services;

/// <summary>
/// Creates and hardens the hub-local shared folder. The shared folder is a dedicated root, not part
/// of the general remote file-transfer roots. Local users need write access because files may be
/// placed there directly in File Explorer as well as through the console.
/// </summary>
public sealed class SharedFolderRootManager(ILogger<SharedFolderRootManager> logger)
{
    public bool TryEnsureRoot(string root, out string normalizedRoot, out string? error)
    {
        normalizedRoot = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(root))
        {
            error = "A shared-folder path is required.";
            return false;
        }

        if (!RemoteDeskHub.Shared.Security.PathGuard.IsValidAbsolutePath(root, out error))
        {
            return false;
        }

        try
        {
            normalizedRoot = Path.GetFullPath(root);
            var existed = Directory.Exists(normalizedRoot);
            Directory.CreateDirectory(normalizedRoot);
            if (!existed)
            {
                TryApplySharedAcl(normalizedRoot);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.LogWarning(ex, "Could not create the hub shared folder {Root}.", root);
            return false;
        }
    }

    private void TryApplySharedAcl(string root)
    {
        try
        {
            var directory = new DirectoryInfo(root);
            var security = directory.GetAccessControl();
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            security.AddAccessRule(new FileSystemAccessRule(
                users,
                FileSystemRights.Modify | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            directory.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            // Creating the folder is still useful even when ACL hardening is denied. The agent
            // service and administrators can still use it; the UI surfaces the access error later.
            logger.LogWarning(ex, "The shared folder was created but its local-user ACL could not be applied.");
        }
    }
}
