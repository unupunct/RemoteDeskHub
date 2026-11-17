# User guide

This guide covers the RemoteDesk Hub console: first-run setup, sign-in, the 15 pages in the sidebar,
and the actions that change a managed computer. It assumes the hub and at least one agent are
installed. See [INSTALLATION.md](INSTALLATION.md) and [AGENT_DEPLOYMENT.md](AGENT_DEPLOYMENT.md) for
installation.

## 1. Before you sign in

The hub stores its data per Windows user:

| Item | Default location |
| --- | --- |
| Data root | `%LOCALAPPDATA%\RemoteDeskHub` (override with `REMOTEDESKHUB_DATA`) |
| Database | `%LOCALAPPDATA%\RemoteDeskHub\remotedeskhub.db` |
| Certificates | `%LOCALAPPDATA%\RemoteDeskHub\certs` |
| Logs | `%LOCALAPPDATA%\RemoteDeskHub\logs` |
| Backups | `%LOCALAPPDATA%\RemoteDeskHub\backups` |

The agent stores its state under `%ProgramData%\RemoteDesk Agent`.

The first launch opens the setup wizard. Later launches open the sign-in window. The hub HTTPS
listener starts before sign-in so agents can heartbeat while nobody is at the console.

## 2. First-run wizard

The wizard runs once, when no local account exists. Every value can be changed later in **Settings**.

### Step 1 — Welcome

Explains what setup configures: the local administrator, the hub listener, the private certificate
authority, data locations, discovery defaults, alert thresholds and agent policy. Choose **Next**.

### Step 2 — Create the first administrator

| Field | Rule |
| --- | --- |
| User name | 3–64 characters; letters, digits, spaces, `.`, `-`, `_`, `@` |
| Password | At least 10 characters; upper-case, lower-case and a digit |
| Confirm password | Must match |

This account is an Administrator and cannot be removed by setup. The last enabled administrator
cannot be disabled or demoted later.

### Step 3 — Hub and agent network settings

| Field | Default | Notes |
| --- | --- | --- |
| Hub HTTPS port | 7443 | Used by agents and the console listener |
| Agent management port | 7444 | Hub calls each agent on this port |
| Hub URL | `https://<machine>:7443` | Written into pairing packages; must be reachable by agents |
| Bind address | `0.0.0.0` | `127.0.0.1` restricts the hub to this computer |

Windows Firewall must allow the chosen ports. The installer adds local-subnet rules.

### Step 4 — Mutual TLS with a private certificate authority

Describes the RSA-4096 CA, short-lived certificates, agent pinning and TLS 1.3. **Create certificate
authority now** creates the CA and the hub certificates immediately; otherwise they are created when
setup finishes.

### Step 5 — Where your data lives

Read-only view of the data root, database, logs and backups. Use the `REMOTEDESKHUB_DATA`
environment variable before launching the hub to relocate the data root.

### Step 6 — Network discovery defaults

| Field | Default | Range |
| --- | --- | --- |
| Probe timeout | 800 ms | 100–10000 |
| Concurrent probes | 24 | 1–128 |
| Automatic scan interval | 0 (manual only) | 0–1440 minutes |
| TCP probe ports | 7444, 445, 3389 | Comma-separated |

Discovery is restricted to private IPv4 space, a /24 per local subnet, and at most 4096 hosts per
run. An automatic interval makes the shell run a local-subnet scan in the background; `0` keeps
discovery manual.

### Step 7 — Performance alert thresholds

| Field | Default |
| --- | --- |
| CPU warning | 90% |
| Memory warning | 92% |
| Minimum free disk | 10% |
| CPU temperature | 85 °C |

These values are applied to the built-in global alert rules when setup finishes. Individual rules
can be edited or scoped later on **Monitoring > Rules**.

### Step 8 — Agent enrollment and file access

- **Automatically approve agents with a valid token** — unchecked by default. Leave it unchecked to
  review each request in **Computers > Agent enrollments**.
- **Permitted file-transfer roots** — one per line. Default `C:\Users` and `C:\ProgramData`. The
  agent rejects paths outside these roots, system locations, UNC paths and junctions regardless of
  the operator's role.

