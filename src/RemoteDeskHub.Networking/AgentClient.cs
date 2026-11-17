using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;
using RemoteDeskHub.Shared.Security;

namespace RemoteDeskHub.Networking;

public sealed class AgentTransportException : Exception
{
    public AgentTransportException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

/// <summary>
/// HTTPS client used by the hub to call an approved agent's management API.
/// Mutual TLS is enforced; the agent's certificate is pinned to the thumbprint recorded at enrollment.
/// </summary>
public sealed class AgentClient : IAgentTransport, IAgentConnectionInfoProvider, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly ICertificateAuthority _certificates;
    private readonly ILogger<AgentClient> _logger;
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    public AgentClient(ICertificateAuthority certificates, ILogger<AgentClient> logger)
    {
        _certificates = certificates;
        _logger = logger;
    }

    public (string Host, int Port)? Resolve(Computer computer)
    {
        if (computer.Agent is null || computer.Agent.State != AgentState.Active ||
            string.IsNullOrWhiteSpace(computer.IpAddress))
        {
            return null;
        }

        return (computer.IpAddress, computer.Agent.ListeningPort);
    }

    public async Task<AgentInfoDto> GetInfoAsync(Computer computer, CancellationToken cancellationToken = default)
        => await GetAsync<AgentInfoDto>(computer, "api/agent/info", DefaultTimeout, cancellationToken).ConfigureAwait(false);

    public async Task<AgentMetricsDto> GetMetricsAsync(Computer computer, CancellationToken cancellationToken = default)
        => await GetAsync<AgentMetricsDto>(computer, "api/agent/metrics", DefaultTimeout, cancellationToken).ConfigureAwait(false);

    public async Task<HardwareInventoryDto> GetHardwareAsync(Computer computer, CancellationToken cancellationToken = default)
        => await GetAsync<HardwareInventoryDto>(computer, "api/agent/inventory/hardware", TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);

