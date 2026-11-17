# Installing RemoteDesk Hub

## Requirements

- Windows 10 22H2 or Windows 11, 64-bit. TLS 1.3 must be available (Windows 11 and Windows Server
  2022 provide it by default; older Windows 10 builds may reject the hub listener).
- Administrative rights for installation (per-machine MSI, Program Files and firewall rules).
- Free disk space: approximately 220 MB for the hub payload, plus room for the database, logs,
  backups, exports and file transfers.
- Inbound TCP **7443** reachable from managed computers on the LAN.
- Outbound TCP **7444** from the hub to managed computers.

No .NET runtime installation is required: the MSI payload is self-contained.

## Install the hub MSI

1. Copy `RemoteDesk-Hub-1.0.0-x64.msi` to the management computer.
2. Double-click it and accept the EULA.
3. Complete the wizard. The installer writes the application to
   `%ProgramFiles%\RemoteDesk Hub`, creates a **RemoteDesk Hub** Start-menu shortcut and adds a
   Windows Firewall rule for inbound TCP 7443 scoped to the local subnet. The folder contains both
   `RemoteDeskHub.Desktop.exe` and the headless `RemoteDeskHub.HubHost.exe`.
4. Start **RemoteDesk Hub** from the Start menu. The console starts the headless hub host if it is
   not already running; closing the console does not stop a host that was started independently.

Silent installation:

```powershell
msiexec /i RemoteDesk-Hub-1.0.0-x64.msi /qn /norestart
```

The MSI writes `HKLM\SOFTWARE\RemoteDesk Labs\RemoteDesk Hub` (`InstallFolder`, `Version`) and
registers a major upgrade; installing a newer MSI replaces the older version.

## First run

The first launch opens a nine-step wizard. Nothing is written outside the data root, and the wizard
can be cancelled before the administrator is created.

1. **Welcome** – product summary and security expectations.
2. **Administrator** – creates the first local account. The default user name is `admin`; the
   password must be at least 10 characters with an upper-case letter, a lower-case letter and a
   digit. The password is salted with PBKDF2-SHA256 (210,000 iterations) and never stored in plain
   text.
3. **Network** – hub HTTPS port (default 7443), agent management port (default 7444), the hub URL
   written into pairing packages, and the bind address (`0.0.0.0` for LAN access or `127.0.0.1`
   for this computer only). Set the hub URL to the address agents can reach, for example
   `https://server01.corp.local:7443`. Do not use `localhost` unless agents run on the hub machine.
4. **Security** – explains mutual TLS and creates the private certificate authority (RSA-4096 root,
   two-year hub certificates, one-year agent certificates). The CA is also created automatically
   when the hub starts.
5. **Storage** – shows the data root, database, log and backup paths. Set the
   `REMOTEDESKHUB_DATA` environment variable before launch to relocate the data root (for example
   to a backed-up volume).
6. **Discovery** – probe timeout, concurrency, automatic scan interval and TCP probe ports
   (default `7444,445,3389`). Scans are limited to private IPv4 space, /24 per local subnet and
   4096 hosts per run.
7. **Alerts** – default CPU, memory, free-disk and temperature thresholds.
8. **Agents** – automatic approval policy, command execution policy and permitted file-transfer
   roots. The defaults are manual approval, commands allowed and roots `C:\Users;C:\ProgramData`.
9. **Finish** – review and complete. You are returned to the sign-in window.

## Data locations

The hub stores everything under one root, by default:

```
%LOCALAPPDATA%\RemoteDeskHub\
    remotedeskhub.db          SQLite database (EF Core)
    certs\                    ca.crt.pem, ca.key.dat, hub-server.*, hub-client.*
    logs\hub-YYYYMMDD.log     Serilog rolling log (31 days)
    backups\                  database backups
    transfers\                downloaded files
    exports\                  exported reports
```

`ca.key.dat` is a DPAPI-protected PKCS#8 key (current-user scope). The certificate directory is
created with the user's profile ACLs; do not copy it to other computers.

Because the data root is per user, two administrators signing in to the same hub computer with
different Windows accounts each have their own hub database and certificate authority. Use one
Windows account for administration, or set `REMOTEDESKHUB_DATA` to a shared, ACL-restricted folder
if a single shared hub store is required.

The headless host writes `hub-host.port` and `hub-host.pid` in that root while it owns the listener.
It runs in the interactive administrator session, not as a Windows service, because the hub private
keys are DPAPI current-user protected. Signing out stops the host; start the console again to
restart it.

## Network and firewall

The installer creates the firewall rule for the default port only. If you change the hub port in
**Settings → Network**, add a matching inbound rule:

```powershell
New-NetFirewallRule -DisplayName "RemoteDesk Hub (8443)" -Direction Inbound `
  -Protocol TCP -LocalPort 8443 -Profile Domain,Private -RemoteAddress LocalSubnet -Action Allow
```

The hub restarts its listener when the port or bind address is saved. If the listener cannot start,
the console remains usable and shows the error; fix the port or bind address in Settings and save
again.

The **Agent** MSI creates its own inbound rule for TCP **7444** for the management API, scoped to the
local subnet. It creates a TCP **3389** rule for Remote Desktop only when installed with
`ENABLE_RDP_FIREWALL=1`. The optional 3389 rule does not enable Remote Desktop or grant sign-in
rights. Enable RDP and authorize users separately, or omit the property if RDP is not permitted on
managed computers.

## Upgrade

Install the newer MSI over the existing installation. The MSI performs a major upgrade, stops and
removes the old files, preserves the per-user data root, and re-adds the shortcut and firewall rule.
The database schema is migrated automatically at startup. Take a backup first
(**Settings → Storage & backups → Create backup**).

## Uninstall

Use **Settings → Apps → Installed apps → RemoteDesk Hub → Uninstall**, or:

```powershell
msiexec /x RemoteDesk-Hub-1.0.0-x64.msi /qn
```

Uninstall removes the application files, shortcut, registry values and firewall rule. It does **not**
delete `%LOCALAPPDATA%\RemoteDeskHub`; remove that folder manually after exporting anything you need.
Removing the data root destroys the CA and database and cannot be undone.

## Command-line reference (hub)

The hub is a GUI application and accepts no product-specific switches. Useful environment
variables:

| Variable | Purpose |
| --- | --- |
| `REMOTEDESKHUB_DATA` | Overrides the hub data root |
| `DOTNET_ENVIRONMENT` | Standard .NET hosting environment name |

## Verify the installation

1. The hub window opens and the title bar shows **RemoteDesk Hub**.
2. **Settings → Security** shows three certificate thumbprints (server, console client, root CA).
3. **Settings → Storage & backups** shows an existing database and allows **Create backup**.
4. `https://<hub>:7443/api/health` returns a JSON health response. A browser warns about the private
   CA; that is expected. Agents validate the certificate chain against the pinned CA instead.
