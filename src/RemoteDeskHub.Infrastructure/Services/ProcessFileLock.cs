using System.IO;

namespace RemoteDeskHub.Infrastructure.Services;

/// <summary>
/// A cross-process exclusive lock backed by a file. Unlike a named mutex, a file handle is not
/// thread-affine, so it can be held across async awaits, and Windows releases it automatically if
/// the owning process exits.
/// </summary>
internal static class ProcessFileLock
{
    public static async Task<FileStream> AcquireAsync(
        string directory,
        string fileName,
        CancellationToken cancellationToken,
        int timeoutMilliseconds = 5000)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var deadline = Environment.TickCount64 + Math.Max(100, timeoutMilliseconds);

        while (true)
        {
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }
}
