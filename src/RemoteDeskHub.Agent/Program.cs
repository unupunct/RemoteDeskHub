using RemoteDeskHub.Agent;
using RemoteDeskHub.Agent.Collectors;
using RemoteDeskHub.Agent.Commands;
using RemoteDeskHub.Agent.Enrollment;
using RemoteDeskHub.Agent.Files;
using RemoteDeskHub.Agent.Hosting;
using RemoteDeskHub.Agent.State;
using RemoteDeskHub.Shared;
using Serilog;
using Serilog.Events;

var options = AgentOptions.Parse(args);
var command = args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant();

if (command is "--help" or "-h" or "/?")
{
    Console.WriteLine("""
        RemoteDesk Agent

        Usage:
          RemoteDeskHub.Agent.exe                 Run in the foreground (console) or as a Windows service.
          RemoteDeskHub.Agent.exe --install       Install and start the Windows service (administrator).
          RemoteDeskHub.Agent.exe --uninstall     Stop and remove the Windows service (administrator).
          RemoteDeskHub.Agent.exe --start         Start the installed service.
          RemoteDeskHub.Agent.exe --stop          Stop the installed service.
          RemoteDeskHub.Agent.exe --console       Run in the foreground with console logging.
          RemoteDeskHub.Agent.exe --pair <file>   Import a pairing package (.rdhenroll) and enroll.
          RemoteDeskHub.Agent.exe --stage-pairing Copy a pairing package into the data directory and exit.
          RemoteDeskHub.Agent.exe --data <dir>    Use a custom data directory.
          RemoteDeskHub.Agent.exe --hub <url>     Override the hub URL from the pairing package.
          RemoteDeskHub.Agent.exe --port <n>      Override the management port.

        The agent never elevates itself and only accepts management connections from the hub
        certificate pinned during enrollment.
        """);
    return 0;
}

if (command == "--version")
{
    Console.WriteLine($"{HubConstants.AgentProductName} {HubConstants.ProductVersion}");
    return 0;
}

if (command == "--install")
{
    StagePairingPackage(options);
    return ServiceInstaller.Install(options);
}

if (command == "--uninstall")
{
    return ServiceInstaller.Uninstall();
}

if (command == "--start")
{
    return ServiceInstaller.Start();
}

if (command == "--stop")
{
    return ServiceInstaller.Stop();
}

if (command == "--stage-pairing")
{
    if (string.IsNullOrWhiteSpace(options.PairingFilePath))
    {
        Console.Error.WriteLine("A pairing package path is required: --pair <file> --stage-pairing");
        return 2;
    }

    if (!File.Exists(options.PairingFilePath))
    {
        Console.Error.WriteLine($"The pairing package was not found: {options.PairingFilePath}");
        return 2;
    }

    StagePairingPackage(options);
    return 0;
}

options.EnsureDirectories();
if (options.RunAsConsole || !Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
{
    try
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
    }
    catch
    {
        // Some hosts have no console.
    }
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(options.LogDirectory, "agent-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 20 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        shared: true)
    .CreateLogger();

try
{
    var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
        new Microsoft.Extensions.Hosting.HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory
        });

    builder.Services.AddWindowsService(service => service.ServiceName = HubConstants.AgentServiceName);
    builder.Logging.ClearProviders();
    builder.Services.AddSerilog(Log.Logger, dispose: true);
    builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(20));

    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<AgentStateStore>();
    builder.Services.AddSingleton<AgentTelemetryQueue>();
    builder.Services.AddSingleton<AgentRuntimeState>();
    builder.Services.AddSingleton<EnrollmentClient>();
    builder.Services.AddSingleton<MetricsCollector>();
    builder.Services.AddSingleton<HardwareCollector>();
    builder.Services.AddSingleton<SoftwareCollector>();
    builder.Services.AddSingleton<ProcessCollector>();
    builder.Services.AddSingleton<ServiceCollector>();
    builder.Services.AddSingleton<EventLogCollector>();
    builder.Services.AddSingleton<CommandRunner>();
    builder.Services.AddSingleton<AgentFileService>();
    builder.Services.AddSingleton<AgentSharedFolderService>();
    builder.Services.AddSingleton<AgentApiServer>();
    builder.Services.AddHostedService<AgentWorker>();

    AgentEventLog.Write("RemoteDesk Agent process starting.", System.Diagnostics.EventLogEntryType.Information);
    var host = builder.Build();
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "RemoteDesk Agent terminated unexpectedly.");
    AgentEventLog.Write($"RemoteDesk Agent terminated unexpectedly: {ex.Message}", System.Diagnostics.EventLogEntryType.Error);
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static void StagePairingPackage(AgentOptions options)
{
    if (string.IsNullOrWhiteSpace(options.PairingFilePath))
    {
        return;
    }

    var target = Path.Combine(options.DataDirectory, "enroll.rdhenroll");
    options.EnsureDirectories();
    if (string.Equals(Path.GetFullPath(options.PairingFilePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    File.Copy(options.PairingFilePath, target, true);
    options.PairingFilePath = target;
    Console.WriteLine($"Pairing package staged at {target}.");
}
