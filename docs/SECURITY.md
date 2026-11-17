# Security

RemoteDesk Hub is designed for a trusted private LAN. It does not use a cloud service, a shared
password, or a self-signed certificate that is trusted globally. Every management connection is
mutually authenticated with certificates issued by a private certificate authority (CA) that is
created on the hub computer.

This document describes what the implementation actually enforces, and where its limits are.

## 1. Security model at a glance

| Control | Implementation |
| --- | --- |
| Transport | HTTPS with TLS 1.3 only on both hub and agent listeners |
| Hub identity | Certificate issued by the private CA; agents pin both the hub TLS server thumbprint and the hub console client certificate thumbprint |
| Agent identity | Unique certificate per installation, issued from an agent-generated CSR; hub pins the thumbprint to the agent record and ignores CSR-supplied SAN/EKU values |
| Enrollment | Single-use 256-bit token, LAN source check, CSR public-key match, administrator approval by default; the status token is sent in `X-Enrollment-Token`, not the URL |
| Authorization | Role-based permissions checked in every service call and used to filter navigation |
| Passwords | PBKDF2-SHA256, 210,000 iterations, 16-byte salt, 32-byte hash |
| Lockout | 5 failed attempts, 5-minute lockout, reset on success |
| Secrets at rest | Windows DPAPI; hub keys per Windows user, agent keys at machine scope; pending enrollment tokens are DPAPI-protected and the agent data folder is ACL-restricted to SYSTEM/Administrators |
| Audit | Append-only SHA-256 hash chain, verified from the Audit Log page |
| Replay resistance | Per-agent nonce cache plus clock-skew checks on heartbeat and telemetry |
| Destructive actions | Explicit confirmation in the console and again at the agent for service/file operations; critical commands are blocked before transport unless an administrator allowlists the exact command; command audit details include transcript hashes |
| Network scope | Private IPv4 discovery and enrollment only; firewall rules scoped to the local subnet |

## 2. Identities and certificates

The hub creates a private CA on first run. It is stored under the hub data directory
(`%LOCALAPPDATA%\RemoteDeskHub\certs` by default) and is never written to the Windows certificate
store.

| Object | Key | Lifetime | Extensions | Used by |
| --- | --- | --- | --- | --- |
| Root CA | RSA-4096 | 10 years | `CA:TRUE`, keyCertSign, cRLSign, digitalSignature | Signs hub and agent certificates |
| Hub TLS certificate | RSA-2048 | 2 years | serverAuth; SAN `localhost`, machine name, `<machine>.local`, loopback and local IPv4 addresses | Hub Kestrel listener |
| Hub console client certificate | RSA-2048 | 2 years | clientAuth | Hub to agent requests and privileged hub endpoints |
| Agent certificate | RSA-2048 (agent-generated CSR) | 1 year | clientAuth and serverAuth; SAN machine name and `<machine>.local` only (CSR SAN/EKU/KU stripped) | Agent heartbeat, telemetry and management API |

Certificate files:

```
certs/ca.crt.pem             CA certificate (public)
certs/ca.key.dat             CA private key, DPAPI-protected
certs/hub-server.crt.pem     Hub TLS certificate
certs/hub-server.key.dat     Hub TLS private key, DPAPI-protected
certs/hub-client.crt.pem     Hub console client certificate
certs/hub-client.key.dat     Hub console private key, DPAPI-protected
```

Hub private keys are protected with DPAPI **current-user** scope. The hub data directory is also
under the current user's `%LOCALAPPDATA%`, so a different Windows account on the same computer does
not automatically gain access. Agent private keys are protected with DPAPI **LocalMachine** scope
because the agent runs as `LocalSystem`.

### Validation rules

- Hub and agent listeners trust only the private CA (`CustomRootTrust`), not the Windows root store.
- Certificate expiry is checked on every validation.
- The hub can require the serverAuth EKU when it validates an agent's TLS server certificate.
- The agent requires the exact pinned thumbprint of the hub console client certificate. A different
  certificate from the same CA is rejected.
- The hub checks that a heartbeat's `AgentId` and `ComputerId` match the agent record bound to the
  presented certificate.
- Revocation is enforced in the database, not with a CRL or OCSP responder. A revoked agent's
  certificate remains cryptographically valid, but the hub rejects its requests and the agent's
  heartbeat receives `Revoked = true`.

