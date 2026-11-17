using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteDeskHub.Agent.State;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.Agent.Enrollment;

public enum EnrollmentOutcome
{
    Enrolled,
    AwaitingApproval,
    NoPairingPackage,
    Rejected,
    Failed
}

/// <summary>
/// Performs first-run enrollment: generates a key pair and CSR, submits the enrollment token to the
/// hub, polls for administrator approval and stores the issued certificate. All secret material is
/// protected with DPAPI before it touches disk.
/// </summary>
public sealed class EnrollmentClient(
    AgentOptions options,
    AgentStateStore stateStore,
    ILogger<EnrollmentClient> logger)
{
    private static readonly TimeSpan InitialPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxPollDuration = TimeSpan.FromHours(24);

    public async Task<EnrollmentOutcome> EnsureEnrolledAsync(CancellationToken cancellationToken = default)
    {
        var identity = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (stateStore.LoadCertificate(identity) is not null)
        {
            if (string.IsNullOrWhiteSpace(identity.HubServerCertificateThumbprint))
            {
                logger.LogError("This agent was enrolled by an older hub build without TLS certificate " +
                                "pinning. Remove {Path} and enroll again with a current pairing package.",
                    options.StatePath);
                return EnrollmentOutcome.Failed;
            }

            return EnrollmentOutcome.Enrolled;
        }

        if (!string.IsNullOrWhiteSpace(identity.PendingEnrollmentId) && !string.IsNullOrWhiteSpace(identity.PendingToken))
        {
            if (string.IsNullOrWhiteSpace(identity.HubServerCertificateThumbprint))
            {
                logger.LogError("The saved enrollment state does not contain a pinned hub server certificate. " +
                                "Remove {Path} and enroll again with a new pairing package.", options.StatePath);
                return EnrollmentOutcome.Failed;
            }

            return await PollUntilDecidedAsync(identity, cancellationToken).ConfigureAwait(false);
        }

        var package = await LoadPairingPackageAsync(cancellationToken).ConfigureAwait(false);
        if (package is null)
        {
            logger.LogWarning("No pairing package found. Place an .rdhenroll package at {Path} or pass --pair.",
                Path.Combine(options.DataDirectory, "enroll.rdhenroll"));
            return EnrollmentOutcome.NoPairingPackage;
        }

        if (package.ExpiresUtc <= DateTimeOffset.UtcNow)
        {
            logger.LogError("The pairing package expired at {Expires:u}. Generate a new package in RemoteDesk Hub.", package.ExpiresUtc);
            return EnrollmentOutcome.Failed;
        }

        if (string.IsNullOrWhiteSpace(package.HubServerCertificateThumbprint))
        {
            logger.LogError("The pairing package was created by an older hub and does not pin the hub TLS " +
                            "certificate. Generate a new pairing package from RemoteDesk Hub {Version} or later.",
                HubConstants.ProductVersion);
            return EnrollmentOutcome.Failed;
        }

        identity.HubUrl = (options.HubUrlOverride ?? package.HubUrl).TrimEnd('/');
        identity.CaCertificatePem = package.CaCertificatePem;
        identity.HubClientCertificateThumbprint = package.HubCertificateThumbprint;
        identity.HubServerCertificateThumbprint = package.HubServerCertificateThumbprint;
        identity.ManagementPort = options.PortOverride ?? package.AgentPort;

        using var rsa = RSA.Create(3072);
        var machineName = Environment.MachineName;
        var request = new CertificateRequest(
            $"{HubConstants.AgentCertificateSubjectPrefix}{machineName}, O=RemoteDesk Labs",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(machineName);
        san.AddDnsName($"{machineName.ToLowerInvariant()}.local");
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var address in GetLocalAddresses())
        {
            san.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(san.Build());
        var csrPem = request.CreateSigningRequestPem();
        var publicKeyPemText = rsa.ExportSubjectPublicKeyInfoPem();

        var dto = new EnrollmentRequestDto
        {
            Token = package.EnrollmentToken,
            MachineName = machineName,
            AgentVersion = HubConstants.ProductVersion,
            OperatingSystem = Environment.OSVersion.VersionString,
            PublicKeyPem = publicKeyPemText,
            CsrPem = csrPem,
            Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            InstallationId = identity.InstallationId,
            MachineId = identity.MachineId,
            ManagementPort = identity.ManagementPort
        };

        logger.LogInformation("Requesting enrollment with hub {Hub} for {Machine}", identity.HubUrl, machineName);

        EnrollmentResponseDto? response;
        try
        {
            using var http = CreateHubClient(package.CaCertificatePem, package.HubServerCertificateThumbprint);
            using var message = new HttpRequestMessage(HttpMethod.Post, $"{identity.HubUrl}/api/enrollment/request")
            {
                Content = JsonContent.Create(dto, options: JsonDefaults.Options)
            };
            using var result = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var payload = await result.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccessStatusCode)
            {
                var error = TryReadError(payload) ?? $"HTTP {(int)result.StatusCode}";
                logger.LogError("Enrollment request rejected: {Error}", error);
                if (result.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return EnrollmentOutcome.Failed;
                }

                return EnrollmentOutcome.Rejected;
            }

            response = JsonSerializer.Deserialize<EnrollmentResponseDto>(payload, JsonDefaults.Options);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "Could not reach the hub at {Hub}. Retrying later.", identity.HubUrl);
            return EnrollmentOutcome.Failed;
        }

        if (response is null)
        {
            logger.LogError("The hub returned an empty enrollment response.");
            return EnrollmentOutcome.Failed;
        }

        identity.PendingEnrollmentId = response.EnrollmentId.ToString();
        identity.PendingToken = package.EnrollmentToken;
        identity.PendingCsrPem = csrPem;
        identity.PendingPrivateKeyProtected = AgentStateStore.ProtectPrivateKey(rsa);
        identity.LastMessage = response.Message;
        await stateStore.SaveAsync(identity, cancellationToken).ConfigureAwait(false);

        // The token is now protected in agent-state.json. Delete the portable package so the
        // plaintext bearer token does not remain on disk.
        DeletePairingPackage();

        logger.LogInformation("Enrollment submitted ({Status}). {Message}", response.Status, response.Message);
        return await PollUntilDecidedAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EnrollmentOutcome> PollUntilDecidedAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(identity.PendingEnrollmentId, out var enrollmentId) ||
            string.IsNullOrWhiteSpace(identity.PendingToken) ||
            string.IsNullOrWhiteSpace(identity.HubUrl) ||
            string.IsNullOrWhiteSpace(identity.CaCertificatePem))
        {
            logger.LogError("The saved enrollment state is incomplete. Remove {Path} and enroll again.", options.StatePath);
            return EnrollmentOutcome.Failed;
        }

        var deadline = DateTimeOffset.UtcNow.Add(MaxPollDuration);
        var pollInterval = InitialPollInterval;
        using var http = CreateHubClient(identity.CaCertificatePem, identity.HubServerCertificateThumbprint);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, $"{identity.HubUrl}/api/enrollment/status/{enrollmentId:D}");
                request.Headers.Add("X-Enrollment-Token", identity.PendingToken);
                using var result = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var payload = await result.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (result.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    pollInterval = MaxPollInterval;
                    logger.LogWarning("Enrollment status checks are being rate limited; backing off to {Seconds} seconds.",
                        pollInterval.TotalSeconds);
                }
                else if (!result.IsSuccessStatusCode)
                {
                    logger.LogWarning("Enrollment status check returned HTTP {Status}: {Body}", (int)result.StatusCode, payload);
                }
                else
                {
                    var status = JsonSerializer.Deserialize<EnrollmentStatusDto>(payload, JsonDefaults.Options);
                    if (status is not null)
                    {
                        switch (status.Status)
                        {
                            case "Approved" when !string.IsNullOrWhiteSpace(status.AgentCertificatePem):
                                if (await CompleteEnrollmentAsync(identity, status, cancellationToken).ConfigureAwait(false))
                                {
                                    return EnrollmentOutcome.Enrolled;
                                }

                                return EnrollmentOutcome.Failed;
                            case "Rejected":
                            case "Expired":
                            case "Cancelled":
                            case "Invalid":
                                logger.LogError("Enrollment was not approved: {Status} - {Message}", status.Status, status.Message);
                                identity.PendingEnrollmentId = null;
                                identity.PendingToken = null;
                                identity.PendingPrivateKeyProtected = null;
                                identity.PendingCsrPem = null;
                                identity.LastMessage = status.Message;
                                await stateStore.SaveAsync(identity, cancellationToken).ConfigureAwait(false);
                                return status.Status == "Invalid" ? EnrollmentOutcome.Failed : EnrollmentOutcome.Rejected;
                            default:
                                logger.LogInformation("Enrollment {EnrollmentId} is {Status}. {Message}", enrollmentId, status.Status, status.Message);
                                break;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Enrollment status poll failed; retrying.");
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            pollInterval = TimeSpan.FromSeconds(Math.Min(MaxPollInterval.TotalSeconds, pollInterval.TotalSeconds * 2));
        }

        logger.LogError("Enrollment was not approved within {Hours} hours.", MaxPollDuration.TotalHours);
        return EnrollmentOutcome.AwaitingApproval;
    }

    public async Task<bool> TryRenewCertificateAsync(
        AgentIdentity identity,
        X509Certificate2 currentCertificate,
        CancellationToken cancellationToken = default)
    {
        if (identity.AgentId is null ||
            string.IsNullOrWhiteSpace(identity.HubUrl) ||
            string.IsNullOrWhiteSpace(identity.CaCertificatePem) ||
            string.IsNullOrWhiteSpace(identity.HubServerCertificateThumbprint))
        {
            logger.LogWarning("The saved agent identity is incomplete; certificate renewal was skipped.");
            return false;
        }

        try
        {
            using var rsa = RSA.Create(3072);
            var machineName = Environment.MachineName;
            var request = new CertificateRequest(
                $"{HubConstants.AgentCertificateSubjectPrefix}{machineName}, O=RemoteDesk Labs",
                rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(machineName);
            san.AddDnsName($"{machineName.ToLowerInvariant()}.local");
            request.CertificateExtensions.Add(san.Build());

            var dto = new AgentCertificateRenewalRequestDto
            {
                AgentId = identity.AgentId.Value,
                MachineName = machineName,
                CsrPem = request.CreateSigningRequestPem(),
                Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                TimestampUtc = DateTimeOffset.UtcNow,
                AgentVersion = HubConstants.ProductVersion
            };

            using var http = CreateHubClient(
                identity.CaCertificatePem,
                identity.HubServerCertificateThumbprint,
                currentCertificate);
            using var response = await http.PostAsJsonAsync(
                "/api/agent/renew", dto, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Certificate renewal returned HTTP {Status}: {Body}", (int)response.StatusCode, payload);
                return false;
            }

            var renewed = JsonSerializer.Deserialize<AgentCertificateRenewalResponseDto>(payload, JsonDefaults.Options);
            if (renewed is null || string.IsNullOrWhiteSpace(renewed.AgentCertificatePem))
            {
                logger.LogWarning("The hub returned an empty certificate renewal response.");
                return false;
            }

            using var publicCertificate = X509Certificate2.CreateFromPem(renewed.AgentCertificatePem);
            using var withKey = publicCertificate.CopyWithPrivateKey(rsa);
            using var certificate = X509CertificateLoader.LoadPkcs12(
                withKey.Export(X509ContentType.Pfx, string.Empty), string.Empty,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);

            identity.CertificatePfxProtected = AgentStateStore.ProtectPfx(certificate);
            identity.CaCertificatePem = renewed.CaCertificatePem;
            identity.HubServerCertificateThumbprint = renewed.HubServerCertificateThumbprint;
            identity.HubClientCertificateThumbprint =
                renewed.HubClientCertificateThumbprint ?? identity.HubClientCertificateThumbprint;
            identity.EnrolledUtc = DateTime.UtcNow;
            identity.Revoked = false;
            identity.LastMessage = renewed.Message;
            await stateStore.SaveAsync(identity, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Agent certificate renewed. New thumbprint {Thumbprint}; expires {Expires:u}.",
                certificate.Thumbprint,
                certificate.NotAfter.ToUniversalTime());
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or CryptographicException)
        {
            logger.LogWarning(ex, "Agent certificate renewal failed; the current certificate will be retained.");
            return false;
        }
    }

    private async Task<bool> CompleteEnrollmentAsync(AgentIdentity identity, EnrollmentStatusDto status, CancellationToken cancellationToken)
    {        using var rsa = AgentStateStore.LoadProtectedPrivateKey(identity.PendingPrivateKeyProtected);
        if (rsa is null)
        {
            logger.LogError("The pending agent private key could not be read from local protected storage.");
            return false;
        }

        try
        {
            using var publicCertificate = X509Certificate2.CreateFromPem(status.AgentCertificatePem!);
            using var withKey = publicCertificate.CopyWithPrivateKey(rsa);
            using var certificate = X509CertificateLoader.LoadPkcs12(
                withKey.Export(X509ContentType.Pfx, string.Empty), string.Empty,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);

            identity.CertificatePfxProtected = AgentStateStore.ProtectPfx(certificate);
            identity.AgentId = status.AgentId;
            identity.ComputerId = status.ComputerId;
            identity.ManagementPort = status.ManagementPort > 0 ? status.ManagementPort : identity.ManagementPort;
            identity.CaCertificatePem ??= status.CaCertificatePem;
            identity.EnrolledUtc = DateTime.UtcNow;
            identity.Revoked = false;
            identity.PendingEnrollmentId = null;
            identity.PendingToken = null;
            identity.PendingPrivateKeyProtected = null;
            identity.PendingCsrPem = null;
            identity.LastMessage = status.Message;
            await stateStore.SaveAsync(identity, cancellationToken).ConfigureAwait(false);

            // Remove the one-time pairing package once enrollment has completed (it is normally
            // already deleted after the token has been protected in agent-state.json).
            DeletePairingPackage();

            logger.LogInformation("Enrollment approved. Agent {AgentId} / computer {ComputerId}. Certificate thumbprint {Thumbprint}.",
                identity.AgentId, identity.ComputerId, certificate.Thumbprint);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The issued agent certificate could not be installed.");
            return false;
        }
    }

    private async Task<PairingPackageDto?> LoadPairingPackageAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.PairingFilePath) || !File.Exists(options.PairingFilePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(options.PairingFilePath, cancellationToken).ConfigureAwait(false);
            var package = JsonSerializer.Deserialize<PairingPackageDto>(json, JsonDefaults.Options);
            if (package is null || string.IsNullOrWhiteSpace(package.EnrollmentToken))
            {
                logger.LogError("The pairing package {Path} is not valid.", options.PairingFilePath);
                return null;
            }

            if (!Uri.TryCreate(package.HubUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                logger.LogError("The pairing package hub URL must be an absolute HTTPS URL.");
                return null;
            }

            using var caCheck = X509Certificate2.CreateFromPem(package.CaCertificatePem);
            return package;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The pairing package {Path} could not be read.", options.PairingFilePath);
            return null;
        }
    }

    private static HttpClient CreateHubClient(
        string caCertificatePem,
        string? expectedServerThumbprint,
        X509Certificate2? clientCertificate = null)
    {
        if (string.IsNullOrWhiteSpace(expectedServerThumbprint))
        {
            throw new InvalidOperationException(
                "The hub server certificate thumbprint is missing. Generate a new pairing package and enroll again.");
        }

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                ClientCertificates = clientCertificate is null
                    ? null
                    : new X509CertificateCollection { clientCertificate },
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null)
                    {
                        return false;
                    }

                    try
                    {
                        using var presented = new X509Certificate2(certificate);
                        if (!string.Equals(presented.Thumbprint, expectedServerThumbprint, StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        using var root = X509Certificate2.CreateFromPem(caCertificatePem);
                        using var chain = new X509Chain();
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.Add(root);
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        if (!chain.Build(presented))
                        {
                            return false;
                        }

                        var eku = presented.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
                        return eku is not null && eku.EnhancedKeyUsages.Cast<Oid>()
                            .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1");
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        };

        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private void DeletePairingPackage()
    {
        if (string.IsNullOrWhiteSpace(options.PairingFilePath) || !File.Exists(options.PairingFilePath))
        {
            return;
        }

        try
        {
            File.Delete(options.PairingFilePath);
            logger.LogInformation("Deleted the consumed pairing package {Path}.", options.PairingFilePath);
            options.PairingFilePath = null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete the consumed pairing package {Path}.", options.PairingFilePath);
        }
    }

    private static IEnumerable<IPAddress> GetLocalAddresses()
    {
        var results = new List<IPAddress>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                        RemoteDeskHub.Shared.Security.NetworkAddressGuard.IsPrivateOrLoopback(unicast.Address) &&
                        !results.Contains(unicast.Address))
                    {
                        results.Add(unicast.Address);
                    }
                }
            }
        }
        catch
        {
            // Address enumeration is best-effort.
        }

        return results;
    }

    private static string? TryReadError(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<ApiErrorDto>(payload, JsonDefaults.Options)?.Message;
        }
        catch
        {
            return null;
        }
    }
}
