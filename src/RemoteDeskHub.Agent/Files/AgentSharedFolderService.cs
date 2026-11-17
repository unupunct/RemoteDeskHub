using RemoteDeskHub.Shared.Protocol;
using RemoteDeskHub.Shared.Security;

namespace RemoteDeskHub.Agent.Files;

/// <summary>
/// Owns the agent's configured shared folder. The manifest is produced by the shared scanner so the
/// hub and the agent apply the same path, reparse-point and size rules.
/// </summary>
public sealed class AgentSharedFolderService(AgentRuntimeState state, ILogger<AgentSharedFolderService> logger)
{
    private readonly SharedFolderScanner _scanner = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    public bool TryEnsureRoot(out string root, out string? error)
    {
        root = string.Empty;
        error = null;
        var config = state.Config;
        if (!config.SharedFolderEnabled)
        {
            return false;
        }

        if (!PathGuard.IsValidAbsolutePath(config.SharedFolderPath, out error))
        {
            return false;
        }

        try
        {
            root = Path.GetFullPath(config.SharedFolderPath);
            Directory.CreateDirectory(root);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.LogWarning(ex, "Could not create the shared folder {Root}.", config.SharedFolderPath);
            return false;
        }
    }

    public async Task<SharedFolderManifestDto> BuildManifestAsync(CancellationToken cancellationToken = default)
    {
        var config = state.Config;
        if (!config.SharedFolderEnabled)
        {
            return new SharedFolderManifestDto { Enabled = false, Error = "Shared-folder sync is disabled in hub settings." };
        }

        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            if (!TryEnsureRoot(out var root, out var ensureError))
            {
                return new SharedFolderManifestDto
                {
                    Enabled = true,
                    Root = config.SharedFolderPath,
                    Error = ensureError,
                    IsTruncated = true
                };
            }

            var manifest = await _scanner.ScanAsync(
                root,
                Math.Clamp(config.SharedFolderMaxFiles, 100, 200_000),
                Math.Clamp(config.SharedFolderMaxFileBytes, 1_048_576L, 4_294_967_296L),
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(manifest.Error))
            {
                logger.LogWarning("The shared-folder scan failed: {Error}", manifest.Error);
            }

            return manifest;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The shared-folder manifest scan failed.");
            return new SharedFolderManifestDto
            {
                Enabled = true,
                Root = config.SharedFolderPath,
                Error = ex.Message,
                IsTruncated = true
            };
        }
        finally
        {
            _scanGate.Release();
        }
    }
}