Agent certificates renew automatically. The agent sends an authenticated
`POST /api/agent/renew` with a fresh CSR, its current certificate, agent ID and machine name before
the certificate expires (the worker renews when less than 30 days remain). The hub checks the
certificate-bound agent record, machine name and replay nonce, issues a replacement from the same
CA, replaces `CertificateThumbprint` atomically and audits the renewal. The old certificate is
rejected by the authorization middleware on its next request. Hub certificates still require a
controlled reinstall or CA replacement before their two-year expiry.

The hub CA and TLS private keys are protected with DPAPI **current-user** scope. This is why the
headless hub host runs in the interactive administrator session rather than as a `LocalSystem`
service; a service account cannot decrypt these keys. Moving to a service requires a
service-account-readable key store, preferably a non-exportable CNG key. This limitation is
explicit rather than hidden.

## 3. TLS and endpoint authorization

### Hub listener

- TLS 1.3 only.
- HTTP/1.1 and HTTP/2.
- Client certificates are requested but not required at the TLS layer, because enrollment must work
  before an agent has a certificate.
- The server header is disabled.
- Request body limit: 4 MB. Header timeout: 15 seconds. Keep-alive: 2 minutes.
- Fixed-window rate limits:
  - `/api/enrollment/request`: 30 requests per minute per source IP.
  - `/api/enrollment/status/{id}`: 120 requests per minute per enrollment token (or source IP when
    no token is supplied), which avoids throttling multiple agents behind one NAT address.
  - `/api/agent/*` and `/ws/live`: 1,200 requests per minute per certificate thumbprint (or source
    IP when no certificate is presented), with a queue of 20.
- Rate-limit rejections return HTTP 429.

Endpoint authorization:

| Endpoint group | Requirement |
| --- | --- |
| `/api/health` | Anonymous, HTTPS only |
| `/api/enrollment/request`, `/api/enrollment/status/{id}` | Anonymous, HTTPS only, private source address, valid token |
| `/api/hub/*`, `/ws/live` | Exact thumbprint of the hub console client certificate |
| `/api/agent/*` | Certificate registered to an agent in `Active` or `PendingApproval` state; revoked and rejected agents are refused |

### Agent listener

- Binds the agent management port (default 7444) on all interfaces, but rejects requests from
  non-private source addresses as a second line of defence behind the firewall rule.
- TLS 1.3 only; client certificate is **required**.
- The presented client certificate must chain to the CA distributed in the pairing package and must
  match the pinned hub console thumbprint exactly.
- Request body limit: 16 MB. Header timeout: 20 seconds. Keep-alive: 2 minutes.
- Rate limit: 600 requests per minute per client thumbprint, queue of 20.
- The agent's own server certificate is presented to the hub and validated against the pinned agent
  thumbprint by the hub client.

### Outbound clients

Hub-to-agent and agent-to-hub clients offer TLS 1.2 and TLS 1.3. In practice both peers are the
product's own listeners, which enforce TLS 1.3, so TLS 1.2 is only a compatibility floor for the
client stack. Both clients validate the remote certificate against the private CA, require the
correct EKU and pin the expected thumbprint. The agent pins the hub **server** thumbprint for
heartbeat/telemetry and the hub **console client** thumbprint for inbound management calls, so a
CA-issued agent certificate cannot impersonate the hub.

## 4. Enrollment and approval

1. An administrator creates a pairing package on **Computers > Agent enrollments**.
2. The package (`.rdhenroll`) contains:
   - hub URL and agent port,
   - a single-use enrollment token,
   - the CA certificate PEM,
   - the hub TLS server certificate thumbprint,
   - the hub console client certificate thumbprint,
   - expiry and an optional note.
   It never contains a private key. Format v2 requires the hub server thumbprint; an older package
   without it is rejected so the agent cannot fall back to trusting any CA-issued certificate.
3. The token is a 256-bit URL-safe random value. Only its SHA-256 hash and a masked preview are
   stored. The clear token is shown once and written into the package.
4. The agent generates an RSA key pair and CSR locally, then submits the CSR, public key, machine
   name, installation ID, nonce and token.
