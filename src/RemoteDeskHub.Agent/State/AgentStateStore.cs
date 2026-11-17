using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Agent.State;

/// <summary>Persisted identity and trust material for one agent installation.</summary>
public sealed class AgentIdentity
{
    public Guid InstallationId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Stable machine identity from the Windows registry. Unlike the IP address, it survives
    /// restarts and DHCP changes and lets the hub keep one computer record across re-enrollment.
    /// </summary>
    public string? MachineId { get; set; }

    public Guid? AgentId { get; set; }
    public Guid? ComputerId { get; set; }
    public string? HubUrl { get; set; }
    public string? CaCertificatePem { get; set; }
    public string? HubClientCertificateThumbprint { get; set; }
    public string? HubServerCertificateThumbprint { get; set; }
    public int ManagementPort { get; set; } = HubConstants.DefaultAgentPort;
    public string? CertificatePfxProtected { get; set; }
    public string? PendingEnrollmentId { get; set; }

    /// <summary>The single-use enrollment token, protected with DPAPI when persisted.</summary>
    [JsonConverter(typeof(ProtectedStringJsonConverter))]
    public string? PendingToken { get; set; }

    public string? PendingPrivateKeyProtected { get; set; }
    public string? PendingCsrPem { get; set; }
    public bool Revoked { get; set; }
    public DateTime? EnrolledUtc { get; set; }
    public string? LastMessage { get; set; }

    public bool HasCertificate => !string.IsNullOrWhiteSpace(CertificatePfxProtected);
}

/// <summary>
/// Stores agent state on disk. Secrets (the agent private key) are encrypted with Windows DPAPI
/// at machine scope because the agent runs as a Windows service under LocalSystem.
/// </summary>
public sealed class AgentStateStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RemoteDesk.Agent.State.v1");
    private readonly AgentOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AgentIdentity? _cached;

    public AgentStateStore(AgentOptions options)
    {
        _options = options;
    }

    public async Task<AgentIdentity> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            _options.EnsureDirectories();
            if (!File.Exists(_options.StatePath))
            {
                _cached = new AgentIdentity();
                _cached.MachineId = MachineIdentity.GetStableMachineId(_cached.InstallationId);
                return _cached;
            }

            var json = await File.ReadAllTextAsync(_options.StatePath, cancellationToken).ConfigureAwait(false);
            _cached = JsonSerializer.Deserialize<AgentIdentity>(json, JsonDefaults.Options) ?? new AgentIdentity();
            if (string.IsNullOrWhiteSpace(_cached.MachineId))
            {
                _cached.MachineId = MachineIdentity.GetStableMachineId(_cached.InstallationId);
                await WriteUnlockedAsync(_cached, cancellationToken).ConfigureAwait(false);
            }

            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AgentIdentity identity, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cached = identity;
            await WriteUnlockedAsync(identity, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteUnlockedAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        _options.EnsureDirectories();
        var json = JsonSerializer.Serialize(identity, JsonDefaults.PrettyOptions);
        var temp = _options.StatePath + ".tmp";
        await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temp, _options.StatePath, true);
    }

    public X509Certificate2? LoadCertificate(AgentIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.CertificatePfxProtected))
        {
            return null;
        }

        try
        {
            var pfx = Unprotect(identity.CertificatePfxProtected);
            return X509CertificateLoader.LoadPkcs12(pfx, null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        }
        catch
        {
            return null;
        }
    }

    public static string ProtectPfx(X509Certificate2 certificate)
    {
        var pfx = certificate.Export(X509ContentType.Pfx);
        return Protect(pfx);
    }

    public static string ProtectPrivateKey(RSA rsa)
    {
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        return Protect(Encoding.UTF8.GetBytes(pem));
    }

    public static RSA? LoadProtectedPrivateKey(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        try
        {
            var pem = Encoding.UTF8.GetString(Unprotect(protectedValue));
            var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return rsa;
        }
        catch
        {
            return null;
        }
    }

    private static string Protect(byte[] plain)
    {
        var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(cipher);
    }

    private static byte[] Unprotect(string value)
        => ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.LocalMachine);

    internal static string ProtectString(string value)
        => Protect(Encoding.UTF8.GetBytes(value));

    internal static string? UnprotectString(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Unprotect(value));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            // Backward compatibility: old versions stored the token in plaintext. It will be
            // encrypted the next time the state file is saved.
            return null;
        }
    }
}

/// <summary>DPAPI-protects a string property while keeping the JSON shape a plain string.</summary>
internal sealed class ProtectedStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return AgentStateStore.UnprotectString(value) ?? value;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(AgentStateStore.ProtectString(value));
    }
}
