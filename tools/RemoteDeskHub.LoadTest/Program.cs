using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Networking;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.LoadTest;

internal static class Program
{
    private const string AdminUser = "load-admin";
    private const string AdminPassword = "RemoteDesk-Load1";

    public static async Task<int> Main(string[] args)
    {
        LoadOptions options;
        try
        {
            options = LoadOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(LoadOptions.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(LoadOptions.Usage);
            return 0;
        }

        return await LoadTestRunner.RunAsync(options).ConfigureAwait(false);
    }

    private sealed record LoadOptions(
        int Agents,
        int Iterations,
        string Scenario,
        int AuditWriters,
        int AuditWrites,
        bool KeepData,
        bool ShowHelp)
    {
        public const string Usage = """
            RemoteDesk Hub load and fault harness

            Usage:
              dotnet run --project tools/RemoteDeskHub.LoadTest -- [options]

            Options:
              --agents <n>        Synthetic agents (1-500, default 20)
              --iterations <n>    Heartbeats per agent (1-2000, default 25)
              --scenario <name>   load | fault | audit | all (default all)
              --audit-writers <n> Concurrent audit writers (1-64, default 8)
              --audit-writes <n>  Audit rows per writer (1-5000, default 50)
              --keep-data         Keep the temporary database and certificates
              --help              Show this help
            """;

        public static LoadOptions Parse(string[] args)
        {
            var agents = 20;
            var iterations = 25;
            var scenario = "all";
            var auditWriters = 8;
            var auditWrites = 50;
            var keepData = false;
            var showHelp = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--help" or "-h":
                        showHelp = true;
                        break;
                    case "--keep-data":
                        keepData = true;
                        break;
                    case "--agents":
                        agents = ReadInt(args, ref i, "--agents", 1, 500);
                        break;
                    case "--iterations":
                        iterations = ReadInt(args, ref i, "--iterations", 1, 2000);
                        break;
                    case "--scenario":
                        scenario = ReadValue(args, ref i, "--scenario").ToLowerInvariant();
                        if (scenario is not ("load" or "fault" or "audit" or "all"))
                        {
                            throw new ArgumentException("--scenario must be load, fault, audit or all.");
                        }
                        break;
                    case "--audit-writers":
                        auditWriters = ReadInt(args, ref i, "--audit-writers", 1, 64);
                        break;
                    case "--audit-writes":
                        auditWrites = ReadInt(args, ref i, "--audit-writes", 1, 5000);
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument '{args[i]}'.");
                }
            }

            return new LoadOptions(agents, iterations, scenario, auditWriters, auditWrites, keepData, showHelp);
        }

