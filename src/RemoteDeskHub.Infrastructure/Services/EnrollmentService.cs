using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;
using RemoteDeskHub.Shared.Security;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class EnrollmentService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    ICertificateAuthority certificateAuthority,
    ISettingsService settings,
    ISessionContext session,
    IAppEvents events,
    IAuditService audit,
    IClock clock) : IEnrollmentService
{
    public async Task<(Guid Id, string Token, DateTime ExpiresUtc)> CreateTokenAsync(string actor, string? note, TimeSpan? lifetime, bool autoApprove, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.EnrollAgents, actor);
        var token = PasswordHasher.GenerateToken();
        var expires = clock.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(30));
        if (expires <= clock.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Enrollment token lifetime must be positive.");
        }

        var enrollment = new AgentEnrollment
        {
            TokenHash = PasswordHasher.HashToken(token),
            TokenPreview = PasswordHasher.MaskToken(token),
            CreatedBy = actor,
            CreatedUtc = clock.UtcNow,
            ExpiresUtc = expires,
            Status = EnrollmentState.Pending,
            AutoApprove = autoApprove,
            Note = note
        };

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.AgentEnrollments.Add(enrollment);
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, AuditOperations.EnrollmentTokenCreated,
            details: $"Enrollment token {enrollment.TokenPreview} created (auto-approve: {autoApprove}).",
            cancellationToken: cancellationToken);
        return (enrollment.Id, token, expires);
    }

    public async Task<IReadOnlyList<AgentEnrollment>> GetEnrollmentsAsync(CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.EnrollAgents);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.AgentEnrollments.AsNoTracking().OrderByDescending(e => e.CreatedUtc).Take(500).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ComputerSummary>> GetPendingAgentsAsync(CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.EnrollAgents);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var computers = await db.Computers.AsNoTracking().Include(c => c.Agent).Include(c => c.Group)
            .Where(c => c.Agent != null && c.Agent.State == AgentState.PendingApproval)
            .ToListAsync(cancellationToken);
        return computers.Select(c => new ComputerSummary
        {
            Id = c.Id,
            Name = c.Name,
            IpAddress = c.IpAddress,
            OperatingSystem = c.OperatingSystem,
            Status = ComputerStatus.PendingApproval,
            IsRegistered = true,
            IsManaged = false,
            AgentVersion = c.Agent?.Version,
            GroupName = c.Group?.Name,
            LastSeenUtc = c.Agent?.LastSeenUtc,
            IsTrusted = false
        }).ToList();
    }

    public async Task<OperationResult<EnrollmentResponseDto>> HandleEnrollmentRequestAsync(EnrollmentRequestDto request, string remoteIp, CancellationToken cancellationToken = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Token) || request.Token.Length < 20)
        {
            return OperationResult<EnrollmentResponseDto>.Fail("A valid enrollment token is required.");
        }

        if (!string.IsNullOrWhiteSpace(remoteIp) && !IsAllowedEnrollmentIp(remoteIp))
        {
            return OperationResult<EnrollmentResponseDto>.Fail("Enrollment is only permitted from a private LAN address.");
        }

        var machineName = SanitizeMachineName(request.MachineName);
        if (machineName is null)
        {
            return OperationResult<EnrollmentResponseDto>.Fail("A valid computer name is required.");
        }

        string csrPublicKey;
        try
        {
            var csr = CertificateRequest.LoadSigningRequestPem(request.CsrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default);
            csrPublicKey = Convert.ToBase64String(csr.PublicKey.ExportSubjectPublicKeyInfo());
            if (!csrPublicKey.Equals(Convert.ToBase64String(ParsePublicKey(request.PublicKeyPem).ExportSubjectPublicKeyInfo()), StringComparison.Ordinal))
            {
                return OperationResult<EnrollmentResponseDto>.Fail("The enrollment public key does not match the certificate request.");
            }
        }
        catch (Exception ex)
        {
            return OperationResult<EnrollmentResponseDto>.Fail($"The certificate request is invalid: {ex.Message}");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var tokenHash = PasswordHasher.HashToken(request.Token);
        var enrollment = await db.AgentEnrollments.FirstOrDefaultAsync(e => e.TokenHash == tokenHash, cancellationToken);
        if (enrollment is null)
        {
            await audit.LogAsync("anonymous", "Agent.EnrollmentRejected", result: "Failed",
                error: "Invalid enrollment token.", ipAddress: remoteIp, cancellationToken: cancellationToken);
            return OperationResult<EnrollmentResponseDto>.Fail("The enrollment token is not valid.");
        }

        if (enrollment.Status is EnrollmentState.Rejected or EnrollmentState.Cancelled)
        {
            return OperationResult<EnrollmentResponseDto>.Fail("This enrollment request was rejected. Contact the administrator for a new token.");
        }

        if (enrollment.ExpiresUtc < clock.UtcNow)
        {
            enrollment.Status = EnrollmentState.Expired;
            await db.SaveChangesAsync(cancellationToken);
            return OperationResult<EnrollmentResponseDto>.Fail("The enrollment token has expired.");
        }

        if (enrollment.ConsumedUtc.HasValue)
        {
            // Idempotent retry for the same installation only.
            if (enrollment.InstallationId.HasValue && enrollment.InstallationId == request.InstallationId &&
                enrollment.Status is EnrollmentState.Pending or EnrollmentState.Approved)
            {
                return OperationResult<EnrollmentResponseDto>.Ok(new EnrollmentResponseDto(
                    enrollment.Id, enrollment.Status.ToString(), "Enrollment already registered.", 5));
            }

            return OperationResult<EnrollmentResponseDto>.Fail("The enrollment token has already been used.");
        }

        var now = clock.UtcNow;
        var normalizedIp = NormalizeIp(remoteIp);
        var machineId = NormalizeMachineId(request.MachineId);
        var existingAgent = await FindExistingAgentAsync(db, machineId, request.InstallationId, cancellationToken);
        var preservedIdentity = existingAgent?.Computer is not null;

        Computer computer;
        Agent agent;
        if (existingAgent?.Computer is not null)
        {
            // Reinstalling or re-enrolling an agent must not create a second computer. A stable
            // machine ID (or the persisted installation ID) keeps the computer, its history and
            // its group membership even when the IP address changed.
            computer = existingAgent.Computer;
            agent = existingAgent;
            computer.Name = machineName;
            computer.IpAddress = normalizedIp;
            computer.OperatingSystem = request.OperatingSystem;
            computer.AgentVersion = request.AgentVersion;
            computer.MachineId = machineId ?? computer.MachineId;
            computer.Status = ComputerStatus.PendingApproval;
            computer.IsRegistered = true;
            computer.LastSeenUtc = now;
            computer.UpdatedUtc = now;

            agent.MachineName = machineName;
            agent.MachineId = machineId ?? agent.MachineId;
            agent.State = AgentState.PendingApproval;
            agent.PublicKeyPem = request.PublicKeyPem;
            agent.CertificatePem = null;
            agent.CertificateThumbprint = null;
            agent.Version = request.AgentVersion;
            agent.OperatingSystem = request.OperatingSystem;
            agent.ListeningPort = request.ManagementPort is > 0 and < 65536
                ? request.ManagementPort
                : HubConstants.DefaultAgentPort;
            agent.InstallationId = request.InstallationId == Guid.Empty ? agent.InstallationId : request.InstallationId;
            agent.EnrolledUtc = now;
            agent.ApprovedUtc = null;
            agent.ApprovedBy = null;
            agent.RevokedUtc = null;
            agent.RevocationReason = null;
            agent.LastSeenUtc = now;
            agent.LastKnownIp = normalizedIp;
            agent.HealthStatus = "Awaiting approval (identity preserved)";
        }
        else
        {
            computer = new Computer
            {
                Name = machineName,
                IpAddress = normalizedIp,
                OperatingSystem = request.OperatingSystem,
                AgentVersion = request.AgentVersion,
                MachineId = machineId,
                Status = ComputerStatus.PendingApproval,
                IsRegistered = true,
                IsManaged = false,
                IsTrusted = false,
                Source = "Agent",
                FirstSeenUtc = now,
                LastSeenUtc = null,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            agent = new Agent
            {
                ComputerId = computer.Id,
                MachineName = machineName,
                MachineId = machineId,
                State = AgentState.PendingApproval,
                PublicKeyPem = request.PublicKeyPem,
                Version = request.AgentVersion,
                OperatingSystem = request.OperatingSystem,
                ListeningPort = request.ManagementPort is > 0 and < 65536 ? request.ManagementPort : HubConstants.DefaultAgentPort,
                InstallationId = request.InstallationId == Guid.Empty ? null : request.InstallationId,
                EnrolledUtc = now,
                LastSeenUtc = now,
                LastKnownIp = normalizedIp,
                HealthStatus = "Awaiting approval"
            };
            computer.Agent = agent;
            db.Computers.Add(computer);
            db.Agents.Add(agent);
        }

        enrollment.ConsumedUtc = now;
        enrollment.RequestedIp = normalizedIp;
        enrollment.PublicKeyPem = request.PublicKeyPem;
        enrollment.CsrPem = request.CsrPem;
        enrollment.ComputerName = machineName;
        enrollment.MachineId = machineId;
        enrollment.AgentId = agent.Id;
        enrollment.ComputerId = computer.Id;
        enrollment.Nonce = request.Nonce;
        enrollment.InstallationId = request.InstallationId;
        enrollment.ManagementPort = agent.ListeningPort;

        var autoApprove = enrollment.AutoApprove || await settings.GetAsync(SettingsKeys.AgentAutoApprove, false, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        await audit.LogAsync("anonymous", "Agent.EnrollmentRequested", computer.Name, computer.Id,
            details: preservedIdentity
                ? $"Agent '{machineName}' requested re-enrollment from {normalizedIp}; the existing computer identity was preserved."
                : $"Agent '{machineName}' requested enrollment from {normalizedIp}.",
            ipAddress: normalizedIp, cancellationToken: cancellationToken);

        if (autoApprove)
        {
            var approval = await ApproveCoreAsync(db, enrollment, "auto-enrollment", cancellationToken);
            if (!approval.Success)
            {
                return OperationResult<EnrollmentResponseDto>.Fail(approval.Error ?? "Automatic approval failed.");
            }
        }

        events.RaiseDataChanged();
        var message = autoApprove ? "Enrollment approved." : "Enrollment is awaiting administrator approval.";
        return OperationResult<EnrollmentResponseDto>.Ok(new EnrollmentResponseDto(enrollment.Id, enrollment.Status.ToString(), message, 5));
    }

    public async Task<EnrollmentStatusDto> GetEnrollmentStatusAsync(Guid enrollmentId, string token, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var enrollment = await db.AgentEnrollments.FirstOrDefaultAsync(e => e.Id == enrollmentId, cancellationToken);
        if (enrollment is null || string.IsNullOrWhiteSpace(token) ||
            !string.Equals(enrollment.TokenHash, PasswordHasher.HashToken(token), StringComparison.Ordinal))
        {
            return new EnrollmentStatusDto { Status = "Invalid", Message = "Enrollment request not found or token mismatch." };
        }

        if (enrollment.Status == EnrollmentState.Pending && enrollment.ExpiresUtc < clock.UtcNow)
        {
            enrollment.Status = EnrollmentState.Expired;
            await db.SaveChangesAsync(cancellationToken);
        }

        var status = enrollment.Status switch
        {
            EnrollmentState.Approved => "Approved",
            EnrollmentState.Rejected => "Rejected",
            EnrollmentState.Expired => "Expired",
            EnrollmentState.Cancelled => "Cancelled",
            _ => "Pending"
        };

        return new EnrollmentStatusDto
        {
            Status = status,
            Message = status switch
            {
                "Approved" => "Enrollment approved. The agent certificate is ready.",
                "Pending" => "Waiting for administrator approval.",
                "Rejected" => enrollment.RejectReason ?? "The enrollment request was rejected.",
                "Expired" => "The enrollment request expired.",
                "Cancelled" => "The enrollment request was cancelled.",
                _ => status
            },
            AgentCertificatePem = status == "Approved" ? enrollment.CertificatePem : null,
            CaCertificatePem = status == "Approved" ? certificateAuthority.GetCaCertificatePem() : null,
            HubCertificatePem = status == "Approved" ? certificateAuthority.GetServerCertificate().ExportCertificatePem() : null,
            HubServerCertificateThumbprint = status == "Approved" ? certificateAuthority.GetServerCertificateThumbprint() : null,
            HubClientCertificatePem = status == "Approved" ? certificateAuthority.GetClientCertificatePem() : null,
            AgentId = enrollment.AgentId,
            ComputerId = enrollment.ComputerId,
            ManagementPort = enrollment.ManagementPort
        };
    }

    public async Task<OperationResult> ApproveAsync(string actor, Guid enrollmentId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.EnrollAgents, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var enrollment = await db.AgentEnrollments.FirstOrDefaultAsync(e => e.Id == enrollmentId, cancellationToken);
        if (enrollment is null)
        {
            return OperationResult.Fail("Enrollment request not found.");
        }

        if (enrollment.Status == EnrollmentState.Approved)
        {
            return OperationResult.Ok("Enrollment is already approved.");
        }

        if (enrollment.Status is not EnrollmentState.Pending || enrollment.ConsumedUtc is null)
        {
            return OperationResult.Fail("Only a pending enrollment request that an agent has submitted can be approved.");
        }

        if (enrollment.ExpiresUtc < clock.UtcNow)
        {
            enrollment.Status = EnrollmentState.Expired;
            await db.SaveChangesAsync(cancellationToken);
            return OperationResult.Fail("The enrollment token has expired. Create a new token.");
        }

        var result = await ApproveCoreAsync(db, enrollment, actor, cancellationToken);
        if (result.Success)
        {
            events.RaiseDataChanged();
        }

        return result;
    }

    private async Task<OperationResult> ApproveCoreAsync(RemoteDeskDbContext db, AgentEnrollment enrollment, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(enrollment.CsrPem) || string.IsNullOrWhiteSpace(enrollment.ComputerName))
        {
            return OperationResult.Fail("The enrollment request is incomplete.");
        }

        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == enrollment.AgentId, cancellationToken);
        var computer = await db.Computers.FirstOrDefaultAsync(c => c.Id == enrollment.ComputerId, cancellationToken);
        if (agent is null || computer is null)
        {
            return OperationResult.Fail("The enrollment request has no associated agent record.");
        }

        string certPem;
        try
        {
            certPem = await certificateAuthority.SignAgentCertificateAsync(enrollment.CsrPem, enrollment.ComputerName, agent.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            await audit.LogAsync(actor, AuditOperations.AgentApproved, enrollment.ComputerName, computer.Id,
                result: "Failed", error: ex.Message, cancellationToken: cancellationToken);
            return OperationResult.Fail($"Certificate issuance failed: {ex.Message}");
        }

        var certificate = X509Certificate2.CreateFromPem(certPem);
        var now = clock.UtcNow;
        agent.State = AgentState.Active;
        agent.CertificatePem = certPem;
        agent.CertificateThumbprint = certificate.Thumbprint;
        agent.ApprovedUtc = now;
        agent.ApprovedBy = actor;
        agent.HealthStatus = "Approved; awaiting first heartbeat";

        computer.IsManaged = true;
        computer.IsTrusted = true;
        computer.Status = ComputerStatus.Offline;
        computer.AgentVersion = agent.Version;
        computer.UpdatedUtc = now;

        var defaultGroupId = await settings.GetAsync<string?>(SettingsKeys.DefaultGroupId, null, cancellationToken);
        if (!computer.GroupId.HasValue && Guid.TryParse(defaultGroupId, out var groupId))
        {
            var groupExists = await db.ComputerGroups.AnyAsync(g => g.Id == groupId, cancellationToken);
            if (groupExists)
            {
                computer.GroupId = groupId;
                db.ComputerGroupMemberships.Add(new ComputerGroupMembership { ComputerId = computer.Id, GroupId = groupId, AssignedUtc = now });
            }
        }

        enrollment.Status = EnrollmentState.Approved;
        enrollment.CertificatePem = certPem;
        enrollment.ApprovedBy = actor;
        enrollment.ApprovedUtc = now;
        await db.SaveChangesAsync(cancellationToken);

        await audit.LogAsync(actor, AuditOperations.AgentApproved, computer.Name, computer.Id,
            details: $"Agent certificate issued. Thumbprint {agent.CertificateThumbprint}.",
            cancellationToken: cancellationToken);
        events.RaiseComputerChanged(computer.Id);
        return OperationResult.Ok("Agent approved and certificate issued.");
    }

    public async Task<OperationResult> RejectAsync(string actor, Guid enrollmentId, string? reason, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.EnrollAgents, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var enrollment = await db.AgentEnrollments.FirstOrDefaultAsync(e => e.Id == enrollmentId, cancellationToken);
        if (enrollment is null)
        {
            return OperationResult.Fail("Enrollment request not found.");
        }

        enrollment.Status = EnrollmentState.Rejected;
        enrollment.RejectReason = string.IsNullOrWhiteSpace(reason) ? "Rejected by administrator." : reason.Trim();
        if (enrollment.AgentId.HasValue)
        {
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == enrollment.AgentId, cancellationToken);
            if (agent is not null)
            {
                agent.State = AgentState.Rejected;
            }
        }

        if (enrollment.ComputerId.HasValue)
        {
            var computer = await db.Computers.FirstOrDefaultAsync(c => c.Id == enrollment.ComputerId, cancellationToken);
            if (computer is not null)
            {
                computer.IsManaged = false;
                computer.IsTrusted = false;
                computer.Status = ComputerStatus.Offline;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, AuditOperations.AgentRejected, enrollment.ComputerName, enrollment.ComputerId,
            details: enrollment.RejectReason, cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok("Enrollment rejected.");
    }

    public async Task<OperationResult> RevokeAgentAsync(string actor, Guid agentId, string reason, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.EnrollAgents, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Agents.Include(a => a.Computer).FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null)
        {
            return OperationResult.Fail("Agent not found.");
        }

        agent.State = AgentState.Revoked;
        agent.RevokedUtc = clock.UtcNow;
        agent.RevocationReason = string.IsNullOrWhiteSpace(reason) ? "Revoked by administrator." : reason.Trim();
        agent.HealthStatus = "Revoked";
        if (agent.Computer is not null)
        {
            agent.Computer.IsManaged = false;
            agent.Computer.IsTrusted = false;
            agent.Computer.Status = ComputerStatus.Offline;
            agent.Computer.UpdatedUtc = clock.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, AuditOperations.AgentRevoked, agent.Computer?.Name, agent.ComputerId,
            details: agent.RevocationReason, cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok("Agent revoked. Future management requests from this agent will be rejected.");
    }

    public Task<PairingPackageDto> CreatePairingPackageAsync(string actor, TimeSpan lifetime, string hubUrl, CancellationToken cancellationToken = default)
        => CreatePairingPackageAsync(actor, lifetime, hubUrl, autoApprove: false, cancellationToken);

    public async Task<PairingPackageDto> CreatePairingPackageAsync(string actor, TimeSpan lifetime, string hubUrl, bool autoApprove, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.EnrollAgents, actor);
        if (!Uri.TryCreate(hubUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The hub URL must be an absolute HTTPS URL.", nameof(hubUrl));
        }

        // The console may export a pairing package before the hub listener has ever started.
        await certificateAuthority.EnsureCreatedAsync(cancellationToken);

        var (_, token, expires) = await CreateTokenAsync(actor, "Pairing package", lifetime, autoApprove, cancellationToken);
        return new PairingPackageDto
        {
            FormatVersion = 2,
            HubUrl = uri.ToString().TrimEnd('/'),
            EnrollmentToken = token,
            CaCertificatePem = certificateAuthority.GetCaCertificatePem(),
            HubCertificateThumbprint = certificateAuthority.GetClientCertificateThumbprint(),
            HubServerCertificateThumbprint = certificateAuthority.GetServerCertificateThumbprint(),
            ExpiresUtc = new DateTimeOffset(expires, TimeSpan.Zero),
            AgentPort = await settings.GetAsync(SettingsKeys.AgentPort, HubConstants.DefaultAgentPort, cancellationToken)
        };
    }

    public async Task<OperationResult> DeleteEnrollmentAsync(string actor, Guid enrollmentId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.EnrollAgents, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var enrollment = await db.AgentEnrollments.FirstOrDefaultAsync(e => e.Id == enrollmentId, cancellationToken);
        if (enrollment is null)
        {
            return OperationResult.Fail("Enrollment request not found.");
        }

        if (enrollment.Status == EnrollmentState.Approved)
        {
            return OperationResult.Fail("Approved enrollments are part of the audit trail. Revoke the agent instead.");
        }

        db.AgentEnrollments.Remove(enrollment);
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, "Agent.EnrollmentDeleted", enrollment.ComputerName, enrollment.ComputerId, cancellationToken: cancellationToken);
        return OperationResult.Ok("Enrollment record removed.");
    }

    internal static bool IsAllowedEnrollmentIp(string remoteIp)
    {
        var normalized = NormalizeIp(remoteIp);
        return IPAddress.TryParse(normalized, out var ip) && NetworkAddressGuard.IsPrivateOrLoopback(ip);
    }

    internal static string NormalizeIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var candidate = value.Trim();
        if (candidate.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[7..];
        }

        if (candidate.Contains(':') && IPAddress.TryParse(candidate, out var ip6) && ip6.IsIPv4MappedToIPv6)
        {
            candidate = ip6.MapToIPv4().ToString();
        }

        return candidate;
    }

    private static async Task<Agent?> FindExistingAgentAsync(
        RemoteDeskDbContext db,
        string? machineId,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(machineId))
        {
            var byMachine = await db.Agents.Include(a => a.Computer)
                .Where(a => a.MachineId == machineId)
                .OrderByDescending(a => a.LastSeenUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (byMachine is not null)
            {
                return byMachine;
            }
        }

        if (installationId != Guid.Empty)
        {
            return await db.Agents.Include(a => a.Computer)
                .Where(a => a.InstallationId == installationId)
                .OrderByDescending(a => a.LastSeenUtc)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return null;
    }

    internal static string? NormalizeMachineId(string? value)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 128)
        {
            return null;
        }

        if (candidate.Any(ch => char.IsControl(ch) || !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')))
        {
            return null;
        }

        return candidate.ToLowerInvariant();
    }

    private static string? SanitizeMachineName(string? machineName)
    {
        var name = machineName?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            return null;
        }

        if (name.Any(char.IsControl) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        return name;
    }

    private static RSA ParsePublicKey(string pem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa;
    }
}
