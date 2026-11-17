using System.Diagnostics;
using System.Security.Principal;

namespace RemoteDeskHub.Agent;

/// <summary>Agent configuration resolved from command line, pairing package and local state.</summary>
public sealed class AgentOptions
{
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RemoteDesk Agent");

    public string? PairingFilePath { get; set; }
    public string? HubUrlOverride { get; set; }
    public int? PortOverride { get; set; }
    public bool RunAsConsole { get; set; }

    public string StatePath => Path.Combine(DataDirectory, "agent-state.json");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string CertificatesDirectory => Path.Combine(DataDirectory, "certs");
    public string WorkDirectory => Path.Combine(DataDirectory, "work");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(CertificatesDirectory);
        Directory.CreateDirectory(WorkDirectory);
        HardenDataDirectoryAcl(DataDirectory);
    }

    private static bool _aclHardened;

    /// <summary>
    /// Restricts the agent's data folder to SYSTEM and local administrators. DPAPI machine-scope
    /// secrets are decryptable by any local user, so the file ACL is the boundary that prevents a
    /// standard user from copying the state file and impersonating the agent.
    /// </summary>
    private static void HardenDataDirectoryAcl(string dataDirectory)
    {
        if (_aclHardened || !OperatingSystem.IsWindows() || !IsAdministrator())
        {
            return;
        }

        _aclHardened = true;
        try
        {
            var startInfo = new ProcessStartInfo("icacls.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add(dataDirectory);
            startInfo.ArgumentList.Add("/T");
            startInfo.ArgumentList.Add("/inheritance:r");
            startInfo.ArgumentList.Add("/remove:g");
            startInfo.ArgumentList.Add("*S-1-1-0");      // Everyone
            startInfo.ArgumentList.Add("*S-1-5-11");     // Authenticated Users
            startInfo.ArgumentList.Add("*S-1-5-32-545"); // Users
            startInfo.ArgumentList.Add("/grant:r");
            startInfo.ArgumentList.Add("*S-1-5-18:(OI)(CI)F");      // SYSTEM
            startInfo.ArgumentList.Add("*S-1-5-32-544:(OI)(CI)F");  // Administrators

            using var process = Process.Start(startInfo);
            process?.WaitForExit(5000);
        }
        catch
        {
            // ACL hardening is best effort; the service still protects secrets with DPAPI.
        }
    }

    /// <summary>
    /// Parses agent command-line switches. Supported:
    /// --console, --data &lt;dir&gt;, --pair &lt;file&gt;, --hub &lt;url&gt;, --port &lt;n&gt;
    /// </summary>
    public static AgentOptions Parse(string[] args)
    {
        var options = new AgentOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--console":
                    options.RunAsConsole = true;
                    break;
                case "--data" when i + 1 < args.Length:
                    options.DataDirectory = Path.GetFullPath(args[++i]);
                    break;
                case "--pair" when i + 1 < args.Length:
                    options.PairingFilePath = Path.GetFullPath(args[++i]);
                    break;
                case "--hub" when i + 1 < args.Length:
                    options.HubUrlOverride = args[++i];
                    break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[++i], out var port):
                    options.PortOverride = port;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(options.PairingFilePath))
        {
            var candidate = Path.Combine(options.DataDirectory, "enroll.rdhenroll");
            if (File.Exists(candidate))
            {
                options.PairingFilePath = candidate;
            }
        }

        return options;
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