### Step 9 — Ready to launch

Shows a summary. **Finish** writes the settings, ensures the CA exists, and opens the sign-in window.
The next step is to create a pairing package and install the agent (see
[AGENT_DEPLOYMENT.md](AGENT_DEPLOYMENT.md)).

## 3. Signing in and the shell

Sign in with a local account. Five consecutive failures lock the account for five minutes; the
remaining time is shown. Successful sign-in resets the counter.

The shell contains:

- **Sidebar** — the 15 pages, filtered by role. The arrow at the top-left collapses it to icons.
  Pending agent approvals appear as a badge on **Computers** for roles that can enroll agents.
- **Header** — page title, subtitle, signed-in user and role.
- **Content** — the selected page.
- **Status bar** — the last operation's message. It turns red when a page reports an error.
- **Tray icon** — double-click or **Open** restores the window; **Exit** closes the hub. Closing the
  window while **Minimize to tray** is enabled hides it instead of exiting.
- **Sign out** — returns to the sign-in window without exiting the process.

The console signs out automatically after the configured idle period (**Settings > Security**,
default 60 minutes, `0` disables it). Windows notifications appear in the tray when alerts are
raised and **Enable Windows notifications** is on.

If the hub listener cannot start (for example, the port is in use), the console still opens and shows
a warning. Change the port in **Settings > Network** and save to restart the listener.

## 4. Dashboard

Live overview, refreshed every 20 seconds while the page is open.

- **Fleet cards** — managed computers, online, offline, warning/critical, active alerts.
- **Fleet averages** — CPU, memory and disk across online computers.
- **CPU / memory utilisation** — up to six online computers over the last two hours, 60 points per
  series.
- **Computer list** — up to 12 computers, ordered by severity.
- **Recent alerts** — active alerts with severity and time.
- **Recent privileged actions** — the newest audit entries.
- **Refresh** — reloads immediately.
- **Scan network** — asks for confirmation, probes the local subnet, and reports addresses and
  devices found.

Charts are empty until agents have reported at least one sample.

## 5. Computers

Inventory, agent state and per-computer actions.

### Toolbar

| Control | Purpose |
| --- | --- |
| Group filter | All computers or a specific group |
| Search | Matches computer name, IP address or notes |
| Refresh | Reloads the list and details |
| New group / Rename group / Delete group | Manage groups; deleting a group leaves its computers ungrouped |
| Add computer | Registers a computer manually (name, IPv4, optional MAC, group, notes) |
| Refresh inventory (all) | Asks for confirmation, then refreshes selected computers in parallel |
| Run diagnostics (all) | Asks for confirmation, then runs the diagnostic bundle in parallel |
| Agent enrollments | Opens pending approvals and pairing-package generation |

Bulk operations require a selection and are limited to four concurrent agent calls.

### Computer details

Selecting a computer shows:

- identity: name, IP, MAC, machine ID, operating system, agent version, group, last seen, agent state;
- remote access IDs: TeamViewer, AnyDesk, LM Viewer and RustDesk identifiers, editable in **Edit**;
- hardware: manufacturer, model, CPU, memory, software count;
- configuration history: hardware snapshots captured when the configuration changes, with a readable
  change summary;
- live utilisation: CPU, memory, disk;
- notes.

Actions:

- **Edit** — friendly name, description, tags, group, notes.
- **Refresh inventory** — pulls hardware and software from the agent.
- **Toggle trust** — marks the computer trusted or untrusted (recorded in the audit log).
- **Revoke agent** — asks for a reason, revokes the certificate binding immediately, and stops
  management. The agent must be re-enrolled.
- **Remove** — asks for confirmation and deletes the computer record.

### Agent enrollment

**Agent enrollments** opens one panel with two sections.

**Pending approvals** lists agents that submitted a valid token but are waiting for review. Each row
shows the computer, machine name, address and request time.

- **Approve** — signs the CSR, activates the agent and creates the computer record.
- **Reject** — records a reason and refuses the request.

**Create a pairing package**

