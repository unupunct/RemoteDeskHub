using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Security;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class DiscoveryService(
    IDbContextFactory<RemoteDeskDbContext> contextFactory,
    ISessionContext session,
    IAuditService audit,
    IAppEvents events,
    IClock clock) : IDiscoveryService
{
    private const int MaxHosts = 4096;

    private static readonly Dictionary<string, string> OuiVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["00:05:69"] = "VMware", ["00:0C:29"] = "VMware", ["00:1C:14"] = "VMware", ["00:50:56"] = "VMware",
        ["00:15:5D"] = "Microsoft (Hyper-V)", ["00:03:FF"] = "Microsoft", ["7C:ED:8D"] = "Microsoft",
        ["00:1B:21"] = "Intel", ["00:1E:67"] = "Intel", ["3C:97:0E"] = "Intel", ["8C:16:45"] = "Intel",
        ["00:14:22"] = "Dell", ["00:21:9B"] = "Dell", ["18:66:DA"] = "Dell", ["B0:83:FE"] = "Dell",
        ["00:1A:4B"] = "Hewlett Packard", ["00:25:B3"] = "Hewlett Packard", ["3C:D9:2B"] = "Hewlett Packard",
        ["00:12:5A"] = "Lenovo", ["00:50:8B"] = "Lenovo", ["54:EE:75"] = "Lenovo",
        ["00:1F:F3"] = "Apple", ["3C:07:54"] = "Apple", ["F0:18:98"] = "Apple",
        ["00:1B:FC"] = "ASUSTek", ["04:D4:C4"] = "ASUSTek", ["2C:56:DC"] = "ASUSTek",
        ["00:E0:4C"] = "Realtek", ["52:54:00"] = "QEMU/KVM", ["08:00:27"] = "Oracle VirtualBox",
        ["00:15:5D"] = "Microsoft (Hyper-V)", ["AC:DE:48"] = "Private", ["DC:A6:32"] = "Raspberry Pi",
        ["00:26:BB"] = "Apple", ["78:2B:CB"] = "Dell", ["B4:2E:99"] = "GIGA-BYTE",
        ["00:24:8C"] = "ASUSTek", ["F4:8E:38"] = "Dell", ["00:23:24"] = "G-PRO Computer",
        ["98:FA:9B"] = "Intel", ["D8:BB:C1"] = "Micro-Star International", ["00:1C:42"] = "Parallels"
    };

    public async Task<DiscoveryScanResult> ScanAsync(DiscoveryScanRequest request, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ViewComputers);
        var timeoutMs = Math.Clamp(request.TimeoutMs <= 0 ? 800 : request.TimeoutMs, 100, 5000);
        var concurrency = Math.Clamp(request.Concurrency <= 0 ? 24 : request.Concurrency, 1, 128);
        var ports = (request.ProbePorts ?? []).Where(p => p is > 0 and < 65536).Distinct().Take(12).ToList();
        if (ports.Count == 0)
        {
            ports.Add(HubConstants.DefaultAgentPort);
        }

        var addresses = await BuildTargetListAsync(request, cancellationToken);
        if (addresses.Count == 0)
        {
            return new DiscoveryScanResult { Duration = TimeSpan.Zero };
        }

        progress?.Report($"Scanning {addresses.Count} addresses with {concurrency} workers...");
        var stopwatch = Stopwatch.StartNew();
        var discoveries = new List<ProbeResult>();
        var scanned = 0;
        using var gate = new SemaphoreSlim(concurrency);
        var sync = new object();

        var tasks = addresses.Select(async ip =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ProbeHostAsync(ip, timeoutMs, ports, cancellationToken);
                if (result.Responded)
                {
                    lock (sync)
                    {
                        discoveries.Add(result);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A single host failure must never stop the scan.
            }
            finally
            {
                gate.Release();
                var count = Interlocked.Increment(ref scanned);
                if (count % 16 == 0 || count == addresses.Count)
                {
                    progress?.Report($"Scanned {count} of {addresses.Count} addresses. {discoveries.Count} responding devices...");
                }
            }
        });

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        // Apply results sequentially to keep duplicate detection deterministic.
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var registered = await db.Computers.AsNoTracking()
            .Select(c => new { c.Id, c.Name, c.IpAddress }).ToListAsync(cancellationToken);
        var agents = await db.Agents.AsNoTracking()
            .Where(a => a.State == AgentState.Active)
            .Select(a => new { a.ComputerId, a.LastKnownIp, a.LastSeenUtc }).ToListAsync(cancellationToken);
        var agentIps = agents.Where(a => a.LastKnownIp != null).ToDictionary(a => a.LastKnownIp!, a => a.LastSeenUtc, StringComparer.OrdinalIgnoreCase);

        var devices = await db.DiscoveryDevices.ToListAsync(cancellationToken);
        var newDevices = 0;
        var now = clock.UtcNow;

        foreach (var discovery in discoveries.OrderBy(d => IpToUInt(IPAddress.Parse(d.IpAddress))))
        {
            var device = devices.FirstOrDefault(d => string.Equals(d.IpAddress, discovery.IpAddress, StringComparison.OrdinalIgnoreCase));
            if (device is null && !string.IsNullOrWhiteSpace(discovery.MacAddress))
            {
                var macMatch = devices.FirstOrDefault(d => string.Equals(d.MacAddress, discovery.MacAddress, StringComparison.OrdinalIgnoreCase));
                if (macMatch is not null)
                {
                    devices.Remove(macMatch);
                    device = macMatch;
                    device.IpAddress = discovery.IpAddress;
                }
            }

            if (device is null)
            {
                device = new DiscoveryDevice
                {
                    IpAddress = discovery.IpAddress,
                    FirstSeenUtc = now
                };
                db.DiscoveryDevices.Add(device);
                devices.Add(device);
                newDevices++;
            }

            device.MacAddress = discovery.MacAddress ?? device.MacAddress;
            device.Hostname = discovery.Hostname ?? device.Hostname;
            device.Vendor = discovery.MacAddress is null ? device.Vendor : GetVendor(discovery.MacAddress) ?? device.Vendor;
            device.ResponseTimeMs = discovery.ResponseTimeMs;
            device.OpenServicesCsv = discovery.OpenServices.Count > 0 ? string.Join(',', discovery.OpenServices) : device.OpenServicesCsv;
            device.LastSeenUtc = now;
            device.LastScanUtc = now;

            var byIp = registered.FirstOrDefault(c => string.Equals(c.IpAddress, discovery.IpAddress, StringComparison.OrdinalIgnoreCase));
            var hasAgent = discovery.HasAgent || agentIps.ContainsKey(discovery.IpAddress);
            device.HasAgent = hasAgent;
            if (byIp is not null)
            {
                device.IsRegistered = true;
                device.ComputerId = byIp.Id;
            }
            else
            {
                device.IsRegistered = false;
                device.ComputerId = null;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        stopwatch.Stop();

        await audit.LogAsync(session.CurrentUser?.UserName ?? HubConstants.AuditActorSystem, "Discovery.Scan",
            details: $"Scanned {addresses.Count} hosts; {discoveries.Count} responded; {newDevices} new devices.",
            durationMs: (long)stopwatch.Elapsed.TotalMilliseconds, cancellationToken: cancellationToken);
        events.RaiseDataChanged();

        var resultViews = discoveries.Count == 0
            ? []
            : await GetDevicesAsync(cancellationToken);
        var respondedIps = discoveries.Select(d => d.IpAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new DiscoveryScanResult
        {
            HostsScanned = addresses.Count,
            DevicesFound = discoveries.Count,
            NewDevices = newDevices,
            AgentsFound = discoveries.Count(d => d.HasAgent || agentIps.ContainsKey(d.IpAddress)),
            Duration = stopwatch.Elapsed,
            Devices = resultViews.Where(d => respondedIps.Contains(d.IpAddress)).ToList()
        };
    }

    public async Task<IReadOnlyList<DiscoveryView>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.DiscoveryDevices.AsNoTracking()
            .OrderByDescending(d => d.LastSeenUtc)
            .Select(d => new DiscoveryView
            {
                Id = d.Id,
                IpAddress = d.IpAddress,
                MacAddress = d.MacAddress,
                Hostname = d.Hostname,
                Vendor = d.Vendor,
                OperatingSystem = d.OperatingSystem,
                ResponseTimeMs = d.ResponseTimeMs,
                OpenServices = d.OpenServicesCsv,
                HasAgent = d.HasAgent,
                IsRegistered = d.IsRegistered,
                IsTrusted = d.IsTrusted,
                FriendlyName = d.FriendlyName,
                Notes = d.Notes,
                ComputerId = d.ComputerId,
                FirstSeenUtc = d.FirstSeenUtc,
                LastSeenUtc = d.LastSeenUtc
            }).ToListAsync(cancellationToken);
    }

    public async Task<OperationResult> AddToInventoryAsync(string actor, Guid deviceId, string? friendlyName, Guid? groupId, string? notes, bool trusted, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageComputers, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var device = await db.DiscoveryDevices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null)
        {
            return OperationResult.Fail("Discovered device not found.");
        }

        if (groupId.HasValue && !await db.ComputerGroups.AnyAsync(g => g.Id == groupId, cancellationToken))
        {
            return OperationResult.Fail("The selected group does not exist.");
        }

        var computer = await db.Computers.FirstOrDefaultAsync(c => c.IpAddress == device.IpAddress, cancellationToken);
        if (computer is null && device.ComputerId.HasValue)
        {
            computer = await db.Computers.FirstOrDefaultAsync(c => c.Id == device.ComputerId, cancellationToken);
        }

        if (computer is null)
        {
            computer = new Computer
            {
                Name = string.IsNullOrWhiteSpace(friendlyName) ? device.Hostname ?? device.IpAddress : friendlyName.Trim(),
                FriendlyName = string.IsNullOrWhiteSpace(friendlyName) ? null : friendlyName.Trim(),
                IpAddress = device.IpAddress,
                MacAddress = device.MacAddress,
                Notes = notes,
                GroupId = groupId,
                IsRegistered = true,
                IsTrusted = trusted,
                IsManaged = device.HasAgent,
                Source = "Discovery",
                Status = device.HasAgent ? ComputerStatus.Offline : ComputerStatus.Unknown,
                FirstSeenUtc = device.FirstSeenUtc,
                CreatedUtc = clock.UtcNow,
                UpdatedUtc = clock.UtcNow
            };
            db.Computers.Add(computer);
        }
        else
        {
            computer.IsRegistered = true;
            computer.IsTrusted = trusted;
            if (!string.IsNullOrWhiteSpace(friendlyName))
            {
                computer.FriendlyName = friendlyName.Trim();
            }

            if (notes is not null)
            {
                computer.Notes = notes;
            }

            if (groupId.HasValue)
            {
                computer.GroupId = groupId;
            }

            computer.UpdatedUtc = clock.UtcNow;
        }

        if (groupId.HasValue)
        {
            if (!await db.ComputerGroupMemberships.AnyAsync(m => m.ComputerId == computer.Id && m.GroupId == groupId, cancellationToken))
            {
                db.ComputerGroupMemberships.Add(new ComputerGroupMembership { ComputerId = computer.Id, GroupId = groupId.Value, AssignedUtc = clock.UtcNow });
            }
        }

        device.IsRegistered = true;
        device.IsTrusted = trusted;
        device.ComputerId = computer.Id;
        device.FriendlyName = string.IsNullOrWhiteSpace(friendlyName) ? device.FriendlyName : friendlyName.Trim();
        device.Notes = notes ?? device.Notes;
        device.GroupId = groupId;

        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, AuditOperations.ComputerAdded, computer.Name, computer.Id,
            details: $"Discovered device {device.IpAddress} added to inventory. Trusted={trusted}.", cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok($"Device added to the inventory as '{computer.Name}'.");
    }

    public async Task<OperationResult> UpdateDeviceAsync(string actor, Guid deviceId, string? friendlyName, string? notes, bool? trusted, Guid? groupId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageComputers, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var device = await db.DiscoveryDevices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null)
        {
            return OperationResult.Fail("Discovered device not found.");
        }

        device.FriendlyName = string.IsNullOrWhiteSpace(friendlyName) ? null : friendlyName.Trim();
        device.Notes = notes;
        if (trusted.HasValue)
        {
            device.IsTrusted = trusted.Value;
        }

        device.GroupId = groupId;
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, "Discovery.DeviceUpdated", details: $"Device {device.IpAddress} updated.", cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok("Device updated.");
    }

    public async Task<OperationResult> RemoveDeviceAsync(string actor, Guid deviceId, CancellationToken cancellationToken = default)
    {
        AccessGuard.Require(session, Permission.ManageComputers, actor);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var device = await db.DiscoveryDevices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null)
        {
            return OperationResult.Fail("Device not found.");
        }

        db.DiscoveryDevices.Remove(device);
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(actor, "Discovery.DeviceRemoved", details: $"Device {device.IpAddress} removed from discovery inventory.", cancellationToken: cancellationToken);
        events.RaiseDataChanged();
        return OperationResult.Ok("Device removed from discovery inventory. Managed computers are unaffected.");
    }

    public Task<IReadOnlyList<string>> GetLocalSubnetsAsync(CancellationToken cancellationToken = default)
    {
        var subnets = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && NetworkAddressGuard.IsPrivateOrLoopback(a.Address) && !IPAddress.IsLoopback(a.Address))
            .Select(a => ToCidr(a.Address, a.IPv4Mask))
            .Where(s => s is not null)
            .Select(s => s!)
            .Distinct()
            .OrderBy(s => s)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(subnets);
    }

    private async Task<List<IPAddress>> BuildTargetListAsync(DiscoveryScanRequest request, CancellationToken cancellationToken)
    {
        var addresses = new SortedSet<uint>();
        void AddRange(uint start, uint end)
        {
            if (end < start)
            {
                (start, end) = (end, start);
            }

            var count = (long)end - start + 1;
            if (count > MaxHosts)
            {
                throw new InvalidOperationException($"The requested range contains {count:N0} addresses. Scan at most {MaxHosts:N0} addresses at a time.");
            }

            for (var value = start; value <= end && value != 0; value++)
            {
                var ip = UIntToIp(value);
                if (NetworkAddressGuard.IsWithinScanScope(ip))
                {
                    addresses.Add(value);
                }

                if (value == uint.MaxValue)
                {
                    break;
                }
            }
        }

        var mode = (request.Mode ?? "range").Trim().ToLowerInvariant();
        if (mode is "range" && !string.IsNullOrWhiteSpace(request.Range))
        {
            var parts = request.Range.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is 1 or 2)
            {
                if (!NetworkAddressGuard.TryParseLanAddress(parts[0], out var start, out var error))
                {
                    throw new InvalidOperationException(error);
                }

                var end = start;
                if (parts.Length == 2 && !NetworkAddressGuard.TryParseLanAddress(parts[1], out end, out error))
                {
                    throw new InvalidOperationException(error);
                }

                AddRange(IpToUInt(start), IpToUInt(end));
            }
        }
        else if (mode == "cidr" && !string.IsNullOrWhiteSpace(request.Cidr))
        {
            var cidr = request.Cidr.Trim();
            var pieces = cidr.Split('/');
            if (pieces.Length != 2 || !NetworkAddressGuard.TryParseLanAddress(pieces[0], out var network, out var error) ||
                !int.TryParse(pieces[1], out var prefix) || prefix is < 20 or > 30)
            {
                throw new InvalidOperationException("Enter a CIDR range between /20 and /30 using a private IPv4 address.");
            }

            var ipValue = IpToUInt(network);
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            var networkValue = ipValue & mask;
            var broadcast = networkValue | ~mask;
            AddRange(networkValue + 1, broadcast - 1);
        }
        else if (mode == "local" || request.IncludeLocalSubnet)
        {
            foreach (var subnet in await GetLocalSubnetsAsync(cancellationToken))
            {
                var pieces = subnet.Split('/');
                if (pieces.Length != 2 || !IPAddress.TryParse(pieces[0], out var network) || !int.TryParse(pieces[1], out var prefix))
                {
                    continue;
                }

                // Local subnet discovery intentionally scans /24 at most, to stay conservative.
                var effectivePrefix = Math.Max(24, prefix);
                var ipValue = IpToUInt(network);
                var mask = effectivePrefix == 0 ? 0u : uint.MaxValue << (32 - effectivePrefix);
                var networkValue = ipValue & mask;
                var broadcast = networkValue | ~mask;
                AddRange(networkValue + 1, broadcast - 1);
            }

            if (mode == "local" && string.IsNullOrWhiteSpace(request.Range) && string.IsNullOrWhiteSpace(request.Cidr))
            {
                // local-only scan
            }
            else if (mode == "range" && string.IsNullOrWhiteSpace(request.Range))
            {
                // fall back to local subnet
            }
        }

        if (addresses.Count == 0 && mode == "local")
        {
            throw new InvalidOperationException("No active private IPv4 network was found. Enter an IP range or CIDR subnet instead.");
        }

        return addresses.Select(UIntToIp).ToList();
    }

    private async Task<ProbeResult> ProbeHostAsync(IPAddress ip, int timeoutMs, IReadOnlyList<int> ports, CancellationToken cancellationToken)
    {
        var result = new ProbeResult { IpAddress = ip.ToString() };
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(ip, timeoutMs);
            if (reply.Status == IPStatus.Success)
            {
                result.Responded = true;
                result.ResponseTimeMs = reply.RoundtripTime;
            }
        }
        catch
        {
            // ICMP may be blocked by policy; continue with TCP probes.
        }

        foreach (var port in ports)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(ip, port, timeout.Token);
                result.Responded = true;
                result.OpenServices.Add(DescribePort(port));
                if (port == HubConstants.DefaultAgentPort)
                {
                    result.HasAgent = true;
                }
            }
            catch
            {
                // Port closed or timed out.
            }
        }

        if (!result.Responded)
        {
            return result;
        }

        result.MacAddress = TryGetMac(ip);
        result.Hostname = await TryResolveHostnameAsync(ip, timeoutMs, cancellationToken);

        if (result.OpenServices.Count == 0 && ports.Contains(HubConstants.DefaultAgentPort) && await ProbeAgentTlsAsync(ip, HubConstants.DefaultAgentPort, timeoutMs, cancellationToken))
        {
            result.HasAgent = true;
        }

        return result;
    }

    private static async Task<bool> ProbeAgentTlsAsync(IPAddress ip, int port, int timeoutMs, CancellationToken cancellationToken)
    {
        string? subject = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(ip, port, timeout.Token);
            await using var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
            {
                if (certificate is not null)
                {
                    subject = certificate.Subject;
                }

                return true;
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = ip.ToString(),
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, timeout.Token);
        }
        catch
        {
            // The handshake may fail while still revealing the agent certificate.
        }

        return subject?.Contains("RemoteDesk Agent", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static async Task<string?> TryResolveHostnameAsync(IPAddress ip, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(500, timeoutMs * 2)));
            var entry = await Dns.GetHostEntryAsync(ip.ToString()).WaitAsync(timeout.Token);
            return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetMac(IPAddress ip)
    {
        try
        {
            var destination = BitConverter.ToInt32(ip.GetAddressBytes(), 0);
            var buffer = new byte[6];
            uint length = (uint)buffer.Length;
            var result = SendARP(destination, 0, buffer, ref length);
            if (result == 0 && length >= 6 && buffer.Any(b => b != 0))
            {
                return string.Join(':', buffer.Take(6).Select(b => b.ToString("X2")));
            }
        }
        catch
        {
            // MAC address is not always obtainable.
        }

        return null;
    }

    private static string? GetVendor(string mac)
    {
        if (mac.Length < 8)
        {
            return null;
        }

        var prefix = mac[..8].ToUpperInvariant();
        return OuiVendors.TryGetValue(prefix, out var vendor) ? vendor : null;
    }

    private static string DescribePort(int port) => port switch
    {
        445 => "SMB (445)",
        3389 => "RDP (3389)",
        7444 => "RemoteDesk Agent (7444)",
        80 => "HTTP (80)",
        443 => "HTTPS (443)",
        22 => "SSH (22)",
        5985 => "WinRM (5985)",
        5986 => "WinRM TLS (5986)",
        _ => $"TCP ({port})"
    };

    private static string? ToCidr(IPAddress address, IPAddress? mask)
    {
        if (mask is null)
        {
            return null;
        }

        var maskBytes = mask.GetAddressBytes();
        var prefix = maskBytes.Sum(b => System.Numerics.BitOperations.PopCount(b));
        var bytes = address.GetAddressBytes();
        var value = IpToUInt(address);
        var network = prefix == 0 ? 0u : value & (uint.MaxValue << (32 - prefix));
        return $"{UIntToIp(network)}/{prefix}";
    }

    public static uint IpToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress UIntToIp(uint value)
        => new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref uint phyAddrLen);

    private sealed class ProbeResult
    {
        public string IpAddress { get; set; } = string.Empty;
        public string? MacAddress { get; set; }
        public string? Hostname { get; set; }
        public double? ResponseTimeMs { get; set; }
        public List<string> OpenServices { get; } = [];
        public bool HasAgent { get; set; }
        public bool Responded { get; set; }
    }
}
