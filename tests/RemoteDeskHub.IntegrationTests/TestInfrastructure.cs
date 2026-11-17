using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure;
using RemoteDeskHub.Networking;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.IntegrationTests;

/// <summary>Clock with a settable UTC value so time-dependent behaviour can be tested deterministically.</summary>
public sealed class MutableClock : IClock
{
    public DateTime UtcNow { get; set; } = DateTime.UtcNow;

    public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
}

/// <summary>
/// Isolated hub instance: unique data root, real SQLite database, real services and a signed-in administrator.
/// Each instance is disposed (and its data folder deleted) at the end of a test.
/// </summary>
public sealed class TestHub : IAsyncDisposable
{
    public const string AdminUserName = "admin";
    public const string AdminPassword = "RemoteDesk-Admin1";

    private readonly bool _deleteOnDispose;

    private TestHub(string root, ServiceProvider services, MutableClock clock, bool deleteOnDispose)
    {
        RootDirectory = root;
        Services = services;
        Clock = clock;
        _deleteOnDispose = deleteOnDispose;
    }

    public string RootDirectory { get; }
    public ServiceProvider Services { get; }
    public MutableClock Clock { get; }

    public ISessionContext Session => Services.GetRequiredService<ISessionContext>();
    public IAppPaths Paths => Services.GetRequiredService<IAppPaths>();

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public static async Task<TestHub> CreateAsync(
        Action<IServiceCollection>? configure = null,
        bool createAdministrator = true,
        string password = AdminPassword,
        string? dataRoot = null,
        bool deleteOnDispose = true)
    {
        var root = dataRoot ?? Path.Combine(Path.GetTempPath(), "RemoteDeskHubTests", Guid.NewGuid().ToString("N"));
        var clock = new MutableClock();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddRemoteDeskHubCore(root);
        services.AddRemoteDeskHubNetworking();
        services.Replace(ServiceDescriptor.Singleton<IClock>(clock));
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false });
        var initializer = provider.GetRequiredService<IDatabaseInitializer>();
        await initializer.InitializeAsync();
        await provider.GetRequiredService<ISettingsService>().SeedDefaultsAsync();

        if (createAdministrator)
        {
            var auth = provider.GetRequiredService<IAuthService>();
            var created = await auth.CreateInitialAdministratorAsync(AdminUserName, password);
            Assert.True(created.Success, created.Error);

            var login = await auth.LoginAsync(AdminUserName, password, "127.0.0.1");
            Assert.True(login.Success, login.Error);
            provider.GetRequiredService<ISessionContext>().SignIn(login.Value!);
        }

        return new TestHub(root, provider, clock, deleteOnDispose);
    }

    /// <summary>Creates a managed, approved computer together with an active agent record.</summary>
    public async Task<(Computer Computer, Agent Agent)> CreateApprovedComputerAsync(
        string name = "TEST-PC-01",
        string ip = "192.168.1.10",
        string? certificateThumbprint = null)
    {
        await using var db = await Get<Microsoft.EntityFrameworkCore.IDbContextFactory<RemoteDeskHub.Infrastructure.Data.RemoteDeskDbContext>>()
            .CreateDbContextAsync();

        var now = Clock.UtcNow;
        var computer = new Computer
        {
            Name = name,
            IpAddress = ip,
            OperatingSystem = "Windows 11 Pro",
            AgentVersion = "1.0.0",
            IsManaged = true,
            IsTrusted = true,
            IsRegistered = true,
            Status = ComputerStatus.Offline,
            FirstSeenUtc = now,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var agent = new Agent
        {
            ComputerId = computer.Id,
            MachineName = name,
            State = AgentState.Active,
            CertificateThumbprint = certificateThumbprint,
            Version = "1.0.0",
            OperatingSystem = "Windows 11 Pro",
            EnrolledUtc = now,
            ApprovedUtc = now,
            LastSeenUtc = now
        };
        computer.Agent = agent;
        db.Computers.Add(computer);
        db.Agents.Add(agent);
        await db.SaveChangesAsync();
        return (computer, agent);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        if (!_deleteOnDispose)
        {
            return;
        }

        try
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup; the OS temp folder is purged periodically.
        }
    }
}

