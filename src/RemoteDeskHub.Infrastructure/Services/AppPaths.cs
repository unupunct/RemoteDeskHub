using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class AppPaths : IAppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        var root = rootOverride;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetEnvironmentVariable("REMOTEDESKHUB_DATA");
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                HubConstants.DataFolderName);
        }

        RootDirectory = Path.GetFullPath(root);
        DatabasePath = Path.Combine(RootDirectory, HubConstants.DatabaseFileName);
        CertificatesDirectory = Path.Combine(RootDirectory, HubConstants.CertificateFolderName);
        LogsDirectory = Path.Combine(RootDirectory, HubConstants.LogFolderName);
        BackupsDirectory = Path.Combine(RootDirectory, HubConstants.BackupFolderName);
        TransfersDirectory = Path.Combine(RootDirectory, "transfers");
        ExportsDirectory = Path.Combine(RootDirectory, "exports");
        SharedFolderDirectory = Path.Combine(RootDirectory, "shared-folder");
        SharedFolderBlobsDirectory = Path.Combine(SharedFolderDirectory, "blobs");
        SharedFolderStagingDirectory = Path.Combine(SharedFolderDirectory, "staging");

        foreach (var dir in new[]
                 {
                     RootDirectory, CertificatesDirectory, LogsDirectory, BackupsDirectory, TransfersDirectory,
                     ExportsDirectory, SharedFolderDirectory, SharedFolderBlobsDirectory, SharedFolderStagingDirectory
                 })
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string RootDirectory { get; }
    public string DatabasePath { get; }
    public string CertificatesDirectory { get; }
    public string LogsDirectory { get; }
    public string BackupsDirectory { get; }
    public string TransfersDirectory { get; }
    public string ExportsDirectory { get; }
    public string SharedFolderDirectory { get; }
    public string SharedFolderBlobsDirectory { get; }
    public string SharedFolderStagingDirectory { get; }

    public string EnsureDirectory(string name)
    {
        var dir = Path.Combine(RootDirectory, name);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