5. The hub:
   - rejects non-private source addresses,
   - requires the CSR public key to match the submitted public key,
   - looks up the token by hash,
   - rejects expired, rejected, cancelled or already-used tokens,
   - allows an idempotent retry only for the same `InstallationId`,
   - creates the computer and agent records,
   - issues the certificate automatically only when the token or global policy enables
     auto-approval; otherwise the request stays pending.
6. An administrator approves or rejects pending requests. Approval signs the CSR and returns the
   agent certificate, CA certificate and hub certificate information via the status endpoint.
7. Revoking an agent sets its state to `Revoked` and records a reason. The certificate thumbprint is
   rejected by the hub middleware immediately. Re-enrollment requires a new pairing package.

Default policy is `AgentAutoApprove = false`. Keep it false unless the deployment has another
approval mechanism.

## 5. Authentication and roles

Local accounts only. There is no domain authentication, LDAP, or cloud identity provider.

Password rules:

- 10 to 256 characters.
- At least one upper-case letter, one lower-case letter and one digit.
- PBKDF2-SHA256, 210,000 iterations, random 16-byte salt, 32-byte derived key.
- Verification uses `CryptographicOperations.FixedTimeEquals`.
- Five consecutive failures lock the account for five minutes. A successful sign-in resets the
  counter.
- The last enabled administrator cannot be disabled or demoted.
- Passwords and password hashes are never written to logs, audit details or reports.

Sessions live only in memory for the lifetime of the console process. Signing out returns to the
login window. The console also signs out after the configured idle timeout (default 60 minutes,
`0` disables it).

### Permission matrix

| Permission | Viewer | Operator | Administrator |
| --- | :---: | :---: | :---: |
| View dashboard | yes | yes | yes |
| View computers | yes | yes | yes |
| View monitoring | yes | yes | yes |
| View reports | yes | yes | yes |
| Export data | yes | yes | yes |
| View audit log | no | yes | yes |
| Run commands | no | yes | yes |
| Approve commands | no | no | yes |
| Manage processes | no | yes | yes |
| Manage services | no | yes | yes |
| Transfer files | no | yes | yes |
| Delete remote files | no | no | yes |
| Launch remote assistance | no | yes | yes |
| Manage groups | no | no | yes |
| Manage automation | no | yes | yes |
| Manage alerts | no | yes | yes |
| Manage computers | no | no | yes |
| Enroll agents | no | no | yes |
| Manage settings | no | no | yes |
| Manage users | no | no | yes |
| Backup database | no | no | yes |

Authorization is enforced twice:

- Navigation hides pages the current role cannot use.
- Every service method calls `AccessGuard.Require(session, permission, actor)`. A missing or
  insufficient session throws; the actor `SYSTEM` is reserved for internal background operations.

The agent also enforces its own policy: remote commands can be disabled globally
(`AgentAllowCommands`), and file paths are restricted to configured roots.

## 6. Replay protection and request integrity

Authenticated agent requests carry a nonce and a timestamp. The hub keeps a bounded in-memory cache:

- Heartbeat: one acceptance per `(agent, nonce)` for 10 minutes. Duplicate nonces return HTTP 409.
- Telemetry: one acceptance per `(agent, nonce)` for 10 minutes. Duplicate batches are rejected with
  HTTP 400.
- Timestamps more than 5 minutes away from the hub clock are rejected.
- The cache self-prunes once per minute and is bounded by the number of active agents and requests
  in the retention window.

Enrollment uses a different mechanism: the token is single-use, and the same installation may retry
idempotently so a dropped response does not strand an agent. Reusing the token from a different
installation is rejected. Status polling sends the token in the `X-Enrollment-Token` header; the
query-string form is accepted only for older agents and should be avoided because URLs can be logged
by proxies and servers.

Commands carry a correlation ID, issuing actor and a two-minute expiry checked by the agent.
Metrics samples are timestamped by the hub when ingested, so a replayed telemetry batch cannot
backdate history.

The replay cache is process memory. A hub restart clears it. This is acceptable for idempotent
heartbeat state, but it is not a substitute for an external nonce service.

## 7. Audit trail

Every privileged operation writes an `AuditLog` row:

- timestamp (UTC),
- actor,
- operation (for example `Agent.Approved`, `Remote.CommandExecuted`, `Remote.CommandApprovalApproved`, `File.Deleted`),
- computer name and ID,
- result and duration,
- error and details,
- source IP where relevant.

