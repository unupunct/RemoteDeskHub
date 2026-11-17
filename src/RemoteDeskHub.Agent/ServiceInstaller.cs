using System.Diagnostics;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Agent;

/// <summary>Installs, removes and controls the RemoteDesk Agent Windows service using sc.exe.</summary>
public static class ServiceInstaller
{
    private const string ServiceName = HubConstants.AgentServiceName;

    public static int Install(AgentOptions options)
    {
        if (!AgentOptions.IsAdministrator())
        {
            Console.Error.WriteLine("Installing the RemoteDesk Agent service requires an elevated administrator prompt.");
            return 5;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            Console.Error.WriteLine("The agent executable path could not be determined.");
            return 5;
        }

        AgentEventLog.EnsureSource();
        options.EnsureDirectories();

        if (ServiceExists())
        {
            Console.WriteLine($"Service '{ServiceName}' already exists. Reconfiguring it.");
            StopService();
            RunSc($"delete {ServiceName}");
            Thread.Sleep(1000);
        }

        var binaryPath = exePath;
        var defaultData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteDesk Agent");
        if (!string.Equals(Path.GetFullPath(options.DataDirectory).TrimEnd('\\'), Path.GetFullPath(defaultData).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            binaryPath += $" --data \"{options.DataDirectory}\"";
        }

        var createArguments = $"create {ServiceName} binPath= \"\\\"{binaryPath}\\\"\" start= auto " +
                              $"DisplayName= \"{HubConstants.AgentServiceDisplayName}\" " +
                              "obj= LocalSystem";
        if (!RunSc(createArguments, out var createError))
        {
            Console.Error.WriteLine($"The service could not be created: {createError}");
            return 5;
        }

        RunSc($"description {ServiceName} \"{HubConstants.AgentServiceDescription}\"");
        RunSc($"failure {ServiceName} reset= 86400 actions= restart/60000/restart/120000/restart/300000");
        RunSc($"start {ServiceName}");

        Console.WriteLine($"Service '{ServiceName}' installed and started.");
        Console.WriteLine($"Data directory: {options.DataDirectory}");
        if (!string.IsNullOrWhiteSpace(options.PairingFilePath) && File.Exists(options.PairingFilePath))
        {
            Console.WriteLine("The pairing package will be consumed automatically on first start.");
        }
        else
        {
            var target = Path.Combine(options.DataDirectory, "enroll.rdhenroll");
            Console.WriteLine($"Place a pairing package at {target} and restart the service to enroll this computer.");
        }

        return 0;
    }

    public static int Uninstall()
    {
        if (!AgentOptions.IsAdministrator())
        {
            Console.Error.WriteLine("Removing the RemoteDesk Agent service requires an elevated administrator prompt.");
            return 5;
        }

        if (!ServiceExists())
        {
            Console.WriteLine($"Service '{ServiceName}' is not installed.");
            return 0;
        }

        StopService();
        if (!RunSc($"delete {ServiceName}", out var error))
        {
            Console.Error.WriteLine($"The service could not be removed: {error}");
            return 5;
        }

        Console.WriteLine($"Service '{ServiceName}' removed.");
        return 0;
    }

    public static int Start()
    {
        if (!ServiceExists())
        {
            Console.Error.WriteLine($"Service '{ServiceName}' is not installed.");
            return 5;
        }

        return RunSc($"start {ServiceName}") ? 0 : 5;
    }

    public static int Stop()
    {
        if (!ServiceExists())
        {
            Console.Error.WriteLine($"Service '{ServiceName}' is not installed.");
            return 5;
        }

        return StopService() ? 0 : 5;
    }

    private static bool StopService()
    {
        if (!RunSc($"stop {ServiceName}"))
        {
            return false;
        }

        // Wait briefly so the delete can succeed.
        for (var i = 0; i < 20; i++)
        {
            if (RunSc($"query {ServiceName}", out var output) &&
                output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            Thread.Sleep(500);
        }

        return true;
    }

    private static bool ServiceExists()
    {
        return RunSc($"query {ServiceName}", out var output) &&
               !output.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase) &&
               !output.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RunSc(string arguments) => RunSc(arguments, out _);

    private static bool RunSc(string arguments, out string output)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                output = "sc.exe could not be started.";
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            output = $"{stdout}\n{stderr}".Trim();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return false;
        }
    }
}
