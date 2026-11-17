# Architecture

RemoteDesk Hub is a two-process system: a WPF management console that also hosts the hub HTTPS
listener, and a Windows service that runs on every managed computer. There is no cloud component,
no central server other than the console computer, and no shared database between agents. All
state lives in SQLite on the hub computer and in a protected JSON state file on each agent.

This document describes the shape of the code, the request flows, and the reasons behind the
non-obvious decisions.

## 1. Process topology

Published installations contain three executables:

```
Hub computer
  RemoteDeskHub.Desktop.exe     WPF console, per interactive user
  RemoteDeskHub.HubHost.exe     headless hub listener and background service

  %LOCALAPPDATA%\RemoteDeskHub
    remote-desk.db, certs/, logs/, backups/, transfers/, exports/
         ^
         |  https://<hub>:7443, TLS 1.3 + mutual TLS
         v
Managed computer
  RemoteDeskHub.Agent.exe       Windows service, LocalSystem, automatic start
  %ProgramData%\RemoteDesk Agent
    agent-state.json, certs/, logs/, work/
```

The console is the composition root for the UI and login session. When
`RemoteDeskHub.HubHost.exe` is present next to the console (published and installed builds), the
console starts the headless host and connects to it over the private-CA HTTPS listener. When the
executable is absent (normal development output), the console falls back to hosting `HubServer` and
`HubBackgroundService` in-process.

The host owns the database, private CA, Kestrel listener and background scheduler. It writes
`hub-host.port` and `hub-host.pid` into the hub data folder and signals readiness with the named
event `Local\RemoteDeskHub.HubHost.Ready`; the console signals shutdown with
`Local\RemoteDeskHub.HubHost.Stop`. The console leaves an independently started host running when it
exits, and stops a host it started itself.

This split is a **same-session process split**, not a Windows service. The hub CA and TLS private
keys are protected with DPAPI `CurrentUser`, so a `LocalSystem` service cannot decrypt them without a
key-storage redesign. The host therefore runs in the interactive administrator session and stops at
sign-out. A true service split requires moving hub key storage to a service-account-readable,
non-exportable store.

Cross-process SQLite writers are serialized with lock files rather than thread-affine named mutexes:

- `audit.lock` guards the audit hash chain.
- `startup.lock` guards migrations, seeding and staged-restore application.

The agent never opens an inbound connection to the hub. It initiates enrollment, heartbeat and
telemetry; the hub initiates remote administration against the agent's authenticated listener.
## 2. Solution layout

| Project | Target | Responsibility |
| --- | --- | --- |
| `RemoteDeskHub.Shared` | `net10.0` | Protocol DTOs, enums, constants, JSON defaults, `PathGuard`, `NetworkAddressGuard`, protected-process and critical-service lists |
| `RemoteDeskHub.Core` | `net10.0` | Domain entities, view models, service interfaces, `Permissions`, password hashing, credential rules |
| `RemoteDeskHub.Infrastructure` | `net10.0` | EF Core SQLite context and migrations, hub services, reports, audit chain, replay guard, background service, paths |
| `RemoteDeskHub.Networking` | `net10.0` | Private certificate authority, `HubServer` (Kestrel), `AgentClient` (HTTPS transport) |
| `RemoteDeskHub.HubHost` | `net10.0-windows` | Headless hub process: database, CA, listener, background service and cross-process signals |
| `RemoteDeskHub.Agent` | `net10.0-windows` | Worker service, collectors, enrollment client, agent API, command and file services, DPAPI state |
| `RemoteDeskHub.Desktop` | `net10.0-windows` | WPF shell, theme, charts, dialogs, 15 page views and view models, wizard, login, hub-host control |
| `tools/RemoteDeskHub.LoadTest` | `net10.0-windows` | Loopback load, fault and audit-contention harness |

Dependency direction is one-way:

```
Desktop ──> Networking ──> Infrastructure ──> Core ──> Shared
   │            │
   └──> HubHost ┘

Agent ──> Shared
tools/LoadTest ──> Networking, Infrastructure, Core, Shared
```