/// <summary>Helpers for creating certificate signing requests that look like agent requests.</summary>
public static class TestCertificates
{
    public static (string CsrPem, string PublicKeyPem, RSA Key) CreateAgentCsr(string machineName)
    {
        var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={machineName}, O=RemoteDesk Agent",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var csrPem = request.CreateSigningRequestPem();
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();
        return (csrPem, publicKeyPem, rsa);
    }

    /// <summary>Combines an issued certificate with the private key that created its CSR so it can be used by HttpClient.</summary>
    public static X509Certificate2 AttachPrivateKey(string certificatePem, RSA key)
    {
        using var certificate = X509Certificate2.CreateFromPem(certificatePem);
        using var withKey = certificate.CopyWithPrivateKey(key);
        var pfx = withKey.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }
}

/// <summary>
/// In-memory replacement for the HTTPS agent transport. Records calls and supports scripted results so
/// hub-side services can be exercised without a real agent process.
/// </summary>
public sealed class FakeAgentTransport : IAgentTransport
{
    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CommandRequestDto> Commands { get; } = [];
    public List<FileWriteRequestDto> Writes { get; } = [];
    public List<string> TerminateCalls { get; } = [];
    public List<(string Service, string Action)> ServiceCalls { get; } = [];

    public Func<CommandRequestDto, CommandResultDto>? CommandHandler { get; set; }
    public bool FailCommands { get; set; }
    public IReadOnlyList<RemoteProcessDto> Processes { get; set; } = [];
    public IReadOnlyList<RemoteServiceDto> Services { get; set; } = [];

    public Task<AgentInfoDto> GetInfoAsync(Computer computer, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentInfoDto
        {
            AgentId = computer.Agent?.Id ?? Guid.NewGuid(),
            ComputerId = computer.Id,
            MachineName = computer.Name,
            AgentVersion = "1.0.0",
            OperatingSystem = computer.OperatingSystem ?? "Windows",
            ListeningPort = computer.Agent?.ListeningPort ?? HubConstants.DefaultAgentPort
        });

    public Task<AgentMetricsDto> GetMetricsAsync(Computer computer, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentMetricsDto { TimestampUtc = DateTimeOffset.UtcNow, CpuPercent = 5 });

    public Task<HardwareInventoryDto> GetHardwareAsync(Computer computer, CancellationToken cancellationToken = default) =>
        Task.FromResult(new HardwareInventoryDto { CpuModel = "Test CPU", RamTotalBytes = 16L * 1024 * 1024 * 1024 });

    public Task<SoftwareInventoryDto> GetSoftwareAsync(Computer computer, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SoftwareInventoryDto { Items = [] });

    public Task<IReadOnlyList<RemoteProcessDto>> GetProcessesAsync(Computer computer, CancellationToken cancellationToken = default) =>
        Task.FromResult(Processes);

    public Task<OperationResult> TerminateProcessAsync(Computer computer, int pid, string processName, CancellationToken cancellationToken = default)
    {
        TerminateCalls.Add($"{pid}:{processName}");
        return Task.FromResult(OperationResult.Ok("Terminated."));
    }

    public Task<IReadOnlyList<RemoteServiceDto>> GetServicesAsync(Computer computer, CancellationToken cancellationToken = default) =>
        Task.FromResult(Services);

