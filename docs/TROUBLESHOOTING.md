# Troubleshooting

This guide covers the failures that are most likely in a real deployment: listener ports, private
certificates, enrollment, agent service startup, firewall rules, SQLite recovery and audit-chain
verification. Each section starts with the fastest check.

## 1. Where the evidence lives

| Item | Default location |
| --- | --- |
| Hub data | `%LOCALAPPDATA%\RemoteDeskHub` (override with `REMOTEDESKHUB_DATA`) |
| Hub database | `%LOCALAPPDATA%\RemoteDeskHub\remote-desk.db` |
| Hub logs | `%LOCALAPPDATA%\RemoteDeskHub\logs\hub-YYYYMMDD.log` |
| Hub certificates | `%LOCALAPPDATA%\RemoteDeskHub\certs` |
| Hub backups | `%LOCALAPPDATA%\RemoteDeskHub\backups\remotedeskhub-*.db` |
| Agent data | `%ProgramData%\RemoteDesk Agent` |
| Agent state | `%ProgramData%\RemoteDesk Agent\agent-state.json` |
| Agent logs | `%ProgramData%\RemoteDesk Agent\logs\agent-YYYYMMDD.log` |
| Agent certificates | `%ProgramData%\RemoteDesk Agent\certs` |
| Agent work files | `%ProgramData%\RemoteDesk Agent\work` |
| Service name | `RemoteDeskAgent` |
| Event log source | **Application** log, source `RemoteDesk Agent` |
| Hub port | TCP 7443 (HTTPS) |
| Agent port | TCP 7444 (HTTPS) |

Hub logs rotate daily and are kept for 31 days. Agent logs rotate daily or at 20 MB and are kept for
14 files.

Useful commands (run in an elevated PowerShell where noted):

```powershell
# Service state
Get-Service RemoteDeskAgent | Format-List Name,Status,StartType

# Recent agent events
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='RemoteDesk Agent'} -MaxEvents 50 |
    Format-Table TimeCreated,Id,LevelDisplayName,Message -AutoSize

# Is the hub answering at all?
Invoke-RestMethod https://localhost:7443/api/health

# Is the agent port reachable from the hub?
Test-NetConnection -ComputerName 192.168.1.20 -Port 7444

# Who owns a port?
Get-NetTCPConnection -LocalPort 7443 -State Listen |
    Select-Object LocalAddress,LocalPort,OwningProcess
```

The `/api/health` endpoint is anonymous and is the quickest way to separate "the hub is not running"
from "the agent cannot connect".

## 2. Hub will not start

### The window opens, but a listener warning appears

The console intentionally stays usable when Kestrel cannot bind. The warning text includes the
underlying error. Check:

1. **Port already in use.** `Get-NetTCPConnection -LocalPort 7443 -State Listen` shows the owning
   process. Another copy of RemoteDesk Hub, IIS, a container proxy or a VPN client may hold it.
2. **Bind address.** Settings → Network → bind address defaults to `0.0.0.0`. If it is set to a
   specific address that no longer exists on this computer, Kestrel fails. Set it back to `0.0.0.0`
   or the current LAN address.
3. **Permission to bind.** Ports below 1024 need elevation; use the default 7443 for an unelevated
   console.

Change the port in **Settings → Network**, save, and the hub restarts its listener automatically.

### `RemoteDesk Hub could not start`

The startup dialog appears when database initialization, certificate creation or the shell loop
throws. Read the newest `hub-*.log` first. Common causes:

| Symptom in log | Cause | Fix |
| --- | --- | --- |
| `The specified cipher suite is not supported` / TLS errors | Windows build lacks TLS 1.3 support | Windows 10 22H2 or later is required |
| `CryptographicException` on key load | Data folder was copied from another Windows user or computer | Restore a backup made on this account, or start fresh and re-enroll agents |
| `database is locked` | Another hub process, a long-running backup, or an external tool | The console enforces one instance per session; close other processes and wait for the operation |
| `SQLite Error 11: database disk image is malformed` | Disk error or interrupted write | Restore the newest backup (section 8) |
| `Access to the path ... denied` | Data folder permissions | Give the current user full control of the data folder |

### DPAPI and moved data folders

Hub private keys are protected with DPAPI **current-user** scope. Agent keys use **LocalMachine**
scope. Copying `%LOCALAPPDATA%\RemoteDeskHub` to another Windows account or another computer does
not transfer the ability to decrypt the CA key: the application cannot start the listener, and
existing agent certificates cannot be validated against a recreated CA.

For a machine migration, use **Settings → Database → Create backup**, move the backup file, install
the hub on the new computer, create a fresh database there, then restore the backup through the UI.
If the CA cannot be recovered, every agent must be re-enrolled with a new pairing package.