`Agent` references only `Shared`, so agent-side code cannot accidentally depend on hub services or
the database.

## 3. Hub startup and composition

`App.OnStartup` (`src/RemoteDeskHub.Desktop/App.xaml.cs`) is the composition root:

1. `Host.CreateApplicationBuilder()` builds the DI container.
2. `AddRemoteDeskHubCore()` registers paths, clock, session context, app events, the EF Core context
   factory and every hub service as singletons.
3. `AddRemoteDeskHubNetworking()` registers the certificate authority, `AgentClient`, `HubServer`
   and the `IAgentTransport` / `IAgentConnectionInfoProvider` aliases.
4. The desktop registers `DialogService`, windows, page view models and `HubHostControl`.
5. If `RemoteDeskHub.HubHost.exe` exists next to the console, `HubHostControl` tries to attach to a
   ready host or starts one. It waits for `Local\RemoteDeskHub.HubHost.Ready`, reads
   `hub-host.port`, and uses that HTTPS port. If the executable is absent, the desktop registers
   `HubServer` and `HubBackgroundService` in-process instead.
6. The database initializer applies migrations and seeds defaults under `startup.lock`; this is
   skipped in the console when an already-ready host owns the database.
7. Serilog writes `logs/hub-<date>.log`; the shell loop runs wizard (first run only) → login →
   `MainWindow.ShowDialog()`. Signing out returns to login without restarting the host.
8. On exit, the console stops a host it started itself. An independently started host keeps
   running.

If the hub listener cannot bind - for example the port is in use - the console still opens and shows
a warning. Settings can then be changed and the application restarted.

### Dependency lifecycle

Almost every service is a singleton. SQLite access goes through
`IDbContextFactory<RemoteDeskDbContext>` and each operation creates and disposes its own short-lived
context. This avoids a long-lived `DbContext` being shared across UI threads and background timers,
and it keeps `Cache=Shared;Pooling=True;Default Timeout=30` effective.

`IClock` and `ISessionContext` are abstractions so integration tests can replace time and sign in a
test user without a window.

## 4. Data model

The database is SQLite (`remote-desk.db`, WAL-capable shared cache, connection pooling). EF Core
migrations are applied at startup by `DatabaseInitializer.InitializeAsync`.

| Entity | Purpose | Notable constraints |
| --- | --- | --- |
| `Computer` | One discovered or enrolled machine | Stable Windows `MachineId`; unique agent relationship; indexed by name, IP, status, last seen |
| `Agent` | Installed agent identity and approval state | Unique certificate thumbprint; unique `ComputerId`; `MachineId` used to preserve identity on re-enrollment; cascade from computer |
| `AgentEnrollment` | Single-use pairing token and pending CSR | Unique `TokenHash`; token is never stored in clear text; optional `MachineId` and installation ID for identity matching |
| `ComputerGroup` / `ComputerGroupMembership` | Logical grouping | Unique group name; composite membership key |
| `HardwareInventory` | CPU, RAM, board, BIOS, GPU, disks, adapters | One row per computer |
| `HardwareInventorySnapshot` | Historical hardware configurations | Captured when a meaningful change is detected; readable change summary |
| `SoftwareInventoryItem` | Installed software | Indexed by computer/name and computer/presence |
| `PerformanceMetric` | Time-series samples | Composite index on computer + timestamp |
| `Alert` / `AlertRule` | Active alerts and threshold rules | `DedupeKey`; active + created index |
| `AuditLog` | Hash-chained audit entries | `PrevHash` → `Hash`; indexed by timestamp and operation |
| `CommandHistory` | Command execution results | Actor, shell, exit code, stdout/stderr |
| `CommandApprovals` | Two-administrator command approvals | Status/expiry indexes; exact command, computer, shell, timeout and elevation binding |
| `FileTransfer` | Upload/download history | Direction, status, SHA-256, byte counts |
| `RemoteSession` | Remote assistance session records | Tool, target, operator, process ID, status, start/end and duration |
| `AutomationTask` / `AutomationExecution` | Scheduled work and run history | Trigger/action JSON, next-run index |
| `ApplicationSetting` | Typed key/value settings | Key is the primary key |
| `UserAccount` | Local console users | Unique username; PBKDF2 hash + salt + iteration count |
| `DiscoveryDevice` | Network scan results | Unique IP address |
| `RemoteAssistanceProfile` | Saved launch profiles | Tool and arguments |
| `SchemaVersion` | Human-readable schema marker | Single row in practice |

