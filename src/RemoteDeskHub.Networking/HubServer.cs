using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Infrastructure.Services;
using RemoteDeskHub.Networking.Security;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Networking;

public sealed class HubServer : IHubServer, IAsyncDisposable
{
    private readonly IDbContextFactory<RemoteDeskDbContext> _contextFactory;
    private readonly IEnrollmentService _enrollment;
    private readonly IAgentLifecycleService _lifecycle;
    private readonly ICertificateAuthority _certificates;
    private readonly ISettingsService _settings;
    private readonly IAppEvents _events;
    private readonly IAuditService _audit;
    private readonly ReplayGuard _replay;
    private readonly ILogger<HubServer> _logger;

    private WebApplication? _app;
    private int _boundPort;

    public HubServer(
        IDbContextFactory<RemoteDeskDbContext> contextFactory,
        IEnrollmentService enrollment,
        IAgentLifecycleService lifecycle,
        ICertificateAuthority certificates,
        ISettingsService settings,
        IAppEvents events,
        IAuditService audit,
        ReplayGuard replay,
        ILogger<HubServer> logger)
    {
        _contextFactory = contextFactory;
        _enrollment = enrollment;
        _lifecycle = lifecycle;
        _certificates = certificates;
        _settings = settings;
        _events = events;
        _audit = audit;
        _replay = replay;
        _logger = logger;
    }

