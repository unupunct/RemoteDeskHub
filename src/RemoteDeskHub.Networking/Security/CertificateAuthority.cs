using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Networking.Security;

/// <summary>
/// Issues and stores the hub's private certificate authority, the hub TLS certificate and the
/// hub client certificate. Private keys are encrypted at rest with Windows DPAPI (current user).
/// </summary>
public sealed class CertificateAuthority : ICertificateAuthority
{
    private readonly IAppPaths _paths;
    private readonly ILogger<CertificateAuthority> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly byte[] _entropy = Encoding.UTF8.GetBytes("RemoteDeskHub.CertificateAuthority.v1");

    private X509Certificate2? _ca;
    private X509Certificate2? _server;
    private X509Certificate2? _client;
    private string? _caPem;

    private string CaCertPath => Path.Combine(_paths.CertificatesDirectory, "ca.crt.pem");
    private string CaKeyPath => Path.Combine(_paths.CertificatesDirectory, "ca.key.dat");
    private string ServerCertPath => Path.Combine(_paths.CertificatesDirectory, "hub-server.crt.pem");
    private string ServerKeyPath => Path.Combine(_paths.CertificatesDirectory, "hub-server.key.dat");
    private string ClientCertPath => Path.Combine(_paths.CertificatesDirectory, "hub-client.crt.pem");
    private string ClientKeyPath => Path.Combine(_paths.CertificatesDirectory, "hub-client.key.dat");

    public CertificateAuthority(IAppPaths paths, ILogger<CertificateAuthority> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        if (_server is not null && _client is not null && _ca is not null)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_server is not null && _client is not null && _ca is not null)
            {
                return;
            }

            Directory.CreateDirectory(_paths.CertificatesDirectory);
            _caPem = File.Exists(CaCertPath) ? await File.ReadAllTextAsync(CaCertPath, cancellationToken) : null;
            _ca = LoadCertificate(CaCertPath, CaKeyPath);
            _server = LoadCertificate(ServerCertPath, ServerKeyPath);
            _client = LoadCertificate(ClientCertPath, ClientKeyPath);

            if (_ca is null)
            {
                _ca = CreateRootAuthority();
                SaveCertificate(_ca, CaCertPath, CaKeyPath);
                _caPem = _ca.ExportCertificatePem();
                await File.WriteAllTextAsync(CaCertPath, _caPem, cancellationToken);
                _logger.LogInformation("Created RemoteDesk Hub certificate authority");
            }

            // A partially restored data folder can leave leaf certificates signed by a previous CA.
            // Recreate them whenever they are missing, expired, or not issued by the loaded CA.
            if (_server is null || !IsIssuedByCurrentCa(_server))
            {
                _server?.Dispose();
                _server = CreateLeafCertificate("RemoteDesk Hub", serverAuth: true, clientAuth: false);
                SaveCertificate(_server, ServerCertPath, ServerKeyPath);
                _logger.LogInformation("Created hub TLS certificate");
            }

