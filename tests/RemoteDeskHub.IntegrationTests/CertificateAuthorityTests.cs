using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Networking.Security;

namespace RemoteDeskHub.IntegrationTests;

public sealed class CertificateAuthorityTests
{
    [Fact]
    public async Task EnsureCreated_CreatesCaServerAndClientCertificates()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        var ca = hub.Get<ICertificateAuthority>();

        await ca.EnsureCreatedAsync();

        Assert.True(File.Exists(Path.Combine(hub.Paths.CertificatesDirectory, "ca.crt.pem")));
        Assert.True(File.Exists(Path.Combine(hub.Paths.CertificatesDirectory, "ca.key.dat")));
        Assert.True(File.Exists(Path.Combine(hub.Paths.CertificatesDirectory, "hub-server.crt.pem")));
        Assert.True(File.Exists(Path.Combine(hub.Paths.CertificatesDirectory, "hub-client.crt.pem")));

        using var root = ca.GetCaCertificate();
        Assert.True(root.HasPrivateKey);
        Assert.Contains("Certificate Authority", root.Subject);

        using var server = ca.GetServerCertificate();
        using var client = ca.GetClientCertificate();
        Assert.NotEqual(server.Thumbprint, client.Thumbprint);
        Assert.Equal(40, ca.GetServerCertificateThumbprint().Length);
        Assert.Equal(40, ca.GetClientCertificateThumbprint().Length);
    }

    [Fact]
    public async Task PrivateKeys_AreEncryptedWithDpapi()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        await hub.Get<ICertificateAuthority>().EnsureCreatedAsync();

        var keyBytes = await File.ReadAllBytesAsync(Path.Combine(hub.Paths.CertificatesDirectory, "ca.key.dat"));
        var text = System.Text.Encoding.UTF8.GetString(keyBytes);

        Assert.DoesNotContain("PRIVATE KEY", text);
        Assert.False(text.StartsWith("-----BEGIN", StringComparison.Ordinal));
        Assert.True(keyBytes.Length > 100);
    }

    [Fact]
    public async Task EnsureCreated_IsIdempotent_AndReusesCertificates()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        var ca = hub.Get<ICertificateAuthority>();
        await ca.EnsureCreatedAsync();
        var first = ca.GetServerCertificateThumbprint();

        await ca.EnsureCreatedAsync();

        Assert.Equal(first, ca.GetServerCertificateThumbprint());
    }

    [Fact]
    public async Task ServerCertificate_IsTrustedByCa_AndHasServerAuthEku()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        var ca = hub.Get<ICertificateAuthority>();
        await ca.EnsureCreatedAsync();

        Assert.True(ca.ValidateCertificate(ca.GetServerCertificate(), requireServerAuthentication: true));
    }

    [Fact]
    public async Task ClientCertificate_IsNotAcceptedAsServerCertificate()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        var ca = hub.Get<ICertificateAuthority>();
        await ca.EnsureCreatedAsync();

        Assert.False(ca.ValidateCertificate(ca.GetClientCertificate(), requireServerAuthentication: true));
        Assert.True(ca.ValidateCertificate(ca.GetClientCertificate(), requireServerAuthentication: false));
    }

    [Fact]
    public async Task SelfSignedCertificate_IsRejected()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        var ca = hub.Get<ICertificateAuthority>();
        await ca.EnsureCreatedAsync();

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Impostor", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var impostor = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        Assert.False(ca.ValidateCertificate(impostor, requireServerAuthentication: false));
    }

    [Fact]
    public async Task SignAgentCertificate_ProducesTrustedClientCertificate()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        var ca = hub.Get<ICertificateAuthority>();
        await ca.EnsureCreatedAsync();
        var (csrPem, publicKeyPem, key) = TestCertificates.CreateAgentCsr("AGENT-PC");
        using var signingKey = key;

        var certPem = await ca.SignAgentCertificateAsync(csrPem, "AGENT-PC", Guid.NewGuid());

        using var certificate = X509Certificate2.CreateFromPem(certPem);
        var issuedPublicKeyPem = certificate.GetRSAPublicKey()!.ExportSubjectPublicKeyInfoPem();
        Assert.Equal(
            publicKeyPem.Replace("\r\n", "\n").Trim(),
            issuedPublicKeyPem.Replace("\r\n", "\n").Trim());
        Assert.True(ca.ValidateCertificate(certificate, requireServerAuthentication: false));
        Assert.Contains("AGENT-PC", certificate.Subject);
        Assert.True(certificate.NotAfter > DateTime.UtcNow.AddDays(300));
    }

    [Fact]
    public async Task MissingCa_RecreatesLeafCertificatesSignedByNewCa()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        var original = hub.Get<ICertificateAuthority>();
        await original.EnsureCreatedAsync();
        var originalServerThumbprint = original.GetServerCertificateThumbprint();

        File.Delete(Path.Combine(hub.Paths.CertificatesDirectory, "ca.crt.pem"));
        File.Delete(Path.Combine(hub.Paths.CertificatesDirectory, "ca.key.dat"));

        var replacement = new CertificateAuthority(hub.Paths, NullLogger<CertificateAuthority>.Instance);
        await replacement.EnsureCreatedAsync();

        Assert.NotEqual(originalServerThumbprint, replacement.GetServerCertificateThumbprint());
        Assert.True(replacement.ValidateCertificate(replacement.GetServerCertificate(), requireServerAuthentication: true));
        Assert.True(replacement.ValidateCertificate(replacement.GetClientCertificate(), requireServerAuthentication: false));
    }

    [Fact]
    public async Task SignAgentCertificate_IgnoresSanAndEkuFromCsr()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        var ca = hub.Get<ICertificateAuthority>();
        await ca.EnsureCreatedAsync();

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=AGENT-SAN, O=RemoteDesk Agent", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var requestedSan = new SubjectAlternativeNameBuilder();
        requestedSan.AddDnsName("hub.example.com");
        requestedSan.AddIpAddress(IPAddress.Parse("10.0.0.5"));
        request.CertificateExtensions.Add(requestedSan.Build());
        var csrPem = request.CreateSigningRequestPem();

        var certPem = await ca.SignAgentCertificateAsync(csrPem, "AGENT-SAN", Guid.NewGuid());
        using var certificate = X509Certificate2.CreateFromPem(certPem);

        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        var names = san.EnumerateDnsNames().ToList();
        Assert.Contains("AGENT-SAN", names);
        Assert.Contains("agent-san.local", names);
        Assert.DoesNotContain("hub.example.com", names);
        Assert.Equal(2, names.Count);

        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(eku.EnhancedKeyUsages.Cast<Oid>(), oid => oid.Value == "1.3.6.1.5.5.7.3.2");
        Assert.Contains(eku.EnhancedKeyUsages.Cast<Oid>(), oid => oid.Value == "1.3.6.1.5.5.7.3.1");
    }

    [Fact]
    public async Task TryValidateAgentCertificate_DetectsForeignCertificate()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        var ca = hub.Get<ICertificateAuthority>();
        await ca.EnsureCreatedAsync();

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Foreign", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var foreign = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        Assert.False(ca.TryValidateAgentCertificate(foreign.ExportCertificatePem(), out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));

        var (csrPem, _, key) = TestCertificates.CreateAgentCsr("GOOD-PC");
        using var signingKey = key;
        var goodPem = await ca.SignAgentCertificateAsync(csrPem, "GOOD-PC", Guid.NewGuid());
        Assert.True(ca.TryValidateAgentCertificate(goodPem, out var thumbprint, out _));
        Assert.Equal(40, thumbprint!.Length);

        Assert.False(ca.TryValidateAgentCertificate("not a certificate", out _, out _));
        Assert.False(ca.TryValidateAgentCertificate(null, out _, out _));
    }

    [Fact]
    public async Task PairingPackage_ContainsOnlyPublicMaterial()
    {
        await using var hub = await TestHub.CreateAsync();
        var enrollment = hub.Get<IEnrollmentService>();

        var package = await enrollment.CreatePairingPackageAsync(
            TestHub.AdminUserName, TimeSpan.FromHours(1), "https://192.168.1.5:7443");

        Assert.Contains("BEGIN CERTIFICATE", package.CaCertificatePem);
        Assert.DoesNotContain("PRIVATE KEY", package.CaCertificatePem);
        Assert.False(string.IsNullOrWhiteSpace(package.EnrollmentToken));
        Assert.Equal("https://192.168.1.5:7443", package.HubUrl);
        Assert.True(package.ExpiresUtc > DateTimeOffset.UtcNow);

        var json = RemoteDeskHub.Shared.JsonDefaults.Serialize(package);
        Assert.DoesNotContain("PRIVATE KEY", json);
        Assert.DoesNotContain("PasswordHash", json);
        Assert.DoesNotContain("privateKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PairingPackage_RejectsNonHttpsUrl()
    {
        await using var hub = await TestHub.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => hub.Get<IEnrollmentService>()
            .CreatePairingPackageAsync(TestHub.AdminUserName, TimeSpan.FromHours(1), "http://localhost:7443"));
    }
}