All `DateTime` properties are stored as `TEXT` (set explicitly in `OnModelCreating`). SQLite has no
native date type; storing ISO text keeps ordering and range queries correct and avoids provider
round-trip surprises. Audit timestamps are normalised to UTC ticks before hashing.

Deleting a computer cascades to its agent, inventory and metrics. Alerts use `SetNull` so alert
history survives computer removal.

## 5. Hub request pipeline

`HubServer.StartAsync` builds a slim Kestrel application:

- HTTPS on the configured bind address and port (default `0.0.0.0:7443`).
- TLS 1.3 only (`SslProtocols.Tls13`).
- HTTP/1.1 and HTTP/2.
- Server certificate from the private CA; client certificates are requested but optional at the TLS
  layer because enrollment happens before an agent has one.
- Client certificate validation uses `X509ChainTrustMode.CustomRootTrust` against the hub CA, with
  expiry checks inherent in chain building.
- No `Server` header; 4 MiB request body limit; 15 s header timeout; 2 min keep-alive.

### Authorization middleware

After rate limiting and WebSockets, one middleware classifies the path:

| Path | Requirement |
| --- | --- |
| `/api/health`, `/api/enrollment/*` | Anonymous |
| `/api/hub/*`, `/ws/live` | Client certificate thumbprint must equal the hub console certificate |
| `/api/agent/*` | Certificate must be bound to an `Agent` row whose state is not `Revoked` or `Rejected` |
| anything else | `404` |

The middleware also puts `agentId`, `computerId` and `certificateThumbprint` into `HttpContext.Items`
so endpoints do not re-query identity from headers. A request cannot claim to be another agent by
changing a body field: `ProcessHeartbeatAsync` compares the DTO identity with the certificate-bound
record.

### Endpoints

| Method | Path | Notes |
| --- | --- | --- |
| `GET` | `/api/health` | Anonymous liveness probe |
| `GET` | `/api/hub/info` | Product, version, port, server/client thumbprints, CA PEM |
| `POST` | `/api/enrollment/request` | Token + CSR → pending or approved agent |
| `GET` | `/api/enrollment/status/{id}` | Agent polls approval state; token in `X-Enrollment-Token` (query fallback for older agents) |
| `POST` | `/api/agent/heartbeat` | Identity check, metrics, config, revocation |
| `POST` | `/api/agent/telemetry` | Time-series samples |
| `GET` | `/api/agent/config` | Runtime configuration |
| `POST` | `/api/agent/renew` | Authenticated certificate renewal; replaces the pinned thumbprint and revokes the old certificate |
| `GET` | `/ws/live` | WebSocket push of change notifications |

Rate limits are fixed-window per minute: 30 enrollment requests per source IP, 120 enrollment
status requests per token, and 1200 agent requests per certificate thumbprint with a queue of 20.
Rejections return `429`.

## 6. Agent architecture

`Program.Main` builds an `IHost` with Windows Service lifetime support and a console mode
(`--console`) for diagnostics. `AddWindowsService` sets the service name `RemoteDeskAgent`. The
agent runs `asInvoker`; it never tries to elevate itself. The MSI registers it as a `LocalSystem`
service through the Windows Installer service table, which is what allows it to read service state
and perform administrative operations.

### State and secrets

`AgentStateStore` persists `agent-state.json` atomically (write temp file, replace). The file holds:

