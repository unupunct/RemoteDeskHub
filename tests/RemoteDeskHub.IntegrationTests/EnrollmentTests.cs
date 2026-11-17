using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Infrastructure.Services;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.IntegrationTests;

public sealed class EnrollmentTests
{
    private static EnrollmentRequestDto BuildRequest(string token, string machineName, Guid installationId, RSAKey key)
    {
        return new EnrollmentRequestDto
        {
            Token = token,
            MachineName = machineName,
            AgentVersion = "1.0.0",
            OperatingSystem = "Windows 11 Pro",
            PublicKeyPem = key.PublicKeyPem,
            CsrPem = key.CsrPem,
            Nonce = "nonce-" + Guid.NewGuid().ToString("N"),
            InstallationId = installationId,
            ManagementPort = 7444,
            RequestedUtc = DateTimeOffset.UtcNow
        };
    }

    [Fact]
    public async Task FullEnrollment_RequiresApproval_ThenIssuesCertificate()
    {
        await using var hub = await TestHub.CreateAsync();
        var enrollment = hub.Get<IEnrollmentService>();

        var (id, token, _) = await enrollment.CreateTokenAsync(TestHub.AdminUserName, "test token", TimeSpan.FromMinutes(30), autoApprove: false);
        using var key = new RSAKey();
        var request = BuildRequest(token, "NEW-PC-01", Guid.NewGuid(), key);

        var submitted = await enrollment.HandleEnrollmentRequestAsync(request, "192.168.1.22");
        Assert.True(submitted.Success, submitted.Error);
        Assert.Equal(id, submitted.Value!.EnrollmentId);
        Assert.Equal("Pending", submitted.Value.Status);

        var pending = await enrollment.GetPendingAgentsAsync();
        Assert.Contains(pending, p => p.Name == "NEW-PC-01");

        var statusBefore = await enrollment.GetEnrollmentStatusAsync(id, token);
        Assert.Equal("Pending", statusBefore.Status);
        Assert.Null(statusBefore.AgentCertificatePem);

        var approved = await enrollment.ApproveAsync(TestHub.AdminUserName, id);
        Assert.True(approved.Success, approved.Error);

        var status = await enrollment.GetEnrollmentStatusAsync(id, token);
        Assert.Equal("Approved", status.Status);
        Assert.False(string.IsNullOrWhiteSpace(status.AgentCertificatePem));
        Assert.Contains("BEGIN CERTIFICATE", status.CaCertificatePem);
        Assert.Contains("BEGIN CERTIFICATE", status.HubClientCertificatePem);
        Assert.NotEqual(Guid.Empty, status.AgentId);
        Assert.NotEqual(Guid.Empty, status.ComputerId);

        Assert.Empty(await enrollment.GetPendingAgentsAsync());
    }

    [Fact]
    public async Task PairingPackage_AutoApprove_IssuesCertificateImmediately()
    {
        await using var hub = await TestHub.CreateAsync();
        var enrollment = hub.Get<IEnrollmentService>();

        var package = await enrollment.CreatePairingPackageAsync(
            TestHub.AdminUserName, TimeSpan.FromHours(2), "https://127.0.0.1:7443", autoApprove: true);

        using var key = new RSAKey();
        var submitted = await enrollment.HandleEnrollmentRequestAsync(
            BuildRequest(package.EnrollmentToken, "AUTO-PC", Guid.NewGuid(), key), "192.168.1.30");

        Assert.True(submitted.Success, submitted.Error);
        Assert.Equal("Approved", submitted.Value!.Status);

        var status = await enrollment.GetEnrollmentStatusAsync(submitted.Value.EnrollmentId, package.EnrollmentToken);
        Assert.Equal("Approved", status.Status);
        Assert.False(string.IsNullOrWhiteSpace(status.AgentCertificatePem));
    }