| Field | Default |
| --- | --- |
| HTTPS address agents use to reach this hub | `network.hubUrl` |
| Token lifetime | 24 hours (1–168) |
| Automatically approve the agent that redeems this token | off |

**Generate and save...** writes a `.rdhenroll` file and displays the enrollment token once. The
package contains the token, CA certificate, hub TLS server thumbprint and hub console client
thumbprint; it never contains a private key. Treat the file as a secret until it is used or expires.

An agent enrolled without auto-approval stays in **PendingApproval** and cannot run commands or file
operations until approved.

## 6. Discovery

Finds computers on private IPv4 networks.

1. Choose a mode:
   - **Local subnet** — probes the subnet of a local adapter.
   - **Address range** — `192.168.1.10-192.168.1.80`.
   - **CIDR range** — `192.168.1.0/24`.
2. Adjust **Timeout ms** and **Concurrency** if needed (loaded from Settings).
3. **Start scan**. Progress is shown inline; **Cancel** is not offered mid-scan in this release, but
   navigating away cancels the page token.
4. **Refresh results** reloads the stored discovery records.

Results show address, name, MAC, vendor, agent presence, inventory state, last seen, open services,
operating system, response time and first-seen time.

Select a result to:

- **Add to inventory** — optionally assign a group, add notes and mark it trusted;
- **Toggle trust**;
- **Edit notes**;
- **Remove discovery record** — asks for confirmation; does not remove a managed computer.

Discovery never scans public addresses, more than a /24 per local subnet, or more than 4096 hosts
per run.

## 7. Monitoring

Performance history, live agent metrics and alerts for one computer at a time.

### Charts

- **Computer** — choose the target.
- **Range** — 1, 6, 24 or 72 hours.
- **CPU / memory / disk** charts show bucketed samples. Empty charts mean the agent has not reported
  in that period.

### Live agent metrics

The panel queries the agent directly and shows CPU, memory, disk free, uptime, temperature and
process count. If the agent is unreachable, the page reports the error instead of stale numbers.

### Alerts

- **Active alerts only** — filter.
- **Acknowledge** — marks the alert as seen but keeps it active.
- **Resolve** — closes it. Acknowledged and resolved alerts remain in history.
- Alerts are deduplicated per computer and metric and resolve automatically when the metric
  recovers.

### Rules

- **New / Edit** — name, metric, severity, threshold, cooldown, enabled, and optional computer or
  group scope.
- **Delete** — asks for confirmation.
- Rules are evaluated after each metrics batch. Global rules also receive the default thresholds
  from **Settings > Monitoring**. After an alert resolves, its cooldown prevents the same metric
  from flapping back immediately.

### Other actions

- **Export history** — CSV/HTML/PDF for the selected computer and range.
- **Purge old metrics** — asks for confirmation, then deletes samples older than
  `monitoring.retentionDays` (default 30). The background service also performs this purge once per
  day and records it in the audit log.

Offline detection runs every 30 seconds in the background: an agent that misses three heartbeat
intervals is marked offline and raises an alert (once per outage).

## 8. Inventory

Hardware and software for one computer.

- **Hardware** — CPU model, cores/threads, clock, memory, motherboard, BIOS, storage, graphics,
  network adapters and memory modules.
- **Software** — installed applications with publisher, version, install date, architecture and
  estimated size. Filter by text or publisher; **Clear** resets the filters.
- **Refresh hardware** / **Refresh software** — pull fresh data from the agent.
- **Compare software** — choose another computer; the grid shows applications present on one or both
  machines.

Inventory is collected on demand. A computer that has never been refreshed shows an empty state
rather than placeholder data.

## 9. Processes

- Choose a computer; the process list loads from the agent.
- **Show protected** includes protected system processes (they remain non-terminable).
- Filter by name.
- **Terminate process** — select a row, confirm the prompt, and the agent terminates it. Protected
  names are refused before the request is sent.
- **Export CSV** — exports the current list.
- Every termination is audited with the process name, PID and actor.

## 10. Services

- Choose a computer to load services with display name, service name, status, startup type, account
  and a critical marker.
- **Start / Stop / Restart / Pause / Resume** — Stop and Restart ask for confirmation. Critical
  services cannot be stopped or restarted at all.