- `AgentId`, `ComputerId`, installation ID, hub URL, management port
- CA certificate PEM, pinned hub TLS server thumbprint and pinned hub console client thumbprint
- DPAPI-protected PFX containing the agent certificate and private key
- DPAPI-protected pending enrollment token and private key

DPAPI uses `LocalMachine` scope with fixed entropy `RemoteDesk.Agent.State.v1`, because the service
account must be able to read it after a reboot and a service account change. Hub-side keys use
`CurrentUser` scope because the hub runs as the interactive administrator. When the process is
elevated, `AgentOptions.EnsureDirectories` also resets the data-folder DACL to SYSTEM and local
Administrators (inheritance disabled) and removes `Everyone`, `Authenticated Users` and `Users`
grants, so standard local users cannot read the machine-scope state file.

### Lifecycle

`AgentWorker.ExecuteAsync`:

1. Enroll with exponential back-off (15 s doubling to 5 min). Outcomes are enrolled, awaiting
   approval, rejected, no pairing package, or transient failure.
2. Load identity and certificate. If the certificate expires within 30 days, request a renewal from
   the hub instead of waiting for a restart.
3. Start the management API on `0.0.0.0:<managementPort>` (default 7444).
4. Run heartbeat and telemetry loops concurrently until cancellation, revocation or a successful
   renewal.
5. On heartbeat `403` or a `Revoked` response, mark state revoked and stop the API.
6. After a successful renewal, stop the listener, replace the in-memory certificate and reload the
   agent from state; the hub atomically updates the pinned thumbprint and rejects the old
   certificate.

Renewal requests are authenticated with the current agent certificate, checked against the bound
agent and machine name, checked for replay, and issued only after the CSR is validated by the same
CA that strips CSR-supplied SAN/EKU/KU extensions.

Heartbeats carry a fresh 16-byte nonce and a UTC timestamp. Telemetry batches carry a 24-byte nonce.
Both are checked by the hub replay guard.

### Agent API

The agent listener requires a client certificate at the TLS layer. `ValidateHubClient` accepts only
the exact pinned hub console thumbprint, validates the chain against the pinned CA, checks the
clientAuth EKU, and rejects everything else. Non-private source addresses are rejected before
routing, and the listener is rate limited to 600 requests per minute per certificate.

Endpoints cover info, metrics, hardware/software inventory, processes, services, command execution
and cancellation, event logs, and the file operations used by transfers. Command and file services
enforce their own guard rails in addition to the hub-side checks (see section 8).

## 7. Key flows

### Enrollment

```
Agent                                   Hub
  │  POST /api/enrollment/request        │
  │  {token, CSR, installationId, ...}   │
  │─────────────────────────────────────►│  hash token, check expiry/single use
  │                                      │  check source IP is private
  │                                      │  verify CSR public key matches
  │                                      │  create/refresh AgentEnrollment (Pending)
  │  202 {enrollmentId, status}           │
  │◄─────────────────────────────────────│
  │  GET /status/{id} + X-Enrollment-Token │
  │─────────────────────────────────────►│
  │  {approved:false}                    │  administrator approves in Computers page
  │  ... poll ...                        │  CA signs CSR → certificate PEM stored
  │  {approved:true, certificatePem,     │
  │   caPem, hubServerThumbprint, hubClientThumbprint, port}  │
  │◄─────────────────────────────────────│
  │  persist state, start API            │
```

Design points:

- The token is 256 bits from `RandomNumberGenerator`, returned once in the pairing package, and
  stored only as a SHA-256 hash.
- Retrying with the same `InstallationId` and token returns the same enrollment instead of consuming
  a second token. This makes MSI retries and service restarts safe.
- The CSR's public key must match the submitted `PublicKeyPem`; the hub will not sign an arbitrary
  key.
- Automatic approval is a per-token option (`AutoApprove`) and defaults to off.
- `AgentEnrollment.CertificatePem` is the issued certificate; the private key never leaves the agent.