Each row stores `PrevHash` (the previous row's hash) and `Hash`, a SHA-256 over a canonical string
of timestamp, actor, operation, computer name, result, duration, error, details and `PrevHash`.
Timestamps are normalized to a UTC round-trip format before hashing so SQLite's loss of
`DateTimeKind` does not break verification.

**Verify hash chain** recomputes every row and checks every link. Any modification, insertion or
middle deletion is reported with the affected record number.

Purging is explicit. **Purge old** on the Audit Log page:

- reads `security.auditRetentionDays` (default 365, clamped 1–3650),
- deletes rows older than the cutoff,
- appends an `Audit.Purged` row with the new chain tail,
- leaves the remaining chain verifiable.

Purging the oldest rows removes the anchor for those historical actions. Export audit data before
purging if the history must be retained.

Audit writes never throw into the calling operation. A database failure while auditing is logged as
an error by Serilog and the operation continues. This is deliberate availability behavior, but it
means the audit log is not a transactional journal. A single-instance mutex
(`Local\RemoteDeskHub.Desktop`) prevents two local consoles from appending concurrently. The audit
chain is also guarded by `audit.lock`, a cross-process file lock, and an in-process semaphore so the
headless host and a console cannot interleave chain writes. The database uses WAL, shared cache,
pooling and a 30-second busy timeout; a concurrent writer therefore waits instead of failing
immediately.

## 8. Remote administration guard rails

### Commands

- Require `RunCommands`.
- Maximum 8,000 characters, 5-3,600 second timeout.
- Commands run through PowerShell or `cmd.exe` under the agent's `LocalSystem` identity.
- The console always labels this identity; there is no self-elevation prompt.
- Critical patterns (`format`, `diskpart`, boot configuration, event-log clearing, service deletion,
  writes to the run key, broad account/administrator changes and similar) are **blocked before any
  agent request** when `security.commandBlocklistEnabled` is true. An administrator must add the
  exact, whitespace-normalised command to `security.commandAllowlist` to permit it. A blocked attempt
  is audited as `Remote.CommandBlocked` with result `Blocked`.
- Potentially destructive patterns (delete, service control, shutdown, registry deletion, account
  changes, `taskkill`, execution-policy changes, `Invoke-Expression`, DISM, WMIC and similar) require
  explicit confirmation before execution.
- When `security.commandApprovalRequired` is true, a destructive or critical command creates a
  pending approval valid for 15 minutes. A **different** administrator with `ApproveCommands` must
  approve it, and the requester then runs it from the approval queue. Self-approval is rejected, so a
  deployment that enables this policy needs at least two administrator accounts. Request, approval,
  rejection and execution are audited (`Remote.CommandApproval*`). The approval is bound to the
  exact computer, command, shell, timeout and elevation setting, cannot be reused, and is marked
  `Executed` or `Failed`.
- Every command and its output (truncated at 512 KB) is recorded in command history. The audit row
  includes SHA-256 hashes and lengths of stdout and stderr so transcript replacement can be detected.
- Cancellation is sent to the agent and audited.

### Processes

- Require `ManageProcesses`.
- Protected names cannot be terminated: `system`, `idle`, `csrss`, `wininit`, `winlogon`, `services`,
  `lsass`, `smss`, `svchost`, `fontdrvhost`, `dwm`, `memory compression`, `registry`.
- Termination requires explicit confirmation and is audited.

### Services

- Require `ManageServices`.
- Critical services cannot be stopped or restarted at all: `RpcSs`, `RpcEptMapper`, `DcomLaunch`,
  `Winmgmt`, `EventLog`, `Schedule`, `BFE`, `MpsSvc`, `LanmanServer`, `LanmanWorkstation`, `Dhcp`,
  `Dnscache`, `nsi`, `CryptSvc`, `TrustedInstaller`, `WSearch`, `Power`, `PlugPlay`, `ProfSvc`,
  `Themes`.
- Stopping or restarting any other service requires confirmation.
- Startup-type changes are validated at the agent (`Automatic`, `AutomaticDelayedStart`, `Manual`,
  `Disabled`).
- All service actions are audited.

### Files

- Require `TransferFiles`; deletion additionally requires `DeleteRemoteFiles`.
- The agent rejects paths that are not absolute local drive paths, contain invalid characters, are
  UNC/network paths, or fall inside protected locations (`System Volume Information`,
  `$Recycle.Bin`, `Recovery`, `Boot`, `EFI`).
- Paths must resolve inside one of the configured permitted roots. The default is
  `C:\Users;C:\ProgramData`.
- Reparse points (junctions and symbolic links) are refused when detected.
- Delete, rename, overwrite and recursive operations require confirmation.
- Uploads are written to a temporary file and finalized after the complete transfer; file hashes are
  available for verification.
- Every transfer, delete and rename is audited.

### Shared folder

- `C:\zzz` is a dedicated root, not a generic file-transfer root. The default `AgentAllowedRoots`
  remains `C:\Users;C:\ProgramData`; the agent runtime appends the configured shared-folder path only
  for the manifest and file calls used by synchronization.
- Manifests are read-only and contain relative paths only. The hub resolves every entry with
  `PathGuard.TryResolveRelativeWithinRoot`; absolute paths, traversal, alternate data streams,
  reserved device names, trailing dots/spaces and `.rdh-sync` are rejected.
- The scanner skips reparse points and never follows junctions. Agent writes stage under
  `C:\zzz\.rdh-sync` and are finalized only after SHA-256 verification.
- File size and count limits are enforced at both ends. A peer with unreadable or oversized paths is
  reported as skipped rather than treated as having deleted those files.
- Conflict copies and deletion tombstones are audited. Simultaneous edits never silently discard the
  losing bytes; they preserve them as a separately named file.
- The hub applies a modify ACL for `BUILTIN\Users` only when it creates the folder. An existing
  `C:\zzz` keeps its ACL. Any local user who can write to the folder can introduce files that
  synchronize to every managed computer; treat the folder as a shared, low-trust drop location and
  restrict its ACL where that is not acceptable.
- Shared-folder contents are not encrypted at rest beyond NTFS/BitLocker. Do not place secrets in
  `C:\zzz`.

### Destructive console actions

Confirmation is required for: database restore, audit purge, stopping the hub listener, removing a
computer, deleting a group, revoking an agent, deleting an automation task, deleting an alert rule,
terminating a process, deleting remote files, and stopping or restarting a service.

## 9. Data at rest

The hub database is SQLite under the hub data directory. It is protected by NTFS permissions for
the Windows account that runs the console, not by database encryption. The agent state file
(`agent-state.json`) contains the agent certificate PFX, pending private key and pending enrollment
token encrypted with DPAPI LocalMachine and the entropy string `RemoteDesk.Agent.State.v1`. On
startup the agent also resets the data-folder DACL to SYSTEM and local Administrators (inheritance
disabled), so a standard local user cannot copy the state file and reuse the machine-scope secrets.
The plaintext pairing package is deleted as soon as the token has been protected in state.

Logs are written with Serilog:

- Hub: `%LOCALAPPDATA%\RemoteDeskHub\logs\hub-YYYYMMDD.log`, 31 files retained.
- Agent: `%ProgramData%\RemoteDesk Agent\logs\agent-YYYYMMDD.log`, 14 files retained, 20 MB each.

Logs are designed not to contain passwords, tokens, private keys or full command secrets. Command
output can contain sensitive data produced by the target computer, so treat logs and command history
as sensitive.

## 10. Threat model and residual risk

### Mitigated

| Threat | Mitigation |
| --- | --- |
| Rogue device on the LAN enrolls itself | Single-use token, private source check, CSR proof of possession, admin approval by default |
| Eavesdropping on management traffic | TLS 1.3 with mutual certificate authentication |
| Agent impersonation | Certificate bound to agent and computer IDs; thumbprint pinned in the database |
| Rogue hub contacting an agent | Agent pins the hub TLS server thumbprint and the hub console client certificate thumbprint; both are CA-validated and EKU-checked |
| Stolen enrollment token | Short lifetime, hashed at rest, single use, optional approval, header transport, DPAPI-protected in agent state |
| Brute-force console login | PBKDF2 with high iteration count, five-attempt lockout, audit of failures |
| Privilege escalation through the UI | Every service call checks a permission; navigation is filtered |
| Repudiation of privileged actions | Hash-chained audit log with actor, target and result |
| Replay of heartbeat or telemetry | Nonce cache and clock-skew checks |
| Accidental destructive action | Confirmation prompts and protected process/service lists |
| Path traversal in file transfer | PathGuard roots, protected locations, UNC and ADS rejection, and reparse-point checks on every intermediate directory and the final file |

### Residual risks and limits

- **Local administrator on the hub computer.** DPAPI current-user protection and NTFS permissions do
  not defend against an attacker who can run code as the same Windows user or as an administrator.
- **CA private key.** The CA key is a file protected by DPAPI, not a hardware security module.
  Compromise of that file allows issuing arbitrary certificates. Rotating the CA means re-enrolling
  every agent.
- **Revocation is database-backed.** There is no CRL or OCSP endpoint. A revoked agent cannot use
  the hub, but its certificate is still cryptographically valid to any system that trusts the CA
  without checking the hub database.
- **No certificate renewal automation for the hub.** Agent certificates renew automatically, but hub
  TLS and console certificates expire after two years and require a controlled reinstall or CA
  replacement. Rotating the CA means re-enrolling every agent.
- **Hub host runs in the interactive session.** The hub CA and TLS private keys use DPAPI
  current-user scope, so the headless host cannot run as `LocalSystem` without a key-store redesign.
  Signing out stops the host. This is a real availability limitation, not a security claim.
- **Agent binds all interfaces.** The listener rejects non-private source addresses and the installer
  adds a Windows Firewall rule scoped to the local subnet. If the firewall is disabled or the rule is
  removed, the TLS listener is still reachable from a network that can route to the host, although
  TLS, certificate pinning and the private-address middleware continue to apply.
- **In-memory replay cache.** A hub restart clears nonces. Heartbeat state is idempotent, but the
  cache is not durable.
- **Audit retention.** A user with database access can delete the database or the oldest audit rows.
  The hash chain detects modification of remaining rows, not deletion of the entire file, and there
  is no off-box anchor.
- **Command execution is intentionally powerful.** An authorized operator can run arbitrary commands
  as `LocalSystem` on an approved agent. The controls are authorization, confirmation, policy and
  audit, not sandboxing.
- **The shared folder is a low-trust drop location.** Anyone who can write to `C:\zzz` on a managed
  computer can cause a file to be copied to every other peer. The product validates paths and hashes
  content, but it does not scan files for malware or enforce a file-type policy. Restrict the folder
  ACL and use endpoint protection where this matters.
- **The agent MSI can open TCP 3389 to the local subnet.** The rule is created only when the
  administrator installs with `ENABLE_RDP_FIREWALL=1`; a normal install leaves RDP closed. When
  enabled, it broadens the attack surface of the managed computer. It does not enable RDP or grant
  sign-in rights. Omit the property or remove the rule where RDP is not allowed, and enforce
  account, NLA and patching controls on the target.
- **Single-machine validation.** The product was tested on one Windows computer over loopback. It has
  not been validated against a hostile physical LAN, network Address Resolution Protocol spoofing, or
  a compromised switch.

## 11. Hardening checklist

1. Leave `AgentAutoApprove` disabled.
2. Use `RequireTls` as designed (TLS cannot be disabled) and keep the hub bind address at
   `127.0.0.1` if agents are not needed.
3. Keep the Windows Firewall rules installed and scoped to the local subnet.
4. Use unique, long administrator passwords and one account per person.
5. Keep the default permitted file roots narrow; do not add `C:\` or `C:\Windows`.
6. Export the audit log before purging, and verify the hash chain after any suspected incident.
7. Back up the database and store backups on protected media.
8. Protect the hub data directory with NTFS permissions; do not copy `certs/*.key.dat` to other
   computers.
9. Leave automatic agent renewal enabled and monitor the certificate-expiry column; plan hub
   certificate replacement before its two-year expiry.
10. Remove stale discovery records and revoke agents that are decommissioned.
11. Regenerate any pairing package created by a pre-v2 build; older packages lack the hub server
    certificate pin and are rejected by current agents.
12. If more than one administrator operates the console, enable `security.commandApprovalRequired`
    so destructive commands need a second administrator. Keep the allowlist empty or minimal.
