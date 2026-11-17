using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Networking.Security;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.IntegrationTests;

/// <summary>
/// Starts the real Kestrel hub on a loopback port and exercises it over HTTPS with and without the
/// console / agent certificates.
/// </summary>
public sealed class HubHttpTests
{
    private static async Task<HttpClient> CreateClientAsync(TestHub hub, X509Certificate2? certificate)
    {
        var ca = hub.Get<ICertificateAuthority>();
        var expected = ca.GetServerCertificateThumbprint();
        var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            ServerCertificateCustomValidationCallback = (_, serverCert, _, _) =>
                serverCert is not null && string.Equals(serverCert.Thumbprint, expected, StringComparison.OrdinalIgnoreCase),
            SslProtocols = SslProtocols.Tls13
        };

        if (certificate is not null)
        {
            handler.ClientCertificates.Add(certificate);
        }

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{hub.Get<IHubServer>().BoundPort}"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        await Task.CompletedTask;
        return client;
    }

    private static async Task StartHubAsync(TestHub hub)
    {
        var settings = hub.Get<ISettingsService>();
        await settings.SetAsync(SettingsKeys.HubPort, "0", "Network");
        await settings.SetAsync(SettingsKeys.BindAddress, "127.0.0.1", "Network");
        await hub.Get<ICertificateAuthority>().EnsureCreatedAsync();
        await hub.Get<IHubServer>().StartAsync();
    }

    private static async Task<X509Certificate2> CreateAgentClientCertificateAsync(TestHub hub, string machineName, Guid agentId)
    {
        var (csrPem, _, key) = TestCertificates.CreateAgentCsr(machineName);
        var certPem = await hub.Get<ICertificateAuthority>().SignAgentCertificateAsync(csrPem, machineName, agentId);
        return TestCertificates.AttachPrivateKey(certPem, key);
    }

    [Fact]
    public async Task HealthEndpoint_IsAnonymous()
    {
        await using var hub = await TestHub.CreateAsync();
        await StartHubAsync(hub);
        using var client = await CreateClientAsync(hub, null);

        var response = await client.GetAsync("/api/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("RemoteDesk", body, StringComparison.OrdinalIgnoreCase);
        await hub.Get<IHubServer>().StopAsync();
    }

    [Fact]
    public async Task HubInfo_RequiresClientCertificate()
    {
        await using var hub = await TestHub.CreateAsync();
        await StartHubAsync(hub);
        using var client = await CreateClientAsync(hub, null);

        var response = await client.GetAsync("/api/hub/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await hub.Get<IHubServer>().StopAsync();
    }

    [Fact]
    public async Task HubInfo_AcceptsConsoleCertificate()
    {
        await using var hub = await TestHub.CreateAsync();
        await StartHubAsync(hub);
        using var console = hub.Get<ICertificateAuthority>().GetClientCertificate();
        using var client = await CreateClientAsync(hub, console);

        var response = await client.GetAsync("/api/hub/info");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("caCertificatePem", body);
        Assert.Contains("BEGIN CERTIFICATE", body);
        await hub.Get<IHubServer>().StopAsync();
    }

    [Fact]
    public async Task HubInfo_RejectsAgentCertificate()
    {
        await using var hub = await TestHub.CreateAsync();
        await StartHubAsync(hub);
        using var agentCert = await CreateAgentClientCertificateAsync(hub, "AGENT-HTTP", Guid.NewGuid());
        using var agentClient = await CreateClientAsync(hub, agentCert);

        var response = await agentClient.GetAsync("/api/hub/info");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await hub.Get<IHubServer>().StopAsync();
    }

    [Fact]
    public async Task AgentHeartbeat_RequiresCertificate()
    {
        await using var hub = await TestHub.CreateAsync();
        await StartHubAsync(hub);
        using var client = await CreateClientAsync(hub, null);

        var response = await client.PostAsJsonAsync("/api/agent/heartbeat", new HeartbeatRequestDto
        {
            AgentId = Guid.NewGuid(),
            ComputerId = Guid.NewGuid(),
            MachineName = "NO-CERT",
            AgentVersion = "1.0.0",
            Nonce = "n"
        }, JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await hub.Get<IHubServer>().StopAsync();
    }

    [Fact]
    public async Task AgentHeartbeat_WithUnregisteredCertificate_IsForbidden()
    {
        await using var hub = await TestHub.CreateAsync();
        await StartHubAsync(hub);
        using var agentCert = await CreateAgentClientCertificateAsync(hub, "UNREGISTERED", Guid.NewGuid());
        using var client = await CreateClientAsync(hub, agentCert);

        var response = await client.PostAsJsonAsync("/api/agent/heartbeat", new HeartbeatRequestDto
        {
            AgentId = Guid.NewGuid(),
            ComputerId = Guid.NewGuid(),
            MachineName = "UNREGISTERED",
            AgentVersion = "1.0.0",
            Nonce = "n"
        }, JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await hub.Get<IHubServer>().StopAsync();
    }

    [Fact]
    public async Task EnrollmentRequest_WithInvalidToken_ReturnsBadRequest()
    {
        await using var hub = await TestHub.CreateAsync();
        await StartHubAsync(hub);
        using var client = await CreateClientAsync(hub, null);
        var (csrPem, publicKeyPem, key) = TestCertificates.CreateAgentCsr("HTTP-PC");
        using var _ = key;

        var response = await client.PostAsJsonAsync("/api/enrollment/request", new EnrollmentRequestDto
        {
            Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            MachineName = "HTTP-PC",
            AgentVersion = "1.0.0",
            OperatingSystem = "Windows 11",
            PublicKeyPem = publicKeyPem,
            CsrPem = csrPem,
            Nonce = "n",
            InstallationId = Guid.NewGuid()
        }, JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await hub.Get<IHubServer>().StopAsync();
    }

    [Fact]
    public async Task FullEnrollmentAndHeartbeat_OverHttps()
    {
        await using var hub = await TestHub.CreateAsync();
        await StartHubAsync(hub);
        using var client = await CreateClientAsync(hub, null);
        var enrollment = hub.Get<IEnrollmentService>();
        var (_, token, _) = await enrollment.CreateTokenAsync(TestHub.AdminUserName, "http test", null, autoApprove: true);
        var (csrPem, publicKeyPem, key) = TestCertificates.CreateAgentCsr("HTTP-AGENT");
        using var _ = key;

        var request = new EnrollmentRequestDto
        {
            Token = token,
            MachineName = "HTTP-AGENT",
            AgentVersion = "1.0.0",
            OperatingSystem = "Windows 11 Pro",
            PublicKeyPem = publicKeyPem,
            CsrPem = csrPem,
            Nonce = "nonce-http",
            InstallationId = Guid.NewGuid(),
            ManagementPort = 7444
        };

        var submitted = await client.PostAsJsonAsync("/api/enrollment/request", request, JsonDefaults.Options);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var submittedBody = await submitted.Content.ReadFromJsonAsync<EnrollmentResponseDto>(JsonDefaults.Options);
        Assert.NotNull(submittedBody);

        var status = await client.GetFromJsonAsync<EnrollmentStatusDto>(
            $"/api/enrollment/status/{submittedBody!.EnrollmentId:D}?token={Uri.EscapeDataString(token)}",
            JsonDefaults.Options);
        Assert.NotNull(status);
        Assert.Equal("Approved", status!.Status);
        Assert.False(string.IsNullOrWhiteSpace(status.AgentCertificatePem));

        using var agentCert = TestCertificates.AttachPrivateKey(status.AgentCertificatePem!, key);
        using var agentClient = await CreateClientAsync(hub, agentCert);

        var heartbeat = await agentClient.PostAsJsonAsync("/api/agent/heartbeat", new HeartbeatRequestDto
        {
            AgentId = status.AgentId!.Value,
            ComputerId = status.ComputerId!.Value,
            MachineName = "HTTP-AGENT",
            AgentVersion = "1.0.0",
            ListeningPort = 7444,
            Nonce = "hb-1"
        }, JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        var heartbeatBody = await heartbeat.Content.ReadFromJsonAsync<HeartbeatResponseDto>(JsonDefaults.Options);
        Assert.NotNull(heartbeatBody);
        Assert.True(heartbeatBody!.Approved);

        await hub.Get<IHubServer>().StopAsync();
    }

    [Fact]
    public async Task AgentCertificateRenewal_ReplacesThumbprintAndRejectsOldCertificate()
    {
        await using var hub = await TestHub.CreateAsync();
        await StartHubAsync(hub);
        var ca = hub.Get<ICertificateAuthority>();
        var (oldCsr, _, oldKey) = TestCertificates.CreateAgentCsr("RENEW-AGENT");
        using var oldKeyScope = oldKey;
        var oldPem = await ca.SignAgentCertificateAsync(oldCsr, "RENEW-AGENT", Guid.NewGuid());
        using var oldCertificate = TestCertificates.AttachPrivateKey(oldPem, oldKey);
        var (computer, agent) = await hub.CreateApprovedComputerAsync(
            "RENEW-AGENT", "192.168.1.30", oldCertificate.Thumbprint);

        using var oldClient = await CreateClientAsync(hub, oldCertificate);
        var (newCsr, _, newKey) = TestCertificates.CreateAgentCsr("RENEW-AGENT");
        using var newKeyScope = newKey;

        var renewal = await oldClient.PostAsJsonAsync("/api/agent/renew", new AgentCertificateRenewalRequestDto
        {
            AgentId = agent.Id,
            MachineName = "RENEW-AGENT",
            CsrPem = newCsr,
            Nonce = "renew-1",
            AgentVersion = "1.0.1"
        }, JsonDefaults.Options);

        var body = await renewal.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, renewal.StatusCode);
        var renewed = JsonSerializer.Deserialize<AgentCertificateRenewalResponseDto>(body, JsonDefaults.Options);
        Assert.NotNull(renewed);
        Assert.False(string.IsNullOrWhiteSpace(renewed!.AgentCertificatePem));

        using var newCertificate = TestCertificates.AttachPrivateKey(renewed.AgentCertificatePem, newKey);
        Assert.NotEqual(oldCertificate.Thumbprint, newCertificate.Thumbprint);

        using var newClient = await CreateClientAsync(hub, newCertificate);
        var newHeartbeat = await newClient.PostAsJsonAsync("/api/agent/heartbeat", new HeartbeatRequestDto
        {
            AgentId = agent.Id,
            ComputerId = computer.Id,
            MachineName = "RENEW-AGENT",
            AgentVersion = "1.0.1",
            Nonce = "hb-after-renew"
        }, JsonDefaults.Options);
        Assert.Equal(HttpStatusCode.OK, newHeartbeat.StatusCode);

        var oldHeartbeat = await oldClient.PostAsJsonAsync("/api/agent/heartbeat", new HeartbeatRequestDto
        {
            AgentId = agent.Id,
            ComputerId = computer.Id,
            MachineName = "RENEW-AGENT",
            AgentVersion = "1.0.1",
            Nonce = "hb-old-cert"
        }, JsonDefaults.Options);
        Assert.Equal(HttpStatusCode.Forbidden, oldHeartbeat.StatusCode);

        await using var db = await hub.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<RemoteDeskHub.Infrastructure.Data.RemoteDeskDbContext>>()
            .CreateDbContextAsync();
        var stored = await db.Agents.FindAsync(agent.Id);
        Assert.Equal(newCertificate.Thumbprint, stored!.CertificateThumbprint);
        Assert.Equal("1.0.1", stored.Version);

        await hub.Get<IHubServer>().StopAsync();
    }

    [Fact]
    public async Task AgentCertificateRenewal_WithMismatchedMachineName_IsRejected()
    {
        await using var hub = await TestHub.CreateAsync();
        await StartHubAsync(hub);
        var ca = hub.Get<ICertificateAuthority>();
        var (oldCsr, _, oldKey) = TestCertificates.CreateAgentCsr("RENEW-MISMATCH");
        using var oldKeyScope = oldKey;
        var oldPem = await ca.SignAgentCertificateAsync(oldCsr, "RENEW-MISMATCH", Guid.NewGuid());
        using var oldCertificate = TestCertificates.AttachPrivateKey(oldPem, oldKey);
        var (_, agent) = await hub.CreateApprovedComputerAsync(
            "RENEW-MISMATCH", "192.168.1.31", oldCertificate.Thumbprint);
        using var client = await CreateClientAsync(hub, oldCertificate);
        var (newCsr, _, newKey) = TestCertificates.CreateAgentCsr("RENEW-MISMATCH");
        using var newKeyScope = newKey;

        var renewal = await client.PostAsJsonAsync("/api/agent/renew", new AgentCertificateRenewalRequestDto
        {
            AgentId = agent.Id,
            MachineName = "OTHER-MACHINE",
            CsrPem = newCsr,
            Nonce = "renew-mismatch"
        }, JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.BadRequest, renewal.StatusCode);
        await hub.Get<IHubServer>().StopAsync();
    }

    [Fact]
    public async Task StoppingHub_StopsListener()
    {
        await using var hub = await TestHub.CreateAsync();
        await StartHubAsync(hub);
        var server = hub.Get<IHubServer>();
        Assert.True(server.IsRunning);

        await server.StopAsync();

        Assert.False(server.IsRunning);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var client = await CreateClientAsync(hub, null);
            await client.GetAsync("/api/health");
        });
    }
}