        private static string ReadValue(string[] args, ref int index, string name)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{name} requires a value.");
            }

            return args[++index];
        }

        private static int ReadInt(string[] args, ref int index, string name, int min, int max)
        {
            var text = ReadValue(args, ref index, name);
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
                value < min || value > max)
            {
                throw new ArgumentException($"{name} must be an integer between {min} and {max}.");
            }

            return value;
        }
    }

    private sealed class LoadTestRunner
    {
        public static async Task<int> RunAsync(LoadOptions options)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "RemoteDeskHubLoad",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddRemoteDeskHubCore(root);
            services.AddRemoteDeskHubNetworking();

            await using var provider = services.BuildServiceProvider();
            IHubServer? server = null;
            var result = new HarnessResult
            {
                Scenario = options.Scenario,
                Agents = options.Agents,
                IterationsPerAgent = options.Iterations,
                DataRoot = root,
                Machine = Environment.MachineName,
                ProcessorCount = Environment.ProcessorCount,
                OsVersion = Environment.OSVersion.ToString(),
                StartedUtc = DateTimeOffset.UtcNow
            };

            try
            {
                await provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
                var settings = provider.GetRequiredService<ISettingsService>();
                await settings.SeedDefaultsAsync();
                var auth = provider.GetRequiredService<IAuthService>();
                var created = await auth.CreateInitialAdministratorAsync(AdminUser, AdminPassword);
                if (!created.Success)
                {
                    throw new InvalidOperationException(created.Error ?? "Could not create the load-test administrator.");
                }

                var login = await auth.LoginAsync(AdminUser, AdminPassword, "127.0.0.1");
                if (!login.Success || login.Value is null)
                {
                    throw new InvalidOperationException(login.Error ?? "Could not sign in the load-test administrator.");
                }

                provider.GetRequiredService<ISessionContext>().SignIn(login.Value);

                var port = GetFreeLoopbackPort();
                await settings.SetAsync(SettingsKeys.HubPort, port.ToString(CultureInfo.InvariantCulture), "Network");
                await settings.SetAsync(SettingsKeys.BindAddress, "127.0.0.1", "Network");

                server = provider.GetRequiredService<IHubServer>();
                await server.StartAsync();
                if (server.BoundPort != port)
                {
                    throw new InvalidOperationException($"Hub bound port {server.BoundPort}, expected {port}.");
                }

                result.HubPort = server.BoundPort;
                var agents = options.Scenario == "audit"
                    ? []
                    : await CreateAgentsAsync(provider, options.Agents, port);
                result.Agents = agents.Count;

                if (options.Scenario is "load" or "all")
                {
                    // Warm up TLS sessions and the EF/SQLite connection pool before measuring latency.
                    await RunHeartbeatLoadAsync(agents, 1);
                    result.Load = await RunHeartbeatLoadAsync(agents, options.Iterations);
                }

                if (options.Scenario is "fault" or "all")
                {
                    result.Fault = await RunFaultScenarioAsync(server, agents, options.Iterations);
                }

                if (options.Scenario is "audit" or "all")
                {
                    result.Audit = await RunAuditContentionAsync(
                        provider.GetRequiredService<IAuditService>(), options.AuditWriters, options.AuditWrites);
                }

                var loadPassed = options.Scenario is "fault" or "audit" || result.Load.Errors == 0;
                var faultPassed = options.Scenario is not ("fault" or "all") ||
                                  (result.Fault.FaultsInjected > 0 && result.Fault.RecoveredRequests > 0);
                var auditPassed = !result.Audit.Executed || result.Audit.ChainValid;
                result.Succeeded = loadPassed && faultPassed && auditPassed;
                return result.Succeeded ? 0 : 1;
            }
            catch (Exception ex)
            {
                result.Succeeded = false;
                result.Error = ex.ToString();
                return 1;
            }
            finally
            {
                result.FinishedUtc = DateTimeOffset.UtcNow;
                if (server is not null)
                {
                    try
                    {
                        await server.StopAsync();
                    }
                    catch
                    {
                        // Best effort during cleanup.
                    }
                }

                foreach (var agent in _agents)
                {
                    agent.Client.Dispose();
                    agent.Certificate.Dispose();
                }

                _agents.Clear();
                Console.WriteLine(JsonSerializer.Serialize(result, JsonDefaults.PrettyOptions));

                if (!options.KeepData)
                {
                    try
                    {
                        Directory.Delete(root, recursive: true);
                    }
                    catch
                    {
                        // The OS temp folder is purged periodically.
                    }
                }
            }
        }

        private static readonly List<LoadAgent> _agents = [];
        private static readonly object AgentsLock = new();

        private static async Task<List<LoadAgent>> CreateAgentsAsync(IServiceProvider provider, int count, int port)
        {
            var ca = provider.GetRequiredService<ICertificateAuthority>();
            await ca.EnsureCreatedAsync();
            var factory = provider.GetRequiredService<IDbContextFactory<RemoteDeskDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var now = DateTime.UtcNow;
            var serverThumbprint = ca.GetServerCertificateThumbprint();
            var agents = new List<LoadAgent>(count);

            for (var i = 0; i < count; i++)
            {
                var machineName = $"LOAD-PC-{i + 1:D3}";
                var agentId = Guid.NewGuid();
                var (csr, _, key) = CreateAgentCsr(machineName);
                using (key)
                {
                    var certificatePem = await ca.SignAgentCertificateAsync(csr, machineName, agentId);
                    var certificate = AttachPrivateKey(certificatePem, key);
                    var computer = new Computer
                    {
                        Name = machineName,
                        IpAddress = $"192.168.60.{i + 1}",
                        OperatingSystem = "Windows 11 Pro (load harness)",
                        AgentVersion = "1.1.0-load",
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
                        Id = agentId,
                        ComputerId = computer.Id,
                        MachineName = machineName,
                        State = AgentState.Active,
                        CertificateThumbprint = certificate.Thumbprint,
                        CertificatePem = certificate.ExportCertificatePem(),
                        Version = "1.1.0-load",
                        OperatingSystem = "Windows 11 Pro (load harness)",
                        EnrolledUtc = now,
                        ApprovedUtc = now,
                        LastSeenUtc = now
                    };
                    computer.Agent = agent;
                    db.Computers.Add(computer);
                    db.Agents.Add(agent);
                    agents.Add(new LoadAgent
                    {
                        AgentId = agent.Id,
                        ComputerId = computer.Id,
                        MachineName = machineName,
                        Certificate = certificate,
                        Client = CreateClient(certificate, serverThumbprint, port)
                    });
                }
            }

            await db.SaveChangesAsync();
            lock (AgentsLock)
            {
                _agents.Clear();
                _agents.AddRange(agents);
            }

            return agents;
        }

        private static HttpClient CreateClient(X509Certificate2 certificate, string serverThumbprint, int port)
        {
            var handler = new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                SslProtocols = SslProtocols.Tls13,
                ServerCertificateCustomValidationCallback = (_, serverCertificate, _, _) =>
                    serverCertificate is not null &&
                    string.Equals(serverCertificate.Thumbprint, serverThumbprint, StringComparison.OrdinalIgnoreCase),
                MaxConnectionsPerServer = 64
            };
            handler.ClientCertificates.Add(certificate);
            return new HttpClient(handler)
            {
                BaseAddress = new Uri($"https://127.0.0.1:{port}", UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        private static async Task<LoadResult> RunHeartbeatLoadAsync(
            List<LoadAgent> agents,
            int iterations)
        {
            var latencies = new ConcurrentBag<double>();
            var errors = new ConcurrentBag<string>();
            var started = Stopwatch.StartNew();
            var completed = 0;

            await Parallel.ForEachAsync(
                agents,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, agents.Count) },
                async (agent, cancellationToken) =>
                {
                    for (var i = 0; i < iterations; i++)
                    {
                        var stopwatch = Stopwatch.StartNew();
                        try
                        {
                            using var heartbeat = await agent.Client.PostAsJsonAsync(
                                "/api/agent/heartbeat",
                                new HeartbeatRequestDto
                                {
                                    AgentId = agent.AgentId,
                                    ComputerId = agent.ComputerId,
                                    MachineName = agent.MachineName,
                                    AgentVersion = "1.1.0-load",
                                    ListeningPort = HubConstants.DefaultAgentPort,
                                    Nonce = $"{agent.AgentId:N}-{i}-{Guid.NewGuid():N}"
                                },
                                JsonDefaults.Options,
                                cancellationToken);
                            stopwatch.Stop();
                            if (!heartbeat.IsSuccessStatusCode)
                            {
                                var body = await heartbeat.Content.ReadAsStringAsync(cancellationToken);
                                errors.Add($"{(int)heartbeat.StatusCode}: {Trim(body)}");
                            }
                            else
                            {
                                latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                            }

                            if (i % 5 == 0)
                            {
                                using var telemetry = await agent.Client.PostAsJsonAsync(
                                    "/api/agent/telemetry",
                                    new TelemetryBatchDto
                                    {
                                        AgentId = agent.AgentId,
                                        Nonce = $"{agent.AgentId:N}-tm-{i}-{Guid.NewGuid():N}",
                                        Samples =
                                        [
                                            new AgentMetricsDto
                                            {
                                                CpuPercent = 10 + i % 20,
                                                MemoryPercent = 40,
                                                MemoryTotalBytes = 16L * 1024 * 1024 * 1024,
                                                MemoryAvailableBytes = 9L * 1024 * 1024 * 1024,
                                                DiskPercent = 50,
                                                DiskTotalBytes = 512L * 1024 * 1024 * 1024,
                                                DiskFreeBytes = 256L * 1024 * 1024 * 1024,
                                                ProcessCount = 120,
                                                ServiceCount = 90
                                            }
                                        ]
                                    },
                                    JsonDefaults.Options,
                                    cancellationToken);
                            }
                        }
                        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                        {
                            stopwatch.Stop();
                            errors.Add($"{ex.GetType().Name}: {Trim(ex.Message)}");
                        }

                        Interlocked.Increment(ref completed);
                    }
                });

            started.Stop();
            var values = latencies.ToArray();
            return new LoadResult
            {
                Requests = completed,
                Successes = values.Length,
                Errors = errors.Count,
                ErrorSamples = errors.Take(5).ToArray(),
                DurationSeconds = Math.Round(started.Elapsed.TotalSeconds, 2),
                RequestsPerSecond = Math.Round(completed / Math.Max(0.001, started.Elapsed.TotalSeconds), 2),
                MeanMs = Math.Round(values.Length == 0 ? 0 : values.Average(), 2),
                P50Ms = Math.Round(Percentile(values, 50), 2),
                P95Ms = Math.Round(Percentile(values, 95), 2),
                P99Ms = Math.Round(Percentile(values, 99), 2),
                MaxMs = Math.Round(values.Length == 0 ? 0 : values.Max(), 2)
            };
        }

        private static async Task<FaultResult> RunFaultScenarioAsync(
            IHubServer server,
            List<LoadAgent> agents,
            int iterations)
        {
            var result = new FaultResult();
            var faultAt = Math.Max(1, iterations / 2);

            // Run one warm-up round so every client has a TLS session before the hub is stopped.
            await RunHeartbeatLoadAsync(agents, Math.Min(2, iterations));

            var latencies = new ConcurrentBag<double>();
            var errors = new ConcurrentBag<string>();
            var recovered = 0;
            var injected = 0;

            await Parallel.ForEachAsync(
                agents,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, agents.Count) },
                async (agent, cancellationToken) =>
                {
                    for (var i = 0; i < iterations; i++)
                    {
                        if (i == faultAt)
                        {
                            if (Interlocked.Exchange(ref injected, 1) == 0)
                            {
                                await server.StopAsync(cancellationToken);
                                result.DowntimeMs = 600;
                                await Task.Delay(result.DowntimeMs, cancellationToken);
                                await server.StartAsync(cancellationToken);
                            }
                            else
                            {
                                // Other agents wait for the single injected outage to finish.
                                await Task.Delay(700, cancellationToken);
                            }
                        }

                        var stopwatch = Stopwatch.StartNew();
                        try
                        {
                            using var heartbeat = await agent.Client.PostAsJsonAsync(
                                "/api/agent/heartbeat",
                                new HeartbeatRequestDto
                                {
                                    AgentId = agent.AgentId,
                                    ComputerId = agent.ComputerId,
                                    MachineName = agent.MachineName,
                                    AgentVersion = "1.1.0-load",
                                    ListeningPort = HubConstants.DefaultAgentPort,
                                    Nonce = $"{agent.AgentId:N}-fault-{i}-{Guid.NewGuid():N}"
                                },
                                JsonDefaults.Options,
                                cancellationToken);
                            stopwatch.Stop();
                            if (heartbeat.IsSuccessStatusCode)
                            {
                                latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                                if (i > faultAt)
                                {
                                    Interlocked.Increment(ref recovered);
                                }
                            }
                            else
                            {
                                errors.Add($"{(int)heartbeat.StatusCode}");
                            }
                        }
                        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                        {
                            stopwatch.Stop();
                            errors.Add(ex.GetType().Name);
                        }
                    }
                });

            result.FaultsInjected = injected;
            result.RecoveredRequests = recovered;
            result.Successes = latencies.Count;
            result.Errors = errors.Count;
            result.ErrorSamples = errors.Distinct().Take(5).ToArray();
            result.DowntimeMs = 600;
            result.P95Ms = Math.Round(Percentile(latencies.ToArray(), 95), 2);
            return result;
        }

        private static async Task<AuditResult> RunAuditContentionAsync(
            IAuditService audit,
            int writers,
            int writesPerWriter)
        {
            var started = Stopwatch.StartNew();
            var errors = new ConcurrentBag<string>();
            var completed = 0;

            await Parallel.ForEachAsync(
                Enumerable.Range(0, writers),
                new ParallelOptions { MaxDegreeOfParallelism = writers },
                async (writer, cancellationToken) =>
                {
                    for (var i = 0; i < writesPerWriter; i++)
                    {
                        try
                        {
                            await audit.LogAsync(
                                $"load-writer-{writer}",
                                "LoadTest.AuditContention",
                                "load",
                                result: "Success",
                                details: $"writer={writer};row={i};run={Guid.NewGuid():N}",
                                cancellationToken: cancellationToken);
                            Interlocked.Increment(ref completed);
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"{ex.GetType().Name}: {Trim(ex.Message)}");
                        }
                    }
                });

            started.Stop();
            var verification = await audit.VerifyChainAsync();
            return new AuditResult
            {
                Executed = true,
                Writers = writers,
                WritesPerWriter = writesPerWriter,
                Completed = completed,
                Errors = errors.Count,
                ErrorSamples = errors.Take(5).ToArray(),
                DurationSeconds = Math.Round(started.Elapsed.TotalSeconds, 2),
                RowsPerSecond = Math.Round(completed / Math.Max(0.001, started.Elapsed.TotalSeconds), 2),
                ChainValid = verification.Valid,
                ChainMessage = verification.Message
            };
        }

        private static (string CsrPem, string PublicKeyPem, RSA Key) CreateAgentCsr(string machineName)
        {
            var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN={machineName}, O=RemoteDesk Agent",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            return (request.CreateSigningRequestPem(), rsa.ExportSubjectPublicKeyInfoPem(), rsa);
        }

        private static X509Certificate2 AttachPrivateKey(string certificatePem, RSA key)
        {
            using var certificate = X509Certificate2.CreateFromPem(certificatePem);
            using var withKey = certificate.CopyWithPrivateKey(key);
            var pfx = withKey.Export(X509ContentType.Pfx);
            return X509CertificateLoader.LoadPkcs12(
                pfx, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        }

        private static int GetFreeLoopbackPort()
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static double Percentile(IReadOnlyCollection<double> values, double percentile)
        {
            if (values.Count == 0)
            {
                return 0;
            }

            var sorted = values.OrderBy(value => value).ToArray();
            var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
            return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
        }

        private static string Trim(string value) =>
            value.Length <= 300 ? value : value[..300] + "...";

        private sealed class LoadAgent
        {
            public required Guid AgentId { get; init; }
            public required Guid ComputerId { get; init; }
            public required string MachineName { get; init; }
            public required X509Certificate2 Certificate { get; init; }
            public required HttpClient Client { get; init; }
        }

        private sealed class HarnessResult
        {
            public bool Succeeded { get; set; }
            public string Scenario { get; set; } = string.Empty;
            public int Agents { get; set; }
            public int IterationsPerAgent { get; set; }
            public int HubPort { get; set; }
            public string Machine { get; set; } = string.Empty;
            public int ProcessorCount { get; set; }
            public string OsVersion { get; set; } = string.Empty;
            public string DataRoot { get; set; } = string.Empty;
            public DateTimeOffset StartedUtc { get; set; }
            public DateTimeOffset FinishedUtc { get; set; }
            public string? Error { get; set; }
            public LoadResult Load { get; set; } = new();
            public FaultResult Fault { get; set; } = new();
            public AuditResult Audit { get; set; } = new();
        }

        private sealed class LoadResult
        {
            public long Requests { get; set; }
            public long Successes { get; set; }
            public int Errors { get; set; }
            public string[] ErrorSamples { get; set; } = [];
            public double DurationSeconds { get; set; }
            public double RequestsPerSecond { get; set; }
            public double MeanMs { get; set; }
            public double P50Ms { get; set; }
            public double P95Ms { get; set; }
            public double P99Ms { get; set; }
            public double MaxMs { get; set; }
        }

        private sealed class FaultResult
        {
            public int FaultsInjected { get; set; }
            public int DowntimeMs { get; set; }
            public int RecoveredRequests { get; set; }
            public int Successes { get; set; }
            public int Errors { get; set; }
            public string[] ErrorSamples { get; set; } = [];
            public double P95Ms { get; set; }
        }

        private sealed class AuditResult
        {
            public bool Executed { get; set; }
            public int Writers { get; set; }
            public int WritesPerWriter { get; set; }
            public int Completed { get; set; }
            public int Errors { get; set; }
            public string[] ErrorSamples { get; set; } = [];
            public double DurationSeconds { get; set; }
            public double RowsPerSecond { get; set; }
            public bool ChainValid { get; set; }
            public string? ChainMessage { get; set; }
        }
    }
}