            if (_client is null || !IsIssuedByCurrentCa(_client))
            {
                _client?.Dispose();
                _client = CreateLeafCertificate("RemoteDesk Hub Console", serverAuth: false, clientAuth: true);
                SaveCertificate(_client, ClientCertPath, ClientKeyPath);
                _logger.LogInformation("Created hub client certificate");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public string GetCaCertificatePem()
    {
        EnsureReady();
        return _caPem ??= _ca!.ExportCertificatePem();
    }

    public string GetClientCertificatePem()
    {
        EnsureReady();
        return _client!.ExportCertificatePem();
    }

    public string GetServerCertificateThumbprint()
    {
        EnsureReady();
        return _server!.Thumbprint;
    }

    public string GetClientCertificateThumbprint()
    {
        EnsureReady();
        return _client!.Thumbprint;
    }

    public X509Certificate2 GetServerCertificate()
    {
        EnsureReady();
        return _server!;
    }

    public X509Certificate2 GetClientCertificate()
    {
        EnsureReady();
        return _client!;
    }

    public X509Certificate2 GetCaCertificate()
    {
        EnsureReady();
        return _ca!;
    }

    public async Task<string> SignAgentCertificateAsync(string csrPem, string machineName, Guid agentId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(machineName) ||
            !Regex.IsMatch(machineName, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("The computer name is not a valid DNS host name.", nameof(machineName));
        }

        await EnsureCreatedAsync(cancellationToken);
        EnsureReady();

        var csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default);

        // Never trust SAN/EKU/KU extensions supplied by the enrolling agent. In particular an
        // agent must not be able to request a certificate for the hub's DNS name or IP address.
        foreach (var extension in csr.CertificateExtensions
                     .Where(e => e.Oid?.Value is "2.5.29.17" or "2.5.29.15" or "2.5.29.37")
                     .ToList())
        {
            csr.CertificateExtensions.Remove(extension);
        }

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(machineName);
        sanBuilder.AddDnsName($"{machineName.ToLowerInvariant()}.local");
        csr.CertificateExtensions.Add(sanBuilder.Build());

        csr.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new("1.3.6.1.5.5.7.3.2"), // client auth (heartbeat)
            new("1.3.6.1.5.5.7.3.1")  // server auth (management API)
        }, false));
        csr.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

        var ca = _ca ?? throw new InvalidOperationException("The hub certificate authority is not initialized.");
        using var caRsa = ca.GetRSAPrivateKey() ?? throw new InvalidOperationException("The CA private key is unavailable.");
        var generator = X509SignatureGenerator.CreateForRSA(caRsa, RSASignaturePadding.Pkcs1);
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = DateTimeOffset.UtcNow.AddYears(1);
        using var certificate = csr.Create(ca.SubjectName, generator, notBefore, notAfter, serial);
        _logger.LogInformation("Issued agent certificate for {MachineName} (agent {AgentId})", machineName, agentId);
        return certificate.ExportCertificatePem();
    }

    public bool TryValidateAgentCertificate(string? certificatePem, out string? thumbprint, out string? error)
    {
        thumbprint = null;
        error = null;
        if (string.IsNullOrWhiteSpace(certificatePem))
        {
            error = "Certificate is missing.";
            return false;
        }

        try
        {
            using var certificate = X509Certificate2.CreateFromPem(certificatePem);
            if (!ValidateCertificate(certificate, requireServerAuthentication: false))
            {
                error = "The agent certificate is not trusted by the hub certificate authority.";
                return false;
            }

            var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
            if (eku is not null && !eku.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2"))
            {
                error = "The certificate is not valid for client authentication.";
                return false;
            }

            thumbprint = certificate.Thumbprint;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool IsAgentCertificateTrusted(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return false;
        }

        // Database-backed approval/revocation checks are performed by the hub middleware.
        // This function only confirms the thumbprint format is usable.
        return thumbprint.Length is 40 or 64;
    }

    private bool IsIssuedByCurrentCa(X509Certificate2 certificate)
    {
        try
        {
            if (_ca is null || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            {
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(_ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            return chain.Build(certificate);
        }
        catch
        {
            return false;
        }
    }

    public bool ValidateCertificate(X509Certificate2 certificate, bool requireServerAuthentication)
    {
        EnsureReady();
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(_ca!);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            var valid = chain.Build(certificate);
            if (!valid)
            {
                return false;
            }

            if (certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow)
            {
                return false;
            }

            if (!requireServerAuthentication)
            {
                return true;
            }

            var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
            return eku is null || eku.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1");
        }
        catch
        {
            return false;
        }
    }

    private X509Certificate2 CreateRootAuthority()
    {
        using var rsa = RSA.Create(4096);
        var request = new CertificateRequest(
            $"CN=RemoteDesk Hub Certificate Authority, O=RemoteDesk Labs, OU={Environment.MachineName}",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        // Re-import so the private key is usable for signing.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }

    private X509Certificate2 CreateLeafCertificate(string commonName, bool serverAuth, bool clientAuth)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}, O=RemoteDesk Labs", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

        var oids = new OidCollection();
        if (serverAuth)
        {
            oids.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        }

        if (clientAuth)
        {
            oids.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        }

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, false));

        if (serverAuth)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddDnsName(Environment.MachineName);
            san.AddDnsName($"{Environment.MachineName.ToLowerInvariant()}.local");
            san.AddIpAddress(IPAddress.Loopback);
            san.AddIpAddress(IPAddress.IPv6Loopback);
            foreach (var address in GetLocalIPv4Addresses())
            {
                san.AddIpAddress(address);
            }

            request.CertificateExtensions.Add(san.Build());
        }

        var ca = _ca ?? throw new InvalidOperationException("The hub certificate authority is not initialized.");
        using var caRsa = ca.GetRSAPrivateKey() ?? throw new InvalidOperationException("The CA private key is unavailable.");
        var generator = X509SignatureGenerator.CreateForRSA(caRsa, RSASignaturePadding.Pkcs1);
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var certificate = request.Create(ca.SubjectName, generator, DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(2), serial);
        using var withKey = certificate.CopyWithPrivateKey(rsa);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }

    private static IEnumerable<IPAddress> GetLocalIPv4Addresses()
    {
        var addresses = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !addresses.Contains(unicast.Address))
                {
                    addresses.Add(unicast.Address);
                }
            }
        }

        return addresses;
    }

    private X509Certificate2? LoadCertificate(string certPath, string keyPath)
    {
        try
        {
            if (!File.Exists(certPath) || !File.Exists(keyPath))
            {
                return null;
            }

            var certPem = File.ReadAllText(certPath);
            var encrypted = File.ReadAllBytes(keyPath);
            var keyPem = Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser));
            var certificate = X509Certificate2.CreateFromPem(certPem, keyPem);
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load certificate {CertPath}; a new certificate will be created", certPath);
            return null;
        }
    }

    private void SaveCertificate(X509Certificate2 certificate, string certPath, string keyPath)
    {
        var key = certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("Certificate has no RSA private key.");
        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        var keyPem = key.ExportPkcs8PrivateKeyPem();
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(keyPem), _entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(keyPath, encrypted);
    }

    private void EnsureReady()
    {
        if (_ca is null || _server is null || _client is null)
        {
            throw new InvalidOperationException("The certificate authority has not been initialized. Call EnsureCreatedAsync first.");
        }
    }
}