## 3. Sign-in and account problems

| Symptom | Explanation | Resolution |
| --- | --- | --- |
| "Account is locked" | Five failed attempts, five-minute lockout | Wait five minutes, or ask an administrator to reset the password in **Users** |
| Correct password rejected | Username is case-insensitive but must exist; account may be disabled | Check **Users** as an administrator |
| First-run wizard appears again | No user accounts exist in the database | The database was replaced or deleted; complete the wizard or restore a backup |
| Last administrator cannot be disabled | Deliberate guard | Create or promote another administrator first |

There is no email or recovery service. If no administrator can sign in and the password is lost,
the supported recovery path is to restore a backup from before the lockout. Deleting
`remote-desk.db` resets the product to first run but loses computers, history and the CA; agents
would then need re-enrollment.

## 4. Certificate and TLS failures

The private CA is **not** installed in the Windows certificate store. A browser or `curl` without the
CA will report an untrusted certificate. That is expected. Only the hub and enrolled agents trust it.

### Hub → agent call fails with a TLS error

1. Confirm the agent is approved and active in **Computers → Agent enrollments**. Revoked or
   rejected agents return `403`.
2. Confirm the agent management port. It defaults to 7444 but is assigned during enrollment and can
   differ. The value is shown on the computer details panel.
3. Confirm the hub's console client certificate is the one pinned by the agent. Reinstalling the hub
   with a new data folder creates a new certificate; old agents will reject it and must be
   re-enrolled.
4. Check certificate expiry. Agent certificates are valid for one year; hub certificates for two
   years. An expired certificate passes no chain validation. Re-enroll the agent.
5. Check the system clocks. TLS validation rejects a certificate that is not yet valid. The replay
   guard also rejects heartbeats more than five minutes away from the hub clock.

### Agent reports a missing pinned hub certificate

An agent enrolled by an older build may have no `HubServerCertificateThumbprint` in
`agent-state.json`. The agent deliberately fails closed rather than trusting any CA-issued
`serverAuth` certificate. Delete the agent state file and enroll again with a newly generated
pairing package. This prevents another agent certificate from impersonating the hub.

### Agent → hub heartbeat fails

The agent log records `Hub communication problem ...` with the HTTP status.

| Status | Meaning |
| --- | --- |
| `401` / TLS handshake failure | Client certificate not presented or not trusted; state file damaged |
| `403` | Agent certificate not bound to an approved agent row, or the agent was revoked |
| `409` | Replay detected: duplicate nonce or clock skew greater than five minutes |
| `429` | Rate limit exceeded; reduce telemetry frequency or investigate a loop |
| Connection refused / timeout | Wrong hub URL, hub not running, firewall, or wrong port |

The hub URL is stored in `agent-state.json` at enrollment. If the hub moved, either re-enroll or
start the agent once with `--hub https://new-host:7443`. The override is applied when the agent
imports the pairing package.

### Pairing package rejected

A package is rejected if it has no `https://` hub URL, has no CA certificate, or is missing the
`HubServerCertificateThumbprint` field introduced in format v2. A plain `http://` URL is refused
before any network call. Regenerate the package from the current hub; packages from older builds
predate hub-server pinning and fail closed.

## 5. Agent service problems

### Service will not install

`--install` requires an elevated prompt and returns exit code `5` otherwise. The MSI runs elevated,
so this mainly affects manual installs. Check:

```powershell
# Elevated
cd 'C:\Program Files\RemoteDesk Agent'
.\RemoteDeskHub.Agent.exe --install
```

The MSI registers `RemoteDeskAgent` through the Windows Installer `ServiceInstall` table with
`LocalSystem`, automatic start and own-process type; the manual `--install` path uses `sc.exe` for the
same configuration and sets restart-on-failure (60 s, 120 s, 300 s).

### Service starts and stops immediately

1. Read the agent log and the Application event log.
2. A missing pairing package is **not** fatal: the service stays running and retries enrollment with
   exponential back-off. A fatal error looks like `stopped because of an unrecoverable error`.
3. Check that `%ProgramData%\RemoteDesk Agent` is writable by `LocalSystem`. A restrictive ACL or a
   security product can block state writes.
4. Check for a port conflict on 7444. The agent logs the bind failure and stops the API; heartbeat
   may still run, but remote administration will fail.
5. If the certificate is corrupted, the agent logs `The stored agent certificate could not be
   loaded`. Remove `agent-state.json` and the `certs` folder, stage a new pairing package, and
   restart the service.

### Agent is running but the hub never sees it