    public Task<IReadOnlyList<RemoteServiceDto>> GetServiceDependenciesAsync(Computer computer, string serviceName, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RemoteServiceDto>>([]);

    public Task<OperationResult> ServiceActionAsync(Computer computer, string serviceName, string action, string? startupType, CancellationToken cancellationToken = default)
    {
        ServiceCalls.Add((serviceName, action));
        return Task.FromResult(OperationResult.Ok("Service updated."));
    }

    public Task<CommandResultDto> ExecuteCommandAsync(Computer computer, CommandRequestDto request, CancellationToken cancellationToken = default)
    {
        Commands.Add(request);
        if (FailCommands)
        {
            return Task.FromResult(new CommandResultDto
            {
                CorrelationId = request.CorrelationId,
                ExitCode = 1,
                StdErr = "scripted failure",
                Error = "scripted failure"
            });
        }

        if (CommandHandler is not null)
        {
            return Task.FromResult(CommandHandler(request));
        }

        return Task.FromResult(new CommandResultDto
        {
            CorrelationId = request.CorrelationId,
            ExitCode = 0,
            StdOut = $"ran {request.Command}",
            CompletedUtc = DateTimeOffset.UtcNow
        });
    }

    public Task<OperationResult> CancelCommandAsync(Computer computer, Guid correlationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Ok("Cancelled."));

    public Task<IReadOnlyList<RemoteEventLogDto>> GetEventsAsync(Computer computer, string logName, int maxEntries, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RemoteEventLogDto>>([]);

    public SharedFolderManifestDto SharedFolder { get; set; } = new()
    {
        Enabled = true,
        Root = @"C:\zzz",
        Entries = []
    };

    public Task<SharedFolderManifestDto> GetSharedFolderManifestAsync(Computer computer, CancellationToken cancellationToken = default) =>
        Task.FromResult(SharedFolder);

    public Task<FileListDto> ListFilesAsync(Computer computer, string path, CancellationToken cancellationToken = default)
    {
        var entries = Files
            .Where(pair => Path.GetDirectoryName(pair.Key)?.Equals(path, StringComparison.OrdinalIgnoreCase) == true)
            .Select(pair => new FileEntryDto
            {
                Name = Path.GetFileName(pair.Key),
                FullPath = pair.Key,
                SizeBytes = pair.Value.Length,
                Attributes = "Archive",
                ModifiedUtc = DateTimeOffset.UtcNow
            })
            .ToList();

        return Task.FromResult(new FileListDto { Path = path, Entries = entries });
    }

    public Task<FileStatDto> StatFileAsync(Computer computer, string path, bool includeHash, CancellationToken cancellationToken = default)
    {
        if (!Files.TryGetValue(path, out var bytes))
        {
            return Task.FromResult(new FileStatDto { Path = path, Exists = false });
        }

        return Task.FromResult(new FileStatDto
        {
            Path = path,
            Exists = true,
            SizeBytes = bytes.Length,
            ModifiedUtc = DateTimeOffset.UtcNow,
            CanRead = true,
            CanWrite = true,
            Sha256 = includeHash ? Convert.ToHexString(SHA256.HashData(bytes)) : null
        });
    }

    public Task<FileChunkDto> ReadFileChunkAsync(Computer computer, string path, long offset, int length, CancellationToken cancellationToken = default)
    {
        if (!Files.TryGetValue(path, out var bytes))
        {
            throw new FileNotFoundException("Not found", path);
        }

        var slice = bytes.Skip((int)offset).Take(length).ToArray();
        return Task.FromResult(new FileChunkDto
        {
            Path = path,
            Offset = offset,
            DataBase64 = Convert.ToBase64String(slice),
            Length = slice.Length,
            TotalLength = bytes.Length
        });
    }

    public Task<FileWriteResultDto> WriteFileChunkAsync(Computer computer, FileWriteRequestDto request, CancellationToken cancellationToken = default)
    {
        Writes.Add(request);
        if (request.Offset == 0 || !Files.ContainsKey(request.Path))
        {
            Files[request.Path] = [];
        }

        var buffer = Files[request.Path].ToList();
        buffer.AddRange(Convert.FromBase64String(request.DataBase64));
        Files[request.Path] = buffer.ToArray();
        return Task.FromResult(new FileWriteResultDto
        {
            Success = true,
            BytesWritten = Convert.FromBase64String(request.DataBase64).Length,
            TotalLength = request.TotalLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(Files[request.Path]))
        });
    }

    public Task<OperationResult> CreateDirectoryAsync(Computer computer, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Ok());

    public Task<OperationResult> RenameFileAsync(Computer computer, string path, string newName, bool confirmed, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Ok());

    public Task<OperationResult> DeleteFileAsync(Computer computer, string path, bool recursive, bool confirmed, CancellationToken cancellationToken = default)
    {
        Files.TryRemove(path, out _);
        return Task.FromResult(OperationResult.Ok());
    }

    public Task<FileHashDto> GetFileHashAsync(Computer computer, string path, CancellationToken cancellationToken = default)
    {
        if (!Files.TryGetValue(path, out var bytes))
        {
            throw new FileNotFoundException("Not found", path);
        }

        return Task.FromResult(new FileHashDto(path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
    }

    public Task<OperationResult> FinalizeUploadAsync(Computer computer, string temporaryPath, string finalPath, CancellationToken cancellationToken = default)
    {
        if (Files.TryRemove(temporaryPath, out var bytes))
        {
            Files[finalPath] = bytes;
        }

        return Task.FromResult(OperationResult.Ok());
    }

    public Task<OperationResult> PingAsync(Computer computer, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Ok());

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
