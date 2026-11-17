# Load, Fault and Audit-Contention Testing

RemoteDesk Hub includes a loopback harness under `tools/RemoteDeskHub.LoadTest`. It starts the real
hub composition (SQLite, migrations, private CA, Kestrel, TLS 1.3, mutual TLS, replay guard and
audit chain), creates synthetic agent identities with real CA-issued certificates, and drives HTTP
traffic through the same endpoints a deployed agent uses.

The harness is intentionally **single-machine**. It measures the hub's own concurrency, SQLite
write contention and recovery behaviour when the listener is stopped and restarted. It does not
prove LAN capacity, switch behaviour, WAN latency, multi-machine failover or sustained multi-day
soak behaviour.

## 1. Run it

```powershell
Set-Location 'D:\MyAIProjects\RemoteDesk Hub'

# Default: 20 agents x 25 heartbeats, fault run, then 8 x 50 audit writes
dotnet run --project tools\RemoteDeskHub.LoadTest

# A steady-state load run
dotnet run --project tools\RemoteDeskHub.LoadTest -- `
  --agents 25 --iterations 40 --scenario load

# Stop and restart the hub in the middle of a run
dotnet run --project tools\RemoteDeskHub.LoadTest -- `
  --agents 20 --iterations 30 --scenario fault

# Concurrent audit writers + hash-chain verification
dotnet run --project tools\RemoteDeskHub.LoadTest -- `
  --scenario audit --audit-writers 16 --audit-writes 100
```

Options:

| Option | Default | Meaning |
| --- | ---: | --- |
| `--agents` | 20 | Synthetic agents, each with a unique certificate and database row (1-500) |
| `--iterations` | 25 | Heartbeats per agent (1-2,000) |
| `--scenario` | `all` | `load`, `fault`, `audit` or `all` |
| `--audit-writers` | 8 | Concurrent audit writers (1-64) |
| `--audit-writes` | 50 | Rows per audit writer (1-5,000) |
| `--keep-data` | off | Keep the temporary database and certificates for inspection |
| `--help` | off | Print usage |

The harness prints a JSON result and exits `0` when the scenario passed, `1` when a request, audit
row, recovery check or hash-chain verification failed, and `2` for invalid arguments. It creates a
unique data root under `%TEMP%\RemoteDeskHubLoad\<timestamp>\<guid>` and deletes it unless
`--keep-data` is used.

## 2. What the scenarios exercise

### `load`

- Creates the CA, hub server certificate and one client certificate per synthetic agent.
- Inserts a managed `Computer` and active `Agent` row per certificate, with a unique private IPv4
  address.
- Warms TLS sessions and the SQLite connection pool.
- Sends heartbeats over `https://127.0.0.1:<port>` with the agent certificate; the client pins the
  hub TLS certificate thumbprint.
- Sends a telemetry batch every fifth iteration.
- Reports successes, errors, throughput and p50/p95/p99/max latency.

### `fault`

- Runs a warm-up round.
- Stops the real `IHubServer` in the middle of the run, waits 600 ms, and starts it again on the same
  port.
- Counts successful requests after the restart and records the injected downtime.
- The harness passes if at least one fault was injected and at least one request completed after
  recovery.

A small number of in-flight requests can survive the stop because Kestrel drains connections; that
is a valid outcome. The important assertion is that the listener rebinds and subsequent heartbeats
succeed.

### `audit`

- Runs concurrent `IAuditService.LogAsync` calls through the same process lock and SQLite database
  used by command, enrollment and administrative audit writes.
- Verifies the full SHA-256 hash chain after the writes.
- Reports rows/second, failures and the chain result.

This is the direct test for cross-process/thread audit serialization. The hub uses `audit.lock` for
separate processes and an in-process semaphore for threads; SQLite connections use
`Default Timeout=30` so a busy writer waits instead of failing immediately.

## 3. Recorded local result

Environment: Windows 11 Pro x64, 8 logical processors, .NET SDK 10.0.403, Debug build, single
machine, loopback HTTPS. Date: 2026-11-16.

| Scenario | Parameters | Result |
| --- | --- | --- |
| Load | 12 agents x 10 heartbeats | 120/120 requests, 0 errors, 82.43 req/s, p50 5.28 ms, p95 483.17 ms, p99 1,265.64 ms, max 1,420.79 ms |
| Fault | 12 agents x 10 heartbeats, one 600 ms outage | 120/120 requests succeeded, 1 fault injected, 48 post-fault successes, 0 client errors, p95 1,721.52 ms |
| Audit | 8 writers x 50 rows | 400/400 rows, 0 errors, 855.20 rows/s, chain verified (402 records including the two setup writes) |

The load p95 is dominated by SQLite write serialization when many synthetic agents update
`LastSeenUtc` concurrently. Real agents use a heartbeat interval of 60 seconds by default, so 12
simultaneous writers is already an aggressive synthetic shape. The harness is a contention probe,
not a claim that the hub will serve hundreds of agents per second.

## 4. Limitations

- All agents share one Windows process and one loopback interface. There is no network latency,
  packet loss, firewall, NAT or switch behaviour.
- Certificate creation is in-memory and short-lived; the harness does not test certificate renewal
  over multiple days or CA replacement.
- The harness does not run the Windows service, the agent collectors, a UI session or the MSI
  installation path.
- A load run can leave SQLite WAL files and a large audit table in the temporary data root. Use
  `--keep-data` only when you intend to inspect them; otherwise the folder is deleted.
- Results vary with CPU, antivirus, disk and the number of synthetic agents. Re-run on the target
  hardware before sizing a deployment.

For the functional test suite and its limitations, see [TESTING.md](TESTING.md).