- Confirm the service is `Running`, not `StartPending`.
- Check the pairing package is in `%ProgramData%\RemoteDesk Agent\enroll.rdhenroll`, or was passed with
  `PAIRINGPACKAGE=` during MSI install.
- Check the hub URL inside the package. It must be reachable from the agent, not just from the hub.
- Check the hub firewall rule and the agent firewall rule (section 6).
- Check for approval: **Computers → Agent enrollments** may show a pending request. Approval is
  required unless the token was created with auto-approve.

### Manual agent diagnostics

```powershell
# Run in the foreground with console logging; stop the service first
Stop-Service RemoteDeskAgent
& 'C:\Program Files\RemoteDesk Agent\RemoteDeskHub.Agent.exe' --console
```

Do not run two agent instances against the same data directory. The management port and state file
are single-writer.

## 6. Firewall and network

Both MSIs create inbound rules scoped to the local subnet:

| Package | Rule | Port |
| --- | --- | --- |
| Hub | RemoteDesk Hub | TCP 7443 |
| Agent | RemoteDesk Agent | TCP 7444 |

If traffic still fails:

```powershell
# Elevated: inspect existing rules
Get-NetFirewallRule -DisplayName 'RemoteDesk*' |
    Get-NetFirewallPortFilter |
    Format-Table InstanceID,Protocol,LocalPort -AutoSize

# Elevated: add a temporary lab rule (narrow the scope in production)
New-NetFirewallRule -DisplayName 'RemoteDesk Hub (lab)' -Direction Inbound `
    -Protocol TCP -LocalPort 7443 -Action Allow -Profile Domain,Private