- **Startup type** — choose `Automatic`, `AutomaticDelayedStart`, `Manual` or `Disabled`, then
  **Apply startup type**.
- **Dependency chain** — shows services that depend on the selected service.
- All changes are audited.

## 11. Remote Commands

Runs PowerShell or `cmd.exe` on an approved agent.

1. Select a computer.
2. Choose the shell and timeout (5–3600 seconds, default 120).
3. Enter the command (maximum 8,000 characters).
4. **Run command**. A confirmation prompt shows the target and command. Destructive patterns
   (delete, format, disk changes, shutdown, service/registry deletion, account changes, `taskkill`,
   `bcdedit`, `Invoke-Expression`, DISM, WMIC and similar) are called out in the prompt.
5. Output and exit code appear below. Standard output is truncated at 512 KB per stream.

Critical commands (`format`, `diskpart`, boot configuration, event-log clearing, service deletion and
similar) are blocked before they reach the agent unless the exact, whitespace-normalised command is
in the allowlist on **Settings > Security**. Blocked attempts are audited as `Remote.CommandBlocked`.

If **Require a second administrator to approve destructive or critical commands** is enabled, a destructive or
critical command creates a 15-minute request instead of running. Another administrator approves or
rejects it in the **Approval queue** tab; the requester then selects the approved row and uses
**Run approved command**. Self-approval is refused, so at least two administrator accounts are
needed. Request, decision and execution are audited, and the approval is bound to the exact
computer, command, shell, timeout and elevation setting. An approval cannot be reused.

Every command runs as the agent's `LocalSystem` identity; the console labels this rather than
offering an elevation toggle.

- **Cancel selected** — cancels a running command by correlation ID.
- **Command history** — last 200 commands with actor, shell, status, exit code and duration.
- **Approval queue** — pending and approved requests visible to the signed-in administrator. A
  requester sees their own requests; approvers see all.
- **Export history** — CSV/HTML/PDF.
- **Clear output** — clears the local text pane only.

Commands are recorded in history and the audit log even when they fail.

## 12. File Transfer

Browse and transfer files inside the roots the agent permits.

1. Select a computer. The **root** selector lists the roots reported by the agent.
2. Navigate with the path bar and **Go**, **Up**, **Refresh**, or double-click a folder.
3. **Upload** — choose a local file, confirm the remote path, and watch progress. The agent writes a
   temporary file and finalizes it after the last chunk.
4. **Download** — choose a destination; progress and throughput are shown.
5. **New folder**, **Rename**, **Delete** — each asks for confirmation. Delete additionally requires
   the `DeleteRemoteFiles` permission (Administrator by default).
6. **Cancel transfer** stops the current transfer; the history records the outcome.
7. **History** lists recent transfers with direction, sizes, duration and status.

The agent refuses paths outside its roots, protected Windows locations, UNC paths and junctions even
for administrators. A failed transfer does not leave a partially finalized destination file.

### Shared folder tab

The **Shared folder** tab synchronizes a dedicated `C:\zzz` between the hub and every managed
computer. It is separate from the permitted roots used by the Remote files tab.

1. **Add files** or **Add folder** copies items into the hub's `C:\zzz`. Existing names are preserved
   by creating `name (2).ext` or a numbered folder; nothing is overwritten.
2. **Open folder** opens the hub's `C:\zzz` in File Explorer. Files can also be dropped there
   directly or on any managed computer.
3. **Refresh** reloads the status cards and the central **Hub copy** table. The background service
   checks every 15 seconds, so a new file can take one cycle to appear.
4. The **Agents** list shows remote peers that have reported a manifest. **Online** means a heartbeat
   within five minutes; offline peers reconcile when they return.
5. When two computers edit the same file, the hub copy wins and the other copy is renamed
   `name (conflict from MACHINE yyyyMMdd-HHmmss).ext`. Conflict copies are ordinary files and are
   synchronized on the next cycle. Deletions propagate as tombstones; a changed central file is
   restored instead of deleted.

The default limits are 256 MB per file and 20,000 files. See
[SHARED_FOLDER.md](SHARED_FOLDER.md) for limits, ACL behavior and troubleshooting.