### Heartbeat and telemetry

`AgentLifecycleService.ProcessHeartbeatAsync` resolves the agent by certificate thumbprint, rejects
identity mismatches, validates nonce/timestamp through `ReplayGuard`, updates `Computer` health and
last-seen fields, records a metric sample, evaluates alert rules, and returns runtime config plus
revocation state. A replay throws `ReplayDetectedException`, mapped to HTTP 409.

Telemetry is best-effort. A replayed or malformed batch is rejected with HTTP 400 and does not
break the heartbeat loop.

### Monitoring and alerts

`MonitoringService` stores samples in `PerformanceMetrics`, updates the computer's current CPU,
memory, disk and temperature values, and evaluates enabled `AlertRules`. Alerts deduplicate by
`{computerId}:{metricType}`. When the metric recovers the existing alert is resolved rather than a
new one being created. Default thresholds from Settings are applied to the global rules by
`ApplyDefaultThresholdsAsync`.

Offline detection is not driven by the UI. `HubBackgroundService` calls
`RecordOfflineStatusesAsync` every 30 seconds; a computer whose last heartbeat is older than the
configured grace period becomes `Offline` and raises an alert once.

### Remote command

```
CommandsViewModel -> CommandService
  -> RBAC (RunCommands)
  -> validate command text and shell
  -> block critical patterns unless exact allowlist match
  -> require confirmation for destructive patterns
  -> if approval policy is enabled:
       create CommandApproval (15 min), audit, stop
       second administrator approves in Approval queue
  -> AgentClient POST /api/agent/execute
      -> CommandRunner (PowerShell or cmd.exe)
  -> store CommandHistory + audit entry
  -> mark approval Executed/Failed
```

The agent enforces its own maximum timeout and returns stdout, stderr, exit code and duration. The
hub records the attempt even when the agent call fails, so failed administration is auditable.

### File transfer

Uploads and downloads are chunked through the agent file API, with SHA-256 computed at both ends and
verified at completion. The hub enforces `AgentAllowedRoots`; the agent independently normalises and
validates paths, rejects traversal, blocks protected locations, and requires a confirmation flag for
rename/delete. Transfers are recorded in `FileTransfers` with byte counts and status.

### Shared folder

The shared folder is deliberately separate from the general file-transfer service. `C:\zzz` is a
dedicated root, and the hub appends the configured `sharedFolder.path` to the agent's runtime allowed
roots only for the shared-folder manifest and transfer calls. The default `AgentAllowedRoots` setting
does not include it.

`SharedFolderSyncService` is a hosted background service in both `RemoteDeskHub.HubHost` and the
in-process desktop host. Each cycle:

1. Scans the hub-local `C:\zzz` with `SharedFolderScanner`, which hashes files by size and last-write
   time, skips reparse points and `.rdh-sync`, and reports unreadable or oversized paths as skipped.
2. Reconciles the local manifest against `SharedFolderLocalFiles` and `SharedFolderFiles` using the
   pure `SharedFolderPlanner`. The planner returns uploads, pushes, tombstone writes, conflict renames
   and row removals.
3. Pulls manifests from active, approved agents over the pinned mutual-TLS API. The hub excludes an
   agent whose `MachineName` equals `Environment.MachineName`, because the hub-local peer already
   manages that directory.
4. Applies the same planner to each remote manifest, streams file contents through the agent file
   API, verifies SHA-256, and updates `SharedFolderAgentFiles`. A second local pass makes files pulled
   from agents visible in the hub folder during the same cycle.
5. Records a summary in `SharedFolderSyncState` and writes conflict/deletion events to the audit
   chain.

Central blobs are content-addressed under `shared-folder/blobs/<SHA-256>`. Agent writes stage under
`C:\zzz\.rdh-sync`. Simultaneous edits keep the central version and rename the peer copy to
`name (conflict from MACHINE yyyyMMdd-HHmmss).ext`; that copy is uploaded as a new file on the next
scan. Deletions use tombstones and only win when the central file has not changed since that peer's
last observation. See [SHARED_FOLDER.md](SHARED_FOLDER.md).

