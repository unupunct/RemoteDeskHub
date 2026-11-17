using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteDeskHub.Agent.Collectors;
using RemoteDeskHub.Agent.Enrollment;
using RemoteDeskHub.Agent.Files;
using RemoteDeskHub.Agent.State;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Agent.Hosting;

/// <summary>
/// Owns the agent lifecycle: enroll, expose the management API, then send heartbeats and telemetry
/// until the service is stopped or the hub revokes this agent.
/// </summary>
public sealed class AgentWorker(
    AgentOptions options,
    AgentStateStore stateStore,
    AgentTelemetryQueue telemetryQueue,
    EnrollmentClient enrollmentClient,
    AgentRuntimeState state,
    AgentApiServer apiServer,
    AgentSharedFolderService sharedFolder,
    MetricsCollector metrics,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private DateTime _lastErrorLogUtc = DateTime.MinValue;
    private DateTime _lastRenewalAttemptUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AgentEventLog.Write("RemoteDesk Agent service started.", System.Diagnostics.EventLogEntryType.Information);
        logger.LogInformation("RemoteDesk Agent {Version} starting on {Machine}.", HubConstants.ProductVersion, Environment.MachineName);

        try
        {
            if (!await EnrollWithRetryAsync(stoppingToken).ConfigureAwait(false))
            {
                return;
            }

            if (sharedFolder.TryEnsureRoot(out var sharedRoot, out var sharedError))
            {
                logger.LogInformation("Shared folder is ready at {Root}.", sharedRoot);
            }
            else if (!string.IsNullOrWhiteSpace(sharedError))
            {
                logger.LogWarning("The shared folder could not be created: {Error}", sharedError);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var identity = await stateStore.LoadAsync(stoppingToken).ConfigureAwait(false);
                var certificate = stateStore.LoadCertificate(identity);
                if (certificate is null)
                {
                    logger.LogError("The stored agent certificate could not be loaded. Re-enroll this computer.");
                    return;
                }

                // Renew well before expiry. The renewal request is authenticated with the current
                // certificate, so it must succeed while the old certificate is still valid.
                if (certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddDays(30))
                {
                    logger.LogInformation("The agent certificate expires on {Expiry:u}; requesting renewal.",
                        certificate.NotAfter.ToUniversalTime());
                    if (await enrollmentClient.TryRenewCertificateAsync(identity, certificate, stoppingToken).ConfigureAwait(false))
                    {
                        certificate.Dispose();
                        continue;
                    }

                    if (certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                    {
                        logger.LogError("The agent certificate has expired and could not be renewed. Re-enroll this computer.");
                        certificate.Dispose();
                        return;
                    }

                    logger.LogWarning("Certificate renewal failed; continuing with the current certificate until {Expiry:u}.",
                        certificate.NotAfter.ToUniversalTime());
                }

                state.Identity = identity;
                state.Certificate = certificate;

                try
                {
                    await apiServer.StartAsync(stoppingToken).ConfigureAwait(false);

                    using var http = CreateHubClient(identity, certificate);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var telemetryTask = RunTelemetryLoopAsync(http, linked.Token);
                    bool renewalRequested;
                    try
                    {
                        renewalRequested = await RunHeartbeatLoopAsync(http, certificate, linked.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        linked.Cancel();
                        try
                        {
                            await telemetryTask.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // Expected during shutdown or certificate reload.
                        }
                    }

                    if (!renewalRequested)
                    {
                        return;
                    }

                    logger.LogInformation("Reloading the agent after certificate renewal.");
                }
                finally
                {
                    await apiServer.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    state.Certificate = null;
                    certificate.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The RemoteDesk Agent stopped because of an unrecoverable error.");
            AgentEventLog.Write($"RemoteDesk Agent stopped with an error: {ex.Message}", System.Diagnostics.EventLogEntryType.Error);
        }
        finally
        {
            AgentEventLog.Write("RemoteDesk Agent service stopped.", System.Diagnostics.EventLogEntryType.Information);
            logger.LogInformation("RemoteDesk Agent stopped.");
        }
    }

    private async Task<bool> EnrollWithRetryAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(15);
        while (!cancellationToken.IsCancellationRequested)
        {
            var outcome = await enrollmentClient.EnsureEnrolledAsync(cancellationToken).ConfigureAwait(false);
            switch (outcome)
            {
                case EnrollmentOutcome.Enrolled:
                    return true;
                case EnrollmentOutcome.NoPairingPackage:
                    logger.LogWarning("Waiting for an enrollment package. Place enroll.rdhenroll in {Directory}.",
                        options.DataDirectory);
                    break;
                case EnrollmentOutcome.Rejected:
                    logger.LogError("Enrollment was rejected. An administrator must issue a new pairing package.");
                    break;
                case EnrollmentOutcome.AwaitingApproval:
                    logger.LogInformation("Enrollment is still awaiting administrator approval.");
                    break;
                default:
                    logger.LogWarning("Enrollment did not complete. Retrying in {Seconds} seconds.", delay.TotalSeconds);
                    break;
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromSeconds(Math.Min(300, delay.TotalSeconds * 2));
        }

        return false;
    }

    private async Task<bool> RunHeartbeatLoopAsync(
        HttpClient http,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var identity = state.Identity;
            if (identity?.AgentId is null || identity.ComputerId is null)
            {
                logger.LogError("The agent identity is incomplete; cannot send a heartbeat.");
                return false;
            }

            // A long-running service must renew without waiting for a restart. Return to the outer
            // loop so the API listener and HTTP client are rebuilt with the new private key.
            if (certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddDays(30) &&
                DateTime.UtcNow - _lastRenewalAttemptUtc > TimeSpan.FromHours(1))
            {
                _lastRenewalAttemptUtc = DateTime.UtcNow;
                if (await enrollmentClient.TryRenewCertificateAsync(identity, certificate, cancellationToken)
                        .ConfigureAwait(false))
                {
                    logger.LogInformation("Certificate renewal succeeded; restarting the agent listener.");
                    return true;
                }
            }

            var request = new HeartbeatRequestDto
            {
                AgentId = identity.AgentId.Value,
                ComputerId = identity.ComputerId.Value,
                MachineName = Environment.MachineName,
                MachineId = identity.MachineId,
                AgentVersion = HubConstants.ProductVersion,
                ListeningPort = apiServer.BoundPort,
                TimestampUtc = DateTimeOffset.UtcNow,
                Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
                Metrics = metrics.Collect()
            };

            try
            {
                using var response = await http.PostAsJsonAsync("/api/agent/heartbeat", request, JsonDefaults.Options, cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    logger.LogError("The hub rejected this agent's certificate. The agent may have been revoked.");
                    state.Revoked = true;
                    await apiServer.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    return false;
                }

                if (!response.IsSuccessStatusCode)
                {
                    consecutiveFailures++;
                    LogConnectionProblem($"Heartbeat returned HTTP {(int)response.StatusCode}.", consecutiveFailures);
                }
                else
                {
                    consecutiveFailures = 0;
                    var heartbeat = await response.Content
                        .ReadFromJsonAsync<HeartbeatResponseDto>(JsonDefaults.Options, cancellationToken)
                        .ConfigureAwait(false);
                    if (heartbeat is not null)
                    {
                        if (heartbeat.Config is not null)
                        {
                            state.Apply(heartbeat.Config);
                        }

                        if (heartbeat.Revoked || heartbeat.Config?.Revoked == true)
                        {
                            logger.LogError("The hub revoked this agent: {Message}", heartbeat.Message ?? "revoked");
                            state.Revoked = true;
                            await apiServer.StopAsync(CancellationToken.None).ConfigureAwait(false);
                            return false;
                        }

                        state.Approved = heartbeat.Approved;
                        if (!heartbeat.Approved)
                        {
                            LogConnectionProblem(heartbeat.Message ?? "Awaiting administrator approval.", 1);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                consecutiveFailures++;
                LogConnectionProblem(ex.Message, consecutiveFailures);
            }

            var delay = TimeSpan.FromSeconds(Math.Clamp(state.Config.HeartbeatSeconds, 3, 300));
            if (consecutiveFailures > 0)
            {
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds + consecutiveFailures * 5));
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task RunTelemetryLoopAsync(HttpClient http, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var identity = state.Identity;
            if (identity?.AgentId is null)
            {
                return;
            }

            var current = metrics.Collect();
            IReadOnlyList<AgentMetricsDto> buffered;
            try
            {
                buffered = await telemetryQueue.PeekAsync(49, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var samples = buffered.Append(current).OrderBy(s => s.TimestampUtc).ToList();
                var batch = new TelemetryBatchDto
                {
                    AgentId = identity.AgentId.Value,
                    Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)),
                    // The batch envelope is always fresh so the hub's anti-replay guard accepts
                    // samples that were collected while this agent was offline.
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Samples = samples
                };
                using var response = await http.PostAsJsonAsync("/api/agent/telemetry", batch, JsonDefaults.Options, cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return;
                }

                if (response.IsSuccessStatusCode)
                {
                    if (buffered.Count > 0)
                    {
                        await telemetryQueue.AcknowledgeAsync(buffered.Count, cancellationToken).ConfigureAwait(false);
                        logger.LogInformation("Flushed {Count} buffered telemetry sample(s) to the hub.", buffered.Count);
                    }
                }
                else
                {
                    await telemetryQueue.EnqueueAsync(current, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogDebug("Telemetry delivery failed; buffering the sample locally. {Message}", ex.Message);
                    try
                    {
                        await telemetryQueue.EnqueueAsync(current, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }

            // Drain a backlog promptly without flooding the hub; otherwise use the configured cadence.
            var delay = buffered.Count > 0
                ? TimeSpan.FromSeconds(1)
                : TimeSpan.FromSeconds(Math.Clamp(state.Config.MetricsSeconds, 2, 300));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private HttpClient CreateHubClient(AgentIdentity identity, X509Certificate2 certificate)
    {
        if (string.IsNullOrWhiteSpace(identity.HubUrl) || string.IsNullOrWhiteSpace(identity.CaCertificatePem))
        {
            throw new InvalidOperationException("The agent has no hub URL or CA certificate.");
        }

        if (string.IsNullOrWhiteSpace(identity.HubServerCertificateThumbprint))
        {
            throw new InvalidOperationException(
                "The agent has no pinned hub server certificate. Re-enroll this computer with a current pairing package.");
        }

        var expectedServerThumbprint = identity.HubServerCertificateThumbprint;
        var caCertificatePem = identity.CaCertificatePem;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                ClientCertificates = new X509CertificateCollection { certificate },
                RemoteCertificateValidationCallback = (_, presented, _, _) =>
                {
                    if (presented is null)
                    {
                        return false;
                    }

                    try
                    {
                        using var server = new X509Certificate2(presented);
                        if (!string.Equals(server.Thumbprint, expectedServerThumbprint, StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        using var root = X509Certificate2.CreateFromPem(caCertificatePem);
                        using var chain = new X509Chain();
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.Add(root);
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        if (!chain.Build(server))
                        {
                            return false;
                        }

                        var eku = server.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
                        return eku is not null && eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>()
                            .Any(o => o.Value == "1.3.6.1.5.5.7.3.1");
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(identity.HubUrl.TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(35),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
    }

    private void LogConnectionProblem(string message, int failures)
    {
        var now = DateTime.UtcNow;
        if (now - _lastErrorLogUtc < TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Max(1, failures))))
        {
            return;
        }

        _lastErrorLogUtc = now;
        logger.LogWarning("Hub communication problem ({Failures} consecutive attempts): {Message}", failures, message);
    }
}