## 13. Remote Assistance

RemoteDesk Hub launches an operator-side assistance tool and records the session; it does not
provide its own screen viewing or input control and never stores passwords or Quick Assist codes.

- **Assistance targets** lists computers with a recorded IP address and whether they are online
  (heartbeat within 45 seconds).
- Choose a tool:
  - **RDP** — launches `mstsc /v:<address>`.
  - **Quick Assist** — launches Quick Assist and shows the Microsoft-hosted 6-digit code exchange
    instructions. The code is never stored or transmitted by the hub.
  - **Windows Remote Assistance** — launches `msra /offerra <address>`.
  - **TeamViewer**, **AnyDesk**, **LM Viewer** or **RustDesk** — uses the saved per-computer ID or
    the address you enter, and launches the installed tool with the appropriate arguments.
  - **Third-party** — uses a configured profile's executable and arguments.
- **Launch session** records a prepared session, starts the external tool and marks the session
  started with its process ID. If the tool cannot be found or started, the session is marked failed.
- **Consent policy** — `security.remoteConsentRequired` is off by default, so launches start
  immediately and are audited with the trusted-network policy note. Enable the setting in
  **Settings → Security** to require the explicit consent dialog for every launch.
- **Copy details** places the target, tool and arguments on the clipboard for manual use.
- **Mark session ended** closes a running record. The hub also detects when the launched process
  exits and records the end time and duration automatically.
- **Profiles** (New/Edit/Delete) store reusable tool names, arguments and operator instructions.
  Profile management requires the `ManageSettings` permission.

Every launch records the operator, tool, target and time in the audit chain. The Agent MSI creates an
inbound TCP 3389 firewall rule only when installed with `ENABLE_RDP_FIREWALL=1`; the target must still
have Remote Desktop enabled and your Windows account allowed to sign in.

## 14. Automation

Scheduled and event-driven tasks. The hub evaluates due tasks every 30 seconds.

| Field | Notes |
| --- | --- |
| Name, description | Required name |
| Trigger | Manual, Interval, Daily, Computer offline, Service stopped, Disk space low |
| Interval | Minutes, for the Interval trigger |
| Daily time | `HH:mm`, local time |
| Action | Check disk space, Check online, Collect inventory, Check service, Generate report, Run diagnostic, Run command, Notify |
| Target | Optional single computer; blank means all managed computers |
| Service name | For Check service and Service stopped |
| Command / shell | For Run command |
| Free-space threshold | For Check disk space / Disk space low |
| Notification message | For Notify |
| Task enabled | Disabled tasks are skipped |

- **New task / Edit** — opens the editor.
- **Enable / disable** — toggles the task without deleting it.
- **Run now** — executes immediately and records the result.
- **Delete** — asks for confirmation.
- **Execution history** — status, counts, log and trigger source for the last runs.

Event triggers have built-in cooldowns (15 minutes for offline, 30 minutes for the others). Tasks
run as `SYSTEM` and are audited.

## 15. Reports

Generate CSV, HTML or PDF files. Reports are produced in memory and then offered as a save dialog;
the hub does not silently write files.

| Report | Contents |
| --- | --- |
| Network inventory | Computers, groups, addresses, agent state, live utilisation |
| Hardware inventory | CPU, memory, storage, graphics, motherboard |
| Software inventory | Applications with publisher, version and size |
| Alert report | Active, acknowledged and resolved alerts by severity |
| Agent health | Enrollment state, last heartbeat, version, certificate status |
| Audit log | Privileged actions with actor and result |
| Custom selection | Any report name in the list plus **performance** history, using the date range |

- **Format** — `csv`, `html` or `pdf`.
- **From / To** — used by reports that accept a date range.
- **Generate** — starts the selected report.
- **Generated in this session** — keeps the last files; **Save again** reopens the save dialog;
  **Clear list** removes the list entries (it does not delete files already saved).

## 16. Audit Log

