using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using RemoteDeskHub.Agent.Commands;
using RemoteDeskHub.Agent.Collectors;
using RemoteDeskHub.Agent.Files;
using RemoteDeskHub.Agent.State;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;
using RemoteDeskHub.Shared.Security;

namespace RemoteDeskHub.Agent.Hosting;

/// <summary>
/// HTTPS management API exposed by the agent. The listener requires mutual TLS and only accepts the
/// hub console certificate pinned during enrollment.
/// </summary>
public sealed class AgentApiServer(
    AgentRuntimeState state,
    MetricsCollector metrics,
    HardwareCollector hardware,
    SoftwareCollector software,
    ProcessCollector processes,
    ServiceCollector services,
    EventLogCollector eventLogs,
    CommandRunner commandRunner,
    AgentFileService fileService,
    AgentSharedFolderService sharedFolder,
    ILogger<AgentApiServer> logger) : IAsyncDisposable
{
    private WebApplication? _app;
    private X509Certificate2? _ca;
    private string? _pinnedThumbprint;
    private int _boundPort;

    public bool IsRunning => _app is not null;
    public int BoundPort => _boundPort;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        var identity = state.Identity ?? throw new InvalidOperationException("The agent has no enrollment state.");
        var certificate = state.Certificate ?? throw new InvalidOperationException("The agent has no certificate.");
        if (string.IsNullOrWhiteSpace(identity.CaCertificatePem))
        {
            throw new InvalidOperationException("The hub CA certificate is missing from the agent state.");
        }

        if (string.IsNullOrWhiteSpace(identity.HubClientCertificateThumbprint))
        {
            throw new InvalidOperationException("The hub client certificate thumbprint is missing from the agent state.");
        }

        _ca = X509Certificate2.CreateFromPem(identity.CaCertificatePem);
        _pinnedThumbprint = identity.HubClientCertificateThumbprint;
        var port = identity.ManagementPort is > 0 and < 65536 ? identity.ManagementPort : HubConstants.DefaultAgentPort;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "RemoteDeskAgent",
            EnvironmentName = Environments.Production
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ForwardingLoggerProvider(logger));

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("agent", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.ClientCertificate?.Thumbprint ?? "hub",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 600,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 20,
                        AutoReplenishment = true
                    }));
        });

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 16 * 1024 * 1024;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(20);
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
            options.Listen(IPAddress.Any, port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(https =>
                {
                    https.ServerCertificate = certificate;
                    https.SslProtocols = SslProtocols.Tls13;
                    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    https.ClientCertificateValidation = (presented, _, _) => ValidateHubClient(presented);
                });
            });
        });

        var app = builder.Build();
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            // Defence in depth: even with a permissive firewall or a misconfigured bind address,
            // never expose the management API to a non-private network.
            var remote = context.Connection.RemoteIpAddress;
            if (remote is not null && remote.IsIPv4MappedToIPv6)
            {
                remote = remote.MapToIPv4();
            }

            if (remote is null || !NetworkAddressGuard.IsPrivateOrLoopback(remote))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(
                    new ApiErrorDto("lan_only", "The agent management API only accepts private LAN clients."));
                return;
            }

            await next();
        });
        app.Use(ExceptionMiddleware);
        MapEndpoints(app);

        await app.StartAsync(cancellationToken);
        _app = app;
        _boundPort = port;
        state.ApiRunning = true;
        logger.LogInformation("RemoteDesk Agent management API listening on https://0.0.0.0:{Port}", port);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var app = _app;
        if (app is null)
        {
            return;
        }

        _app = null;
        state.ApiRunning = false;
        try
        {
            await app.StopAsync(cancellationToken);
        }
        finally
        {
            await app.DisposeAsync();
            _ca?.Dispose();
            _ca = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
    }

    private bool ValidateHubClient(X509Certificate2? presented)
    {
        if (presented is null || _ca is null || string.IsNullOrWhiteSpace(_pinnedThumbprint))
        {
            return false;
        }

        try
        {
            if (!string.Equals(presented.Thumbprint, _pinnedThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(_ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (!chain.Build(presented))
            {
                return false;
            }

            var eku = presented.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
            return eku is null || eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>()
                .Any(o => o.Value == "1.3.6.1.5.5.7.3.2");
        }
        catch
        {
            return false;
        }
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/agent/info", () => Results.Json(BuildInfo()));
        app.MapGet("/api/agent/metrics", () => Results.Json(metrics.Collect()));
        app.MapGet("/api/agent/inventory/hardware", () => Results.Json(hardware.Collect()));
        app.MapGet("/api/agent/inventory/software", () => Results.Json(software.Collect()));

        app.MapGet("/api/agent/processes", () => Results.Json(processes.Collect()));
        app.MapPost("/api/agent/processes/{pid:int}/terminate", (int pid, string? name) =>
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest(new ApiErrorDto("name_required", "The process name is required."));
            }

            if (ProtectedProcesses.IsProtected(name))
            {
                return Results.Json(OperationResult.Fail(
                    $"'{name}' is a protected Windows process and cannot be terminated through RemoteDesk Agent."),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                process.Kill(entireProcessTree: false);
                logger.LogWarning("Terminated remote process {Name} ({Pid}).", name, pid);
                return Results.Json(OperationResult.Ok($"Process {name} ({pid}) terminated."));
            }
            catch (ArgumentException)
            {
                return Results.Json(OperationResult.Fail($"Process {pid} is no longer running."));
            }
            catch (Exception ex)
            {
                return Results.Json(OperationResult.Fail(ex.Message));
            }
        });

        app.MapGet("/api/agent/services", () => Results.Json(services.Collect()));
        app.MapGet("/api/agent/services/{name}/dependencies", (string name) =>
        {
            var service = services.GetDependencies(name);
            return service is null
                ? Results.NotFound(new ApiErrorDto("service_not_found", $"Service '{name}' was not found."))
                : Results.Json(new[] { service });
        });

        app.MapPost("/api/agent/services/{name}/action", (string name, ServiceActionRequest request) =>
        {
            if (!ServiceCollector.IsValidServiceName(name))
            {
                return Results.BadRequest(new ApiErrorDto("invalid_service", "The service name is invalid."));
            }

            return Results.Json(ExecuteServiceAction(name, request));
        });

        app.MapPost("/api/agent/execute", async (CommandRequestDto request, CancellationToken cancellationToken) =>
        {
            if (!state.CommandsEnabled)
            {
                return Results.Json(new ApiErrorDto("commands_disabled", "Remote command execution is disabled by the hub administrator."),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (request.RequireElevation && !IsElevated())
            {
                return Results.Json(new ApiErrorDto("elevation_unavailable",
                    "The agent is not running with the privileges required for this command."),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var result = await commandRunner.ExecuteAsync(request, state.MaxCommandTimeoutSeconds, cancellationToken);
            return Results.Json(result);
        });

        app.MapDelete("/api/agent/execute/{correlationId:guid}", (Guid correlationId) =>
            Results.Json(commandRunner.Cancel(correlationId)
                ? OperationResult.Ok("Cancellation requested.")
                : OperationResult.Fail("No running command with that correlation id was found.")));

        app.MapGet("/api/agent/events", (string? log, int? max) =>
        {
            var logName = string.IsNullOrWhiteSpace(log) ? "System" : log;
            return Results.Json(eventLogs.Collect(logName, max ?? 100));
        });

        app.MapGet("/api/agent/shared-folder/manifest", async (CancellationToken cancellationToken) =>
            Results.Json(await sharedFolder.BuildManifestAsync(cancellationToken)));
        app.MapGet("/api/agent/files/list", (string path) => Results.Json(fileService.List(path)));
        app.MapGet("/api/agent/files/stat", (string path, bool? hash) => Results.Json(fileService.Stat(path, hash ?? false)));
        app.MapGet("/api/agent/files/read", (string path, long offset, int? length) =>
            Results.Json(fileService.ReadChunk(path, offset, length ?? HubConstants.MaxTransferChunkBytes)));
        app.MapPost("/api/agent/files/write", (FileWriteRequestDto request) => Results.Json(fileService.WriteChunk(request)));
        app.MapPost("/api/agent/files/mkdir", (FilePathRequest request) =>
            Guard(() => fileService.CreateDirectory(request.Path), "Folder created."));
        app.MapPost("/api/agent/files/rename", (FileRenameRequest request) =>
            Guard(() => fileService.Rename(request.Path, request.NewName, request.Confirmed), "Item renamed."));
        app.MapPost("/api/agent/files/delete", (FileDeleteRequest request) =>
            Guard(() => fileService.Delete(request.Path, request.Recursive, request.Confirmed), "Item deleted."));
        app.MapGet("/api/agent/files/hash", (string path) => Results.Json(fileService.Hash(path)));
        app.MapPost("/api/agent/files/finalize", (FileFinalizeRequestDto request) =>
            Guard(() => fileService.FinalizeUpload(request.TemporaryPath, request.FinalPath), "Upload finalized."));
    }

    private OperationResult ExecuteServiceAction(string name, ServiceActionRequest request)
    {
        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("start" or "stop" or "restart" or "pause" or "resume" or "startup"))
        {
            return OperationResult.Fail($"Unsupported service action '{action}'.");
        }

        if (action is "stop" or "restart" or "pause" && !request.Confirmed)
        {
            return OperationResult.Fail("This service action requires explicit confirmation.");
        }

        if (CriticalServices.IsCritical(name) && action is "stop" or "restart" && !request.Confirmed)
        {
            return OperationResult.Fail($"'{name}' is a critical Windows service and requires explicit confirmation.");
        }

        try
        {
            using var controller = new System.ServiceProcess.ServiceController(name);
            switch (action)
            {
                case "start":
                    if (controller.Status != System.ServiceProcess.ServiceControllerStatus.Running)
                    {
                        controller.Start();
                        controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    }

                    break;
                case "stop":
                    if (controller.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                    {
                        controller.Stop();
                        controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                    }

                    break;
                case "restart":
                    if (controller.CanStop && controller.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                    {
                        controller.Stop();
                        controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                    }

                    controller.Start();
                    controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
                    break;
                case "pause":
                    controller.Pause();
                    controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Paused, TimeSpan.FromSeconds(30));
                    break;
                case "resume":
                    controller.Continue();
                    controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    break;
                case "startup":
                    return SetStartupType(name, request.StartupType);
            }

            return OperationResult.Ok($"Service '{name}': {action} completed.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Service action {Action} failed for {Service}.", action, name);
            return OperationResult.Fail(ex.Message);
        }
    }

    private static OperationResult SetStartupType(string name, string? startupType)
    {
        var value = (startupType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "automatic" => "auto",
            "automaticdelayedstart" => "delayed-auto",
            "manual" => "demand",
            "disabled" => "disabled",
            _ => null
        };
        if (value is null)
        {
            return OperationResult.Fail("Choose a valid startup type (Automatic, AutomaticDelayedStart, Manual or Disabled).");
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "sc.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("config");
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add("start=");
        startInfo.ArgumentList.Add(value);
        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null)
        {
            return OperationResult.Fail("The service configuration utility could not be started.");
        }

        process.WaitForExit(15000);
        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            return OperationResult.Fail(string.IsNullOrWhiteSpace(error)
                ? $"sc.exe exited with code {process.ExitCode}."
                : error.Trim());
        }

        return OperationResult.Ok($"Service '{name}' startup type set to {startupType}.");
    }

    private static OperationResult Guard(Action action, string successMessage)
    {
        try
        {
            action();
            return OperationResult.Ok(successMessage);
        }
        catch (UnauthorizedAccessException ex)
        {
            return OperationResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    private AgentInfoDto BuildInfo()
    {
        var identity = state.Identity ?? new AgentIdentity();
        return new AgentInfoDto
        {
            AgentId = identity.AgentId ?? Guid.Empty,
            ComputerId = identity.ComputerId ?? Guid.Empty,
            MachineName = Environment.MachineName,
            AgentVersion = HubConstants.ProductVersion,
            OperatingSystem = Environment.OSVersion.VersionString,
            ListeningPort = _boundPort,
            StartedUtc = state.StartedUtc,
            Elevated = IsElevated(),
            AllowedFileRoots = state.AllowedRoots,
            CommandsEnabled = state.CommandsEnabled,
            UptimeSeconds = state.UptimeSeconds
        };
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static async Task ExceptionMiddleware(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (UnauthorizedAccessException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "access_denied", ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "not_found", ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "not_found", ex.Message);
        }
        catch (ArgumentException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_operation", ex.Message);
        }
        catch (OperationCanceledException)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 499;
            }
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "agent_error", ex.Message);
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new ApiErrorDto(code, message));
    }

    private sealed class ForwardingLoggerProvider(ILogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ForwardingLogger(logger, categoryName);
        public void Dispose() { }

        private sealed class ForwardingLogger(ILogger logger, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => logger.Log(logLevel, exception, "[AgentApi:{Category}] {Message}", category, formatter(state, exception));
        }
    }
}