    public bool IsRunning => _app is not null;
    public int BoundPort => _boundPort;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        await _certificates.EnsureCreatedAsync(cancellationToken);
        var configuredPort = await _settings.GetAsync(SettingsKeys.HubPort, HubConstants.DefaultHubPort, cancellationToken);
        var bindText = await _settings.GetAsync(SettingsKeys.BindAddress, "0.0.0.0", cancellationToken);
        var bindAddress = ParseBindAddress(bindText);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "RemoteDeskHub",
            EnvironmentName = Environments.Production
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ForwardingLoggerProvider(_logger));

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("enrollment", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
            options.AddPolicy("enrollment-status", context =>
            {
                // Status polling is long-lived and may legitimately have many agents behind one
                // NAT address. Partition by token instead of IP; the token is a 256-bit secret.
                var token = context.Request.Headers["X-Enrollment-Token"].FirstOrDefault();
                if (string.IsNullOrWhiteSpace(token))
                {
                    token = context.Request.Query["token"].ToString();
                }

                var key = string.IsNullOrWhiteSpace(token)
                    ? context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
                    : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
                return RateLimitPartition.GetFixedWindowLimiter(
                    key,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 120,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });
            options.AddPolicy("agent", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.ClientCertificate?.Thumbprint ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 1200,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 20,
                        AutoReplenishment = true
                    }));
        });

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
            options.Listen(bindAddress, configuredPort, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(https =>
                {
                    https.ServerCertificate = _certificates.GetServerCertificate();
                    https.SslProtocols = SslProtocols.Tls13;
                    https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                    https.ClientCertificateValidation = (certificate, _, _) => _certificates.ValidateCertificate(certificate, requireServerAuthentication: false);
                });
            });
        });

        var app = builder.Build();
        app.UseRateLimiter();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api/enrollment") || path.StartsWithSegments("/api/health"))
            {
                await next(context);
                return;
            }

            var thumbprint = context.Connection.ClientCertificate?.Thumbprint;
            if (string.IsNullOrWhiteSpace(thumbprint))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new ApiErrorDto("certificate_required", "A client certificate is required."));
                return;
            }

            if (path.StartsWithSegments("/ws/live") || path.StartsWithSegments("/api/hub"))
            {
                if (!string.Equals(thumbprint, _certificates.GetClientCertificateThumbprint(), StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new ApiErrorDto("forbidden", "This endpoint requires the RemoteDesk Hub console certificate."));
                    return;
                }

                context.Items["certificateThumbprint"] = thumbprint;
                await next(context);
                return;
            }

            if (path.StartsWithSegments("/api/agent"))
            {
                await using var db = await _contextFactory.CreateDbContextAsync(context.RequestAborted);
                var agent = await db.Agents.AsNoTracking()
                    .Where(a => a.CertificateThumbprint == thumbprint)
                    .Select(a => new { a.Id, a.ComputerId, a.State })
                    .FirstOrDefaultAsync(context.RequestAborted);
                if (agent is null || agent.State is AgentState.Revoked or AgentState.Rejected)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new ApiErrorDto("agent_rejected", "This agent certificate is not approved."));
                    return;
                }

                context.Items["agentId"] = agent.Id;
                context.Items["computerId"] = agent.ComputerId;
                context.Items["certificateThumbprint"] = thumbprint;
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
        });

        MapEndpoints(app);

        await app.StartAsync(cancellationToken);
        _app = app;
        _boundPort = ResolveBoundPort(app, configuredPort);
        _logger.LogInformation("RemoteDesk Hub server listening on https://{Address}:{Port}", bindAddress, _boundPort);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var app = _app;
        if (app is null)
        {
            return;
        }

        _app = null;
        try
        {
            await app.StopAsync(cancellationToken);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Json(new
        {
            status = "ok",
            product = HubConstants.ProductName,
            version = HubConstants.ProductVersion,
            timeUtc = DateTime.UtcNow
        }));

        app.MapGet("/api/hub/info", () => Results.Json(new
        {
            product = HubConstants.ProductName,
            version = HubConstants.ProductVersion,
            port = _boundPort,
            serverThumbprint = _certificates.GetServerCertificateThumbprint(),
            clientThumbprint = _certificates.GetClientCertificateThumbprint(),
            caCertificatePem = _certificates.GetCaCertificatePem()
        }));

        app.MapPost("/api/enrollment/request", async (HttpContext context, EnrollmentRequestDto request) =>
        {
            var remoteIp = context.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? string.Empty;
            var result = await _enrollment.HandleEnrollmentRequestAsync(request, remoteIp, context.RequestAborted);
            return result.Success
                ? Results.Json(result.Value, statusCode: StatusCodes.Status202Accepted)
                : Results.Json(new ApiErrorDto("enrollment_failed", result.Error ?? "Enrollment failed."), statusCode: StatusCodes.Status400BadRequest);
        }).RequireRateLimiting("enrollment");

        app.MapGet("/api/enrollment/status/{id:guid}", async (HttpContext context, Guid id) =>
        {
            // Prefer the header so the bearer token does not appear in request logs or proxy
            // history. The query parameter is retained for older agents.
            var token = context.Request.Headers["X-Enrollment-Token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token))
            {
                token = context.Request.Query["token"].ToString();
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                return Results.BadRequest(new ApiErrorDto("token_required", "The enrollment token is required."));
            }

            var status = await _enrollment.GetEnrollmentStatusAsync(id, token, context.RequestAborted);
            return Results.Json(status);
        }).RequireRateLimiting("enrollment-status");

        app.MapPost("/api/agent/heartbeat", async (HttpContext context, HeartbeatRequestDto request) =>
        {
            var thumbprint = context.Items["certificateThumbprint"] as string;
            var remoteIp = context.Connection.RemoteIpAddress?.MapToIPv4().ToString();
            try
            {
                var response = await _lifecycle.ProcessHeartbeatAsync(request, thumbprint, remoteIp, context.RequestAborted);
                return Results.Json(response);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new ApiErrorDto("unauthorized", ex.Message), statusCode: StatusCodes.Status403Forbidden);
            }
            catch (RemoteDeskHub.Core.Security.ReplayDetectedException ex)
            {
                return Results.Json(new ApiErrorDto("replay_detected", ex.Message), statusCode: StatusCodes.Status409Conflict);
            }
        }).RequireRateLimiting("agent");

        app.MapPost("/api/agent/telemetry", async (HttpContext context, TelemetryBatchDto batch) =>
        {
            var thumbprint = context.Items["certificateThumbprint"] as string;
            var result = await _lifecycle.RecordTelemetryAsync(batch, thumbprint, context.RequestAborted);
            return result.Success ? Results.Ok() : Results.BadRequest(new ApiErrorDto("telemetry_rejected", result.Error ?? "Telemetry rejected."));
        }).RequireRateLimiting("agent");

        app.MapPost("/api/agent/renew", async (HttpContext context, AgentCertificateRenewalRequestDto request) =>
        {
            var thumbprint = context.Items["certificateThumbprint"] as string;
            if (string.IsNullOrWhiteSpace(thumbprint))
            {
                return Results.Unauthorized();
            }

            if (!_replay.TryAccept("agent-renew:" + thumbprint, request.Nonce, request.TimestampUtc, out var replayError))
            {
                return Results.Json(new ApiErrorDto("replay_detected", replayError ?? "Replay detected."),
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (string.IsNullOrWhiteSpace(request.CsrPem) || request.CsrPem.Length > 32_000)
            {
                return Results.BadRequest(new ApiErrorDto("invalid_csr", "A certificate signing request is required."));
            }

            await using var db = await _contextFactory.CreateDbContextAsync(context.RequestAborted);
            var agent = await db.Agents.Include(a => a.Computer)
                .FirstOrDefaultAsync(a => a.CertificateThumbprint == thumbprint, context.RequestAborted);
            if (agent is null || agent.State is AgentState.Revoked or AgentState.Rejected)
            {
                return Results.Json(new ApiErrorDto("agent_rejected", "This agent certificate is not approved."),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (request.AgentId != Guid.Empty && request.AgentId != agent.Id)
            {
                return Results.BadRequest(new ApiErrorDto("agent_mismatch", "The renewal request does not match this agent."));
            }

            if (!string.Equals(request.MachineName, agent.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new ApiErrorDto("machine_mismatch", "The renewal request does not match this computer."));
            }

            string certificatePem;
            try
            {
                certificatePem = await _certificates.SignAgentCertificateAsync(
                    request.CsrPem, agent.MachineName, agent.Id, context.RequestAborted);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Certificate renewal failed for agent {AgentId}.", agent.Id);
                return Results.BadRequest(new ApiErrorDto("renewal_failed", ex.Message));
            }

            using var renewed = X509Certificate2.CreateFromPem(certificatePem);
            var oldThumbprint = agent.CertificateThumbprint;
            var renewedUtc = DateTime.UtcNow;
            var updated = await db.Agents
                .Where(a => a.Id == agent.Id && a.CertificateThumbprint == oldThumbprint)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(a => a.CertificateThumbprint, renewed.Thumbprint)
                    .SetProperty(a => a.CertificatePem, certificatePem)
                    .SetProperty(a => a.Version, request.AgentVersion ?? agent.Version)
                    .SetProperty(a => a.LastSeenUtc, renewedUtc), context.RequestAborted);
            if (updated == 0)
            {
                return Results.Json(new ApiErrorDto("renewal_conflict", "The agent certificate changed during renewal."),
                    statusCode: StatusCodes.Status409Conflict);
            }

            await _audit.LogAsync(
                HubConstants.AuditActorSystem,
                "Agent.CertificateRenewed",
                agent.Computer?.Name ?? agent.MachineName,
                agent.ComputerId,
                details: $"Agent {agent.Id} certificate {oldThumbprint} replaced with {renewed.Thumbprint}.",
                cancellationToken: context.RequestAborted);

            return Results.Json(new AgentCertificateRenewalResponseDto
            {
                AgentCertificatePem = certificatePem,
                CaCertificatePem = _certificates.GetCaCertificatePem(),
                HubServerCertificateThumbprint = _certificates.GetServerCertificateThumbprint(),
                HubClientCertificateThumbprint = _certificates.GetClientCertificateThumbprint(),
                RenewedUtc = renewedUtc,
                Message = "Agent certificate renewed."
            });
        }).RequireRateLimiting("agent");

        app.MapGet("/api/agent/config", async (HttpContext context) =>
        {
            var config = await _lifecycle.BuildRuntimeConfigAsync(context.RequestAborted);
            return Results.Json(config);
        }).RequireRateLimiting("agent");

        app.Map("/ws/live", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var channel = Channel.CreateBounded<object>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

            void OnComputer(object? sender, Guid id) => channel.Writer.TryWrite(new { type = "computer", id });
            void OnNotification(object? sender, AppNotification notification) => channel.Writer.TryWrite(new
            {
                type = "notification",
                title = notification.Title,
                message = notification.Message,
                severity = notification.Severity.ToString(),
                computerId = notification.ComputerId,
                timestampUtc = notification.When
            });
            void OnData(object? sender, EventArgs args) => channel.Writer.TryWrite(new { type = "data" });

            _events.ComputerChanged += OnComputer;
            _events.NotificationRaised += OnNotification;
            _events.DataChanged += OnData;
            try
            {
                var buffer = new byte[16 * 1024];
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                var receive = ReceiveUntilClosedAsync(socket, buffer, linked);
                await foreach (var message in channel.Reader.ReadAllAsync(linked.Token))
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonDefaults.Options);
                    await socket.SendAsync(bytes, System.Net.WebSockets.WebSocketMessageType.Text, true, linked.Token);
                }

                await receive;
            }
            catch (OperationCanceledException)
            {
                // Client disconnected.
            }
            catch (System.Net.WebSockets.WebSocketException)
            {
                // Client disconnected during shutdown.
            }
            finally
            {
                _events.ComputerChanged -= OnComputer;
                _events.NotificationRaised -= OnNotification;
                _events.DataChanged -= OnData;
                channel.Writer.TryComplete();
            }
        }).RequireRateLimiting("agent");
    }

    private static async Task ReceiveUntilClosedAsync(System.Net.WebSockets.WebSocket socket, byte[] buffer, CancellationTokenSource cts)
    {
        try
        {
            while (socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    cts.Cancel();
                    return;
                }
            }
        }
        catch
        {
            cts.Cancel();
        }
    }

    private static IPAddress ParseBindAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Any;
        }

        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(value, out var address)
            ? address
            : throw new InvalidOperationException($"Invalid bind address '{value}'. Use 0.0.0.0, localhost or a local IPv4 address.");
    }

    private static int ResolveBoundPort(WebApplication app, int configuredPort)
    {
        if (configuredPort != 0)
        {
            return configuredPort;
        }

        var addresses = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>()?
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?
            .Addresses;
        var address = addresses?.FirstOrDefault();
        if (address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return uri.Port;
        }

        return configuredPort;
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
                => logger.Log(logLevel, exception, "[Kestrel:{Category}] {Message}", category, formatter(state, exception));
        }
    }
}