    [Fact]
    public async Task Enrollment_WithUnknownToken_IsRejected()
    {
        await using var hub = await TestHub.CreateAsync();
        using var key = new RSAKey();

        var result = await hub.Get<IEnrollmentService>()
            .HandleEnrollmentRequestAsync(BuildRequest("this-token-is-long-enough-but-unknown", "PC-X", Guid.NewGuid(), key), "192.168.1.5");

        Assert.False(result.Success);
        Assert.Contains("not valid", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enrollment_FromPublicAddress_IsRejected()
    {
        await using var hub = await TestHub.CreateAsync();
        var enrollment = hub.Get<IEnrollmentService>();
        var (_, token, _) = await enrollment.CreateTokenAsync(TestHub.AdminUserName, null, null, false);
        using var key = new RSAKey();

        var result = await enrollment.HandleEnrollmentRequestAsync(BuildRequest(token, "PC-X", Guid.NewGuid(), key), "8.8.8.8");

        Assert.False(result.Success);
        Assert.Contains("private LAN", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExpiredToken_IsRejected()
    {
        await using var hub = await TestHub.CreateAsync();
        var enrollment = hub.Get<IEnrollmentService>();
        var (_, token, _) = await enrollment.CreateTokenAsync(TestHub.AdminUserName, null, TimeSpan.FromMinutes(5), false);
        hub.Clock.Advance(TimeSpan.FromMinutes(6));
        using var key = new RSAKey();

        var result = await enrollment.HandleEnrollmentRequestAsync(BuildRequest(token, "PC-X", Guid.NewGuid(), key), "192.168.1.5");

        Assert.False(result.Success);
        Assert.Contains("expired", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TokenIsSingleUse_ButIdempotentForSameInstallation()
    {
        await using var hub = await TestHub.CreateAsync();
        var enrollment = hub.Get<IEnrollmentService>();
        var (_, token, _) = await enrollment.CreateTokenAsync(TestHub.AdminUserName, null, null, false);
        using var key = new RSAKey();
        var installationId = Guid.NewGuid();
        var request = BuildRequest(token, "PC-RETRY", installationId, key);

        var first = await enrollment.HandleEnrollmentRequestAsync(request, "192.168.1.5");
        var retry = await enrollment.HandleEnrollmentRequestAsync(request, "192.168.1.5");
        var other = await enrollment.HandleEnrollmentRequestAsync(BuildRequest(token, "PC-OTHER", Guid.NewGuid(), key), "192.168.1.6");

        Assert.True(first.Success);
        Assert.True(retry.Success);
        Assert.Contains("already registered", retry.Value!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(other.Success);
        Assert.Contains("already been used", other.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectEnrollment_PreventsApproval()
    {
        await using var hub = await TestHub.CreateAsync();
        var enrollment = hub.Get<IEnrollmentService>();
        var (id, token, _) = await enrollment.CreateTokenAsync(TestHub.AdminUserName, null, null, false);
        using var key = new RSAKey();
        await enrollment.HandleEnrollmentRequestAsync(BuildRequest(token, "BAD-PC", Guid.NewGuid(), key), "192.168.1.5");

        var rejected = await enrollment.RejectAsync(TestHub.AdminUserName, id, "Not authorised");
        Assert.True(rejected.Success, rejected.Error);

        var status = await enrollment.GetEnrollmentStatusAsync(id, token);
        Assert.Equal("Rejected", status.Status);
        Assert.Contains("Not authorised", status.Message);

        var approve = await enrollment.ApproveAsync(TestHub.AdminUserName, id);
        Assert.False(approve.Success);
    }

    [Fact]
    public async Task RevokeAgent_BlocksHeartbeat()
    {
        await using var hub = await TestHub.CreateAsync();
        var enrollment = hub.Get<IEnrollmentService>();
        var lifecycle = hub.Get<IAgentLifecycleService>();
        var (id, token, _) = await enrollment.CreateTokenAsync(TestHub.AdminUserName, null, null, false);
        using var key = new RSAKey();
        await enrollment.HandleEnrollmentRequestAsync(BuildRequest(token, "REVOKE-PC", Guid.NewGuid(), key), "192.168.1.5");
        Assert.True((await enrollment.ApproveAsync(TestHub.AdminUserName, id)).Success);

        Guid agentId;
        Guid computerId;
        string thumbprint;
        await using (var db = await hub.Get<IDbContextFactory<RemoteDeskDbContext>>().CreateDbContextAsync())
        {
            var agent = await db.Agents.SingleAsync(a => a.MachineName == "REVOKE-PC");
            agentId = agent.Id;
            computerId = agent.ComputerId;
            thumbprint = agent.CertificateThumbprint!;
        }

        var heartbeat = new HeartbeatRequestDto
        {
            AgentId = agentId,
            ComputerId = computerId,
            MachineName = "REVOKE-PC",
            AgentVersion = "1.0.0",
            ListeningPort = 7444,
            Nonce = "n1"
        };
        var before = await lifecycle.ProcessHeartbeatAsync(heartbeat, thumbprint, "192.168.1.5");
        Assert.True(before.Approved);

        Assert.True((await enrollment.RevokeAgentAsync(TestHub.AdminUserName, agentId, "Lost device")).Success);

        var after = await lifecycle.ProcessHeartbeatAsync(heartbeat, thumbprint, "192.168.1.5");
        Assert.False(after.Approved);
        Assert.True(after.Revoked);
    }

    [Fact]
    public async Task CreateToken_RequiresEnrollAgentsPermission()
    {
        await using var hub = await TestHub.CreateAsync();
        var viewerSession = new RemoteDeskHub.Core.Security.SessionContext();
        viewerSession.SignIn(new RemoteDeskHub.Core.Security.SessionUser(Guid.NewGuid(), "viewer", UserRole.Viewer, DateTime.UtcNow));

        var scoped = new EnrollmentService(
            hub.Get<IDbContextFactory<RemoteDeskDbContext>>(),
            hub.Get<ICertificateAuthority>(),
            hub.Get<ISettingsService>(),
            viewerSession,
            hub.Get<IAppEvents>(),
            hub.Get<IAuditService>(),
            hub.Clock);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            scoped.CreateTokenAsync("viewer", null, null, false));
    }

    [Fact]
    public async Task MalformedCsr_IsRejected()
    {
        await using var hub = await TestHub.CreateAsync();
        var enrollment = hub.Get<IEnrollmentService>();
        var (_, token, _) = await enrollment.CreateTokenAsync(TestHub.AdminUserName, null, null, false);

        var result = await enrollment.HandleEnrollmentRequestAsync(new EnrollmentRequestDto
        {
            Token = token,
            MachineName = "PC-BAD-CSR",
            AgentVersion = "1.0.0",
            OperatingSystem = "Windows",
            PublicKeyPem = "not a key",
            CsrPem = "not a csr",
            Nonce = "n",
            InstallationId = Guid.NewGuid()
        }, "192.168.1.5");

        Assert.False(result.Success);
        Assert.Contains("certificate request", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AutoApproveToken_ApprovesImmediately()
    {
        await using var hub = await TestHub.CreateAsync();
        var enrollment = hub.Get<IEnrollmentService>();
        var (_, token, _) = await enrollment.CreateTokenAsync(TestHub.AdminUserName, null, null, autoApprove: true);
        using var key = new RSAKey();

        var result = await enrollment.HandleEnrollmentRequestAsync(BuildRequest(token, "AUTO-PC", Guid.NewGuid(), key), "192.168.1.5");

        Assert.True(result.Success, result.Error);
        Assert.Equal("Approved", result.Value!.Status);

        await using var db = await hub.Get<IDbContextFactory<RemoteDeskDbContext>>().CreateDbContextAsync();
        var agent = await db.Agents.SingleAsync(a => a.MachineName == "AUTO-PC");
        Assert.Equal(AgentState.Active, agent.State);
        Assert.False(string.IsNullOrWhiteSpace(agent.CertificateThumbprint));
    }

    internal sealed class RSAKey : IDisposable
    {
        private readonly System.Security.Cryptography.RSA _rsa;

        public RSAKey()
        {
            var material = TestCertificates.CreateAgentCsr("TEST-" + Guid.NewGuid().ToString("N")[..6]);
            CsrPem = material.CsrPem;
            PublicKeyPem = material.PublicKeyPem;
            _rsa = material.Key;
        }

        public string CsrPem { get; }
        public string PublicKeyPem { get; }

        public void Dispose() => _rsa.Dispose();
    }
}