```

Check that the network profile is **Private** or **Domain**, not **Public**. Windows Firewall blocks
the scoped rules on public networks by design.

Also check:

- The hub and agent are on the same routable subnet, or routing/firewall between subnets allows the
  two ports.
- A VPN or endpoint protection product is not intercepting TLS.
- The agent's `IPAddress.Any` listener is reachable from the hub address; `Test-NetConnection` from
  the hub is the definitive test.
- Enrollment rejects public source addresses. A NAT or proxy that rewrites the source IP to a public
  address will fail enrollment even if the computers are physically local.

## 7. Discovery finds nothing or too much

| Symptom | Check |
| --- | --- |
| No devices at all | Scope is wrong; the local interface has no IPv4 address; scan timeout is too low |
| Only some devices | ICMP or the probe ports are blocked; raise timeout to 1500–3000 ms and lower concurrency |
| Duplicate entries | A device changed IP; discovery keys on IP by design. Merge or ignore stale rows manually |
| Scan is slow | 4096-host cap; lower concurrency or scan smaller ranges |
| Agent computers show as unknown | The agent port probe failed; ensure 7444 is in the probe port list |

Discovery is read-only and requires the `ViewComputers` permission. Automatic discovery runs only
when `discovery.intervalMinutes` is greater than zero; the default is `0` (manual only). The interval
is applied by the shell after login, so a changed interval takes effect on the next login or restart.

## 8. Database, backup and restore

### Integrity check

**Settings → Storage & backups → Verify database** runs `PRAGMA integrity_check`. A healthy database
returns `ok`.

### Create and restore

- **Create backup** writes `backups\remotedeskhub-YYYYMMDD-HHmmss-fff.db` using the SQLite online
  backup API, then verifies the copy before reporting success. Millisecond precision and a collision
  suffix prevent a safety backup from overwriting the backup being restored.
- **Restore** first creates a safety backup and verifies the selected file. It then stages the
  database as `remotedeskhub.db.restore-pending` and writes a marker; the live database is **not**
  touched while the hub and background services are running. On the next startup the staged file is
  integrity-checked and applied before any `DbContext` is opened. **Restart the application** to
  complete a restore. If the staged file fails integrity, startup stops and leaves the current
  database intact; recover from the safety backup.

Restore is only allowed for file names inside the backups folder. Path traversal is rejected.

### Manual recovery

If the application cannot start far enough to use the UI:

```powershell
# Stop RemoteDesk Hub first. Keep a copy of the damaged database.
$data = "$env:LOCALAPPDATA\RemoteDeskHub"
Copy-Item "$data\remote-desk.db" "$data\remote-desk.broken.db" -ErrorAction SilentlyContinue
Copy-Item "$data\backups\remotedeskhub-<timestamp>.db" "$data\remote-desk.db" -Force
```

Then start the application. Delete `remote-desk.db-wal` and `remote-desk.db-shm` only after the
application is closed; SQLite recreates them. Never delete the `certs` folder while agents are still
enrolled — the CA is what allows them to authenticate.

### Database grows without bound

Metric samples are the largest table. Lower `monitoring.retentionDays` (default 30) in Settings.
Audit entries are retained according to `security.auditRetentionDays` (default 365); use **Audit Log
→ Purge old** to apply it. Purging appends an `Audit.Purged` row so the deletion is itself
auditable. Backups are not pruned automatically; remove old files from `backups` when appropriate.

## 9. Remote administration failures

| Symptom | Cause | Resolution |
| --- | --- | --- |
| "Permission denied" | Role lacks the permission | Use an Operator or Administrator account |
| Command rejected immediately | Empty/overlong input or destructive pattern without confirmation | Confirm the operation in the dialog; shorten the command |
| Process termination refused | Process is in `ProtectedProcesses` | Protected by design; use a different remediation |
| Service stop refused | Service is in `CriticalServices` | Protected by design, even with confirmation |
| File operation refused | Path outside `AgentAllowedRoots`, not absolute, traversal, or protected location | Choose a path inside the configured roots |
| Upload fails hash verification | Network truncation or disk full | Retry; check free space in the agent `work` directory |
| Agent returns timeout | Long command or slow inventory | Raise the command timeout (max 600 s) or run a narrower query |
| Remote Assistance does not launch | Tool is not installed, the computer has no address, or the launch failed | Check the session error and audit entry; install the tool or use **Copy details** |

The hub checks permissions and guard rails before calling the agent, and the agent checks them
again. A UI that appears to allow an action can still be refused by either layer; the error message
names the layer that refused it.

## 9a. Shared folder does not synchronize

| Symptom | Cause | Resolution |
| --- | --- | --- |
| Status cards show `-` and the folder path is empty | The shared-folder query failed, usually because the user lacks `TransferFiles` or the database migration has not run | Sign in as an Administrator, check hub logs, and confirm the `SharedFolderSync` migration is applied |
| File appears in `C:\zzz` but not in the Hub copy table | The 15-second cycle has not completed, or the file exceeds the size/count limit | Wait one cycle and press **Refresh**; check `sharedFolder.maxFileBytes` and `sharedFolder.maxFiles` |
| `C:\zzz` cannot be created | The service account lacks permission at the drive root | Create the folder with an administrator account or grant the agent service account modify rights |
| Agent files never arrive | Agent is offline, not approved, on a machine name equal to the hub, or the shared path differs | Check the Agents list, heartbeat time, configured `sharedFolder.path`, and hub logs |
| `name (conflict from ...)` file appears | Two peers edited the same path between cycles | Expected; the central version won and the other bytes are preserved. Open the conflict copy and merge manually |
| Deleted file comes back | Another peer still had an older copy and its write won the reconciliation | Confirm the central version, then delete again after peers have synchronized |
| Repeated files or loops on the hub | An agent is running on the hub with the same machine name, or two hubs share a folder | The hub-local peer excludes an agent with the same machine name; do not run a second hub against the same folder |

The internal staging directory is `C:\zzz\.rdh-sync`. Do not create it manually. A file left there
after a crash can be removed when the agent is stopped; it is never treated as a shared file.

## 10. Audit chain verification fails

**Audit Log → Verify chain** recomputes every entry's hash from its content and the previous hash.
A failure means one of:

- A row was modified or deleted directly in the database.
- The database was restored from a partial or older backup while newer audit rows remained.
- Two hub instances wrote to the same database concurrently from different processes.

The verifier reports the first broken entry. Treat a break as a security event: preserve the database
and logs, identify the change window, and restore a known-good backup rather than deleting rows to
make the check pass. Purging old entries through the UI is supported and keeps the chain valid; it
appends a new `Audit.Purged` entry that links to the retained chain.

## 11. Agent exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success (including `--install`, `--uninstall`, `--start`, `--stop`) |
| `1` | Unhandled fatal error; see log and Application event log |
| `2` | Invalid `--stage-pairing` arguments or missing package |
| `5` | Administrative rights required, service missing, or `sc.exe` operation failed |

## 12. What to include in a support report

1. Product version (`RemoteDeskHub.Desktop.exe` file version or `--version` for the agent).
2. Windows version and build (`winver`).
3. Whether the hub and agent are on the same subnet, and the network profile.
4. The relevant `hub-*.log` and `agent-*.log` excerpts with timestamps.
5. Output of `Get-Service RemoteDeskAgent`, `Get-NetTCPConnection` for ports 7443/7444, and
   `Get-NetFirewallRule -DisplayName 'RemoteDesk*'`.
6. The exact error text from the UI or HTTP status from the log.
7. Whether the data folder, CA or agent state was copied from another computer.

Never send private keys (`*.key.dat`, PFX files), `agent-state.json` in full, or pairing packages.
They contain credentials. Certificate thumbprints, public PEM certificates and log excerpts are
safe to share after removing host names if required.