### Discovery

`DiscoveryService.ScanAsync` builds a target list from explicit ranges or local interfaces, caps the
scan at 4096 hosts, probes each address with a bounded concurrency (default 24), and records
responding devices in `DiscoveryDevices`. Probes use the configured ports (default agent port, 445,
3389) and attempt an HTTPS agent identity check where possible. Results are applied sequentially so
duplicate detection is deterministic. Scans are read-only and require `ViewComputers`.

### Automation

`AutomationService.TickAsync` looks for enabled tasks whose `NextRunUtc` is due, executes their
action (command, script, report or maintenance action) against the configured target computer or
group, records an `AutomationExecution`, and computes the next run from the interval or daily time.
The background service invokes it every 30 seconds. Manual runs use the same code path and are
recorded with `TriggeredBy = "User"`.

### Audit chain

Every privileged service call writes an `AuditLog` row. Each row contains the previous row's hash;
the hash is SHA-256 over the canonical fields including the UTC-normalised timestamp. `VerifyAsync`
recomputes the chain and reports the first broken entry. Retention is controlled by
`security.auditRetentionDays`; purging removes expired rows and appends an `Audit.Purged` entry so
the removal itself is recorded.

## 8. Guard rails

These are enforced in code, not only in the UI:

| Guard | Location | Behaviour |
| --- | --- | --- |
| Protected processes | `ProcessService`, `ProtectedProcesses` | Names such as `lsass`, `csrss`, `winlogon`, `services`, `svchost` and `System` cannot be terminated |
| Critical services | `ServiceManagerService`, `CriticalServices` | `RpcSs`, `DcomLaunch`, `Winmgmt`, `EventLog`, `MpsSvc` cannot be stopped even with confirmation |
| Destructive confirmation | Services, file transfer, commands | The operation is rejected unless `confirmed = true` |
| Critical command blocklist | `CommandService`, `CommandSafety` | `format`, `diskpart`, boot configuration, event-log clearing, service deletion and similar commands are refused before transport unless the exact command is in `security.commandAllowlist`; refused attempts are audited |
| Two-administrator approval | `CommandService`, `CommandApprovals` | When enabled, destructive or critical commands create a 15-minute request; a different administrator must approve; execution is bound to the exact request and marked `Executed` or `Failed` |
| Transcript integrity | `CommandService` | Command audit details include SHA-256 hashes and lengths of stdout/stderr; the full transcript remains in `CommandHistory` |
| File roots | `PathGuard`, `FileTransferService`, agent file service | Paths must be absolute local drive paths inside configured roots; traversal and protected system locations are rejected |
| Elevation | `CommandService` | Commands run in the service context; the UI states "Runs as LocalSystem" and never elevates the console |
| RBAC | `AccessGuard.Require` | Every service entry point checks a permission; `SYSTEM` (background) bypasses |

The UI repeats these checks to give immediate feedback, but the service layer is authoritative.

## 9. Concurrency and background work

- UI work is asynchronous; view models use `CommunityToolkit.Mvvm` commands and `CancellationToken`
  for page loads.
- Bulk operations are bounded with `SemaphoreSlim(4)` so a large group does not open hundreds of
  HTTPS connections at once.
- `AgentClient` caches one `HttpClient` per computer/host and sets pooled connection lifetime to
  5 minutes. Timeouts are per operation: 30 s default, 90 s hardware inventory, 3 min software
  inventory, command timeout + 90 s.
- Discovery uses a concurrency gate and caps the target list at 4096 addresses.
- `ReplayGuard` is a bounded, self-pruning in-memory dictionary: nonces are retained for 10 minutes
  and timestamps must be within ±5 minutes. A hub restart clears it, which is acceptable because
  heartbeat payloads are idempotent and identity is still certificate-checked.