- **From / To** — date range (default last 7 days).
- **Search** — actor, operation, computer, details.
- **Verify hash chain** — recomputes the SHA-256 chain and reports the first broken record.
- **Export** — CSV report of the filtered log.
- **Purge old** — asks for confirmation and deletes entries older than
  `security.auditRetentionDays` (default 365). A purge record is appended.

Select a row to see details, error text, duration and source IP. Audit entries are written for
sign-ins (including failures), enrollment and revocation, commands, files, processes, services,
automation, reports, settings, users, backups and remote sessions.

## 17. Users & Roles

- **New account** — user name, password and role; **Create user**.
- Select an account to change its role, enable/disable it, or reset its password.
- **Change my password** — current password plus the new password.
- The last enabled administrator cannot be disabled or demoted.
- Role reference:
  - **Administrator** — full control including security, users, settings and backups.
  - **Operator** — commands, processes, services, files, assistance, automation and alerts.
  - **Viewer** — read-only dashboards, inventory, monitoring, reports and exports.

See [SECURITY.md](SECURITY.md) for the complete permission matrix.

## 18. Settings

Seven tabs.

### General

- **Start with Windows** — writes or removes a per-user `HKCU\...\Run` entry.
- **Minimize to tray** — closing the window hides it instead of exiting.
- **Windows notifications** — tray balloons for new alerts.

### Network

Hub HTTPS port, agent management port, hub URL used in pairing packages, and bind address. Saving a
changed port or address restarts the listener immediately. Start/stop controls are also available.

### Monitoring

- Metrics interval (seconds), heartbeat interval (seconds).
- Metrics retention (days), used by **Purge old metrics**.
- Alert cooldown (minutes), applied to global rules.
- Default alert thresholds for CPU, memory, free disk and temperature. Saving applies them to the
  global alert rules.

### Discovery

Probe timeout, concurrency, automatic scan interval and probe ports. These values are used by the
Discovery page, the Dashboard scan and the background scan.

### Agent policy

- Auto-approve agents with a valid token.
- Permit remote commands and file transfer.
- Permitted file-transfer roots.

These values are returned to agents in the heartbeat response and enforced by the agent.

### Security

- Audit retention in days (used by **Purge old**).
- Session timeout in minutes (`0` disables idle sign-out).
- Critical-command blocklist toggle and exact allowlist (one command per line; `#` comments allowed).
- Require a second administrator to approve destructive commands.
- Require explicit consent for remote-assistance launches (`security.remoteConsentRequired`, off by
  default). When off, launches start immediately and are still audited with the policy note.
- Off-box audit anchor path (`security.auditAnchorPath`). When set to a local or UNC path, each audit
  record is also appended there as JSON Lines after the database commit.
- Require TLS is shown checked and disabled: TLS cannot be disabled in this release.
- Certificate thumbprints for the server, console client and CA.

### Storage & backups

- Data, log and backup paths with **Open data folder** and **Open logs folder**.
- **Create backup** — SQLite online backup plus integrity check.
- **Restore** — confirmation, automatic safety backup, validation, then staged restore. The live
  database is not overwritten while the hub is running; restart the application to apply the staged
  file. Startup verifies the staged database before replacing the live file.
- **Verify database** — runs `PRAGMA integrity_check`.
- Backup list with timestamps and sizes.

## 19. Confirmations and safety

The following actions always ask for confirmation before they run:

- terminating a process;
- stopping or restarting a service (critical services are refused outright);
- deleting, renaming or overwriting remote files;
- deleting an automation task, alert rule, group, discovery record or computer;
- revoking an agent;
- restoring the database;
- purging audit entries or metrics;
- stopping the hub listener;
- running a potentially destructive command.

Protected processes and critical services are blocked in both the hub and the agent. The agent also
refuses file paths outside its roots regardless of the console user's role.

## 20. If something looks wrong

- Empty pages usually mean no agent has reported yet, or the selected computer is offline.
- A listener warning at startup means the configured port is busy; change it in **Settings >
  Network**.
- An agent stuck in **PendingApproval** needs approval in **Computers > Agent enrollments**.
- An agent that shows **Revoked** must be re-enrolled with a new pairing package.
- See [TROUBLESHOOTING.md](TROUBLESHOOTING.md) for log locations and common failures.