    public async Task<SoftwareInventoryDto> GetSoftwareAsync(Computer computer, CancellationToken cancellationToken = default)
        => await GetAsync<SoftwareInventoryDto>(computer, "api/agent/inventory/software", TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<RemoteProcessDto>> GetProcessesAsync(Computer computer, CancellationToken cancellationToken = default)
        => await GetAsync<List<RemoteProcessDto>>(computer, "api/agent/processes", TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult> TerminateProcessAsync(Computer computer, int pid, string processName, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(computer, HttpMethod.Post,
            $"api/agent/processes/{pid}/terminate?name={Uri.EscapeDataString(processName)}", null,
            TimeSpan.FromSeconds(30), idempotent: false, cancellationToken).ConfigureAwait(false);
        return await ReadOperationResultAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteServiceDto>> GetServicesAsync(Computer computer, CancellationToken cancellationToken = default)
        => await GetAsync<List<RemoteServiceDto>>(computer, "api/agent/services", TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<RemoteServiceDto>> GetServiceDependenciesAsync(Computer computer, string serviceName, CancellationToken cancellationToken = default)
        => await GetAsync<List<RemoteServiceDto>>(computer, $"api/agent/services/{Uri.EscapeDataString(serviceName)}/dependencies", TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult> ServiceActionAsync(Computer computer, string serviceName, string action, string? startupType, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(computer, HttpMethod.Post, $"api/agent/services/{Uri.EscapeDataString(serviceName)}/action",
            new ServiceActionRequest(action, startupType, true), TimeSpan.FromSeconds(60), idempotent: false, cancellationToken).ConfigureAwait(false);
        return await ReadOperationResultAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommandResultDto> ExecuteCommandAsync(Computer computer, CommandRequestDto request, CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 5, HubConstants.MaxCommandTimeoutSeconds) + 90);
        var response = await SendAsync(computer, HttpMethod.Post, "api/agent/execute", request, timeout, idempotent: false, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<CommandResultDto>(JsonDefaults.Options, cancellationToken).ConfigureAwait(false)
               ?? throw new AgentTransportException("The agent returned an empty command result.");
    }

    public async Task<OperationResult> CancelCommandAsync(Computer computer, Guid correlationId, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(computer, HttpMethod.Delete, $"api/agent/execute/{correlationId}", null,
            TimeSpan.FromSeconds(20), idempotent: true, cancellationToken).ConfigureAwait(false);
        return await ReadOperationResultAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteEventLogDto>> GetEventsAsync(Computer computer, string logName, int maxEntries, CancellationToken cancellationToken = default)
        => await GetAsync<List<RemoteEventLogDto>>(computer,
            $"api/agent/events?log={Uri.EscapeDataString(logName)}&max={Math.Clamp(maxEntries, 1, 500)}",
            TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);

    public async Task<SharedFolderManifestDto> GetSharedFolderManifestAsync(Computer computer, CancellationToken cancellationToken = default)
        => await GetAsync<SharedFolderManifestDto>(computer, "api/agent/shared-folder/manifest",
            TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);

    public async Task<FileListDto> ListFilesAsync(Computer computer, string path, CancellationToken cancellationToken = default)
        => await GetAsync<FileListDto>(computer, $"api/agent/files/list?path={Uri.EscapeDataString(path)}",
            TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

    public async Task<FileStatDto> StatFileAsync(Computer computer, string path, bool includeHash, CancellationToken cancellationToken = default)
        => await GetAsync<FileStatDto>(computer,
            $"api/agent/files/stat?path={Uri.EscapeDataString(path)}&hash={includeHash}",
            TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);

    public async Task<FileChunkDto> ReadFileChunkAsync(Computer computer, string path, long offset, int length, CancellationToken cancellationToken = default)
        => await GetAsync<FileChunkDto>(computer,
            $"api/agent/files/read?path={Uri.EscapeDataString(path)}&offset={offset}&length={Math.Clamp(length, 1, HubConstants.MaxTransferChunkBytes)}",
            TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);

    public async Task<FileWriteResultDto> WriteFileChunkAsync(Computer computer, FileWriteRequestDto request, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(computer, HttpMethod.Post, "api/agent/files/write", request,
            TimeSpan.FromMinutes(2), idempotent: false, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<FileWriteResultDto>(JsonDefaults.Options, cancellationToken).ConfigureAwait(false)
               ?? throw new AgentTransportException("The agent returned an empty write result.");
    }

    public async Task<OperationResult> CreateDirectoryAsync(Computer computer, string path, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(computer, HttpMethod.Post, "api/agent/files/mkdir", new FilePathRequest(path),
            TimeSpan.FromSeconds(30), idempotent: false, cancellationToken).ConfigureAwait(false);
        return await ReadOperationResultAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> RenameFileAsync(Computer computer, string path, string newName, bool confirmed, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(computer, HttpMethod.Post, "api/agent/files/rename", new FileRenameRequest(path, newName, confirmed),
            TimeSpan.FromSeconds(30), idempotent: false, cancellationToken).ConfigureAwait(false);
        return await ReadOperationResultAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> DeleteFileAsync(Computer computer, string path, bool recursive, bool confirmed, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(computer, HttpMethod.Post, "api/agent/files/delete", new FileDeleteRequest(path, recursive, confirmed),
            TimeSpan.FromSeconds(60), idempotent: false, cancellationToken).ConfigureAwait(false);
        return await ReadOperationResultAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileHashDto> GetFileHashAsync(Computer computer, string path, CancellationToken cancellationToken = default)
        => await GetAsync<FileHashDto>(computer, $"api/agent/files/hash?path={Uri.EscapeDataString(path)}",
            TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult> FinalizeUploadAsync(Computer computer, string temporaryPath, string finalPath, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(computer, HttpMethod.Post, "api/agent/files/finalize",
            new FileFinalizeRequestDto(temporaryPath, finalPath), TimeSpan.FromMinutes(2), idempotent: false, cancellationToken).ConfigureAwait(false);
        return await ReadOperationResultAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> PingAsync(Computer computer, CancellationToken cancellationToken = default)
    {
        try
        {
            await GetInfoAsync(computer, cancellationToken).ConfigureAwait(false);
            return OperationResult.Ok("Agent is reachable.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    private async Task<T> GetAsync<T>(Computer computer, string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var response = await SendAsync(computer, HttpMethod.Get, path, null, timeout, idempotent: true, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options, cancellationToken).ConfigureAwait(false)
               ?? throw new AgentTransportException("The agent returned an empty response.");
    }

    private async Task<HttpResponseMessage> SendAsync(Computer computer, HttpMethod method, string path, object? body,
        TimeSpan timeout, bool idempotent, CancellationToken cancellationToken)
    {
        var client = GetOrCreateClient(computer);
        Exception? lastError = null;
        var attempts = idempotent ? 2 : 1;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                using var request = new HttpRequestMessage(method, path);
                if (body is not null)
                {
                    request.Content = JsonContent.Create(body, options: JsonDefaults.Options);
                }

                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                var payload = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                var message = TryReadError(payload) ?? $"The agent returned HTTP {(int)response.StatusCode}.";
                response.Dispose();
                throw new AgentTransportException(message, response.StatusCode);
            }
            catch (AgentTransportException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new AgentTransportException("The agent did not respond before the request timed out.", null, ex);
            }

            if (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogWarning(lastError, "Agent request {Method} {Path} on {Computer} failed", method, path, computer.Name);
        throw lastError switch
        {
            AgentTransportException ate => ate,
            _ => new AgentTransportException(
                $"The agent on {computer.Name} could not be reached at {computer.IpAddress}:{computer.Agent?.ListeningPort}. {lastError?.Message}",
                null, lastError)
        };
    }

    private HttpClient GetOrCreateClient(Computer computer)
    {
        if (computer.Agent is null || computer.Agent.State != AgentState.Active)
        {
            throw new AgentTransportException("The RemoteDesk Agent is not enrolled and approved for this computer.");
        }

        if (string.IsNullOrWhiteSpace(computer.IpAddress))
        {
            throw new AgentTransportException("No LAN address is recorded for this computer.");
        }

        if (!NetworkAddressGuard.TryParseLanAddress(computer.IpAddress, out _, out var ipError))
        {
            throw new AgentTransportException(ipError);
        }

        if (string.IsNullOrWhiteSpace(computer.Agent.CertificateThumbprint))
        {
            throw new AgentTransportException("The agent has no approved certificate.");
        }

        var key = $"{computer.Agent.Id}|{computer.Agent.CertificateThumbprint}|{computer.IpAddress}|{computer.Agent.ListeningPort}";
        if (_clients.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (_clients.Count > 200)
        {
            foreach (var stale in _clients.ToArray().Take(50))
            {
                if (_clients.TryRemove(stale.Key, out var client))
                {
                    client.Dispose();
                }
            }
        }

        var expectedThumbprint = computer.Agent.CertificateThumbprint;
        var ca = _certificates.GetCaCertificate();
        var hubClient = _certificates.GetClientCertificate();
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 8,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificates = new X509CertificateCollection { hubClient },
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null)
                    {
                        return false;
                    }

                    try
                    {
                        using var presented = new X509Certificate2(certificate);
                        if (!string.Equals(presented.Thumbprint, expectedThumbprint, StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        return _certificates.ValidateCertificate(presented, requireServerAuthentication: true);
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        };

        var http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri($"https://{computer.IpAddress}:{computer.Agent.ListeningPort}/", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _clients[key] = http;
        return http;
    }

    private static string? TryReadError(string payload)
    {
        try
        {
            var error = JsonDefaults.Deserialize<ApiErrorDto>(payload);
            return error?.Message;
        }
        catch
        {
            return string.IsNullOrWhiteSpace(payload) ? null : payload.Length > 500 ? payload[..500] : payload;
        }
    }

    private static async Task<OperationResult> ReadOperationResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return OperationResult.Ok();
        }

        try
        {
            var result = JsonDefaults.Deserialize<OperationResult>(text);
            if (result is not null)
            {
                return result;
            }
        }
        catch
        {
            // Fall through to plain text.
        }

        return OperationResult.Ok(text);
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
    }
}