- `HubBackgroundService` starts after 10 seconds and then ticks every 30 seconds. Offline evaluation
  and automation are isolated in separate try/catch blocks so one failure does not stop the other.
- Cross-process writers share SQLite through `audit.lock` (audit hash chain) and `startup.lock`
  (migrations, seeding and staged restore). SQLite connections use WAL, shared cache, pooling and a
  30-second default busy timeout. The audit service also serializes threads with a semaphore.

## 10. Security boundaries

| Boundary | Trust decision |
| --- | --- |
| LAN | Trusted transport scope, but not trusted identity: every agent still needs a certificate and approval |
| Hub → agent | The hub authenticates with the console client certificate; the agent pins its thumbprint |
| Agent → hub | The hub validates the agent certificate against the private CA and the agent record |
| Console user | Local account with a role; every operation re-checks a permission |
| Enrollment | Token + LAN source + CSR key match + administrator approval |
| At rest | DPAPI for private keys and agent state; PBKDF2 for passwords; no secrets in logs |

The private CA is not written to the Windows certificate store. There is no CRL or OCSP endpoint;
revocation is a database state checked on every request. Agent certificates renew automatically
through `/api/agent/renew`; the old thumbprint is replaced at issuance, so the old certificate stops
working on its next request.

## 11. Design decisions and trade-offs

**SQLite instead of a server database.** The product targets a single management computer. SQLite
removes a service dependency, keeps backup to one file, and is fast enough for the expected scale
(thousands of metric rows per computer per day, pruned by retention). The cost is that multiple
console users on different Windows accounts have separate databases; this is documented rather than
hidden.

**The hub is a desktop app, not a Windows service.** The console and listener share one process so
there is no second installable component and no IPC protocol. The trade-off is that the listener is
only available while the console process runs. For a management workstation this is acceptable; a
future server edition would move `HubServer` into a service and keep WPF as a client.

**Private CA instead of Active Directory or public PKI.** A private CA works on workgroups and
small domains without enterprise infrastructure. It also allows the agent to pin the hub console
certificate, which is stronger than trusting any certificate from a public root.

**Approval by default.** Automatic enrollment would make deployment easier but would let any
computer with a leaked package join. The default is manual approval; auto-approval is opt-in per
token and audited.

**Agent as LocalSystem.** Remote service control, event log access and process termination need
administrative rights. Rather than prompting for elevation, the MSI installs a service with the
minimum fixed account that can do the job. The agent still refuses protected targets.

**In-memory replay cache.** A database-backed nonce table would survive restarts but adds a write per
heartbeat. The chosen cache is bounded and cheap; the residual restart window is covered by
certificate identity and idempotent payloads.

**Reports in memory.** `ReportWriter` renders CSV, HTML and PDF into a byte array and the UI saves it
through a file dialog. This avoids temporary files with sensitive data and keeps export paths under
the user's control.

## 12. Deliberate gaps

These are known limitations of version 1.0.0, not hidden behaviour:

- No multi-user server mode; each Windows account has its own hub database.
- No hub certificate rotation UI; agent certificates renew automatically, but the hub TLS and console
  certificates still require a controlled reinstall or CA replacement.
- The headless hub host runs in the interactive session, not as a Windows service, because hub
  private keys use DPAPI current-user scope.
- Two-administrator command approval is opt-in (`security.commandApprovalRequired`) and requires
  two administrator accounts because self-approval is rejected. It is off by default; critical
  commands are still blocklisted unless an exact allowlist entry exists.
- Remote Assistance launches the selected operator-side tool and records prepared, started, failed
  and ended states, process ID and duration. It does not embed a screen viewer or store credentials.
  Consent is policy-driven (`security.remoteConsentRequired`, default off).
- Inventory is collected on demand, not on a background schedule.
- Multi-machine and physical-LAN behaviour was not tested; see [TESTING.md](TESTING.md).
- Load, fault and audit-contention results are single-machine loopback measurements; see
  [LOAD_TESTING.md](LOAD_TESTING.md).
- The MSIs are not code-signed in this repository.




