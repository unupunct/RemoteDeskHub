# Building RemoteDesk Hub

## Toolchain

| Tool | Version used | Notes |
| --- | --- | --- |
| Windows | Windows 11 Pro 64-bit | Windows 10 22H2 or later is supported |
| .NET SDK | 10.0.403 | `net10.0` for libraries, `net10.0-windows` for WPF/agent |
| WiX Toolset | 5.0.2 | Only required to build MSIs |
| WiX UI extension | 5.0.2 | `WixToolset.UI.wixext` |
| WiX Firewall extension | 5.0.2 | `WixToolset.Firewall.wixext` |

The solution uses **central package management** (`Directory.Packages.props`) and shared build
properties (`Directory.Build.props`). EF Core and Microsoft.Extensions packages are pinned to
`10.0.15`; the product version is `1.0.0`.

## Install the WiX tool and extensions

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2
wix extension add -g WixToolset.Firewall.wixext/5.0.2
```

If `wix` is not on `PATH` immediately after installation, use
`%USERPROFILE%\.dotnet\tools\wix.exe` or open a new terminal.

> WiX 7 requires accepting the Open Source Maintenance Fee EULA. This repository targets WiX 5,
> which does not.

## Build and test

```powershell
Set-Location 'D:\MyAIProjects\RemoteDesk Hub'
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

The script restores, builds `Release`, and runs both test projects. Equivalent manual commands:

```powershell
dotnet build RemoteDeskHub.slnx -c Release -v minimal
dotnet test tests\RemoteDeskHub.UnitTests\RemoteDeskHub.UnitTests.csproj -c Release --no-build
dotnet test tests\RemoteDeskHub.IntegrationTests\RemoteDeskHub.IntegrationTests.csproj -c Release --no-build
```

The integration tests bind Kestrel to a loopback port (`0` = OS-assigned) and create temporary
SQLite databases and certificate authorities under `%TEMP%\RemoteDeskHubTests`. No administrative
rights are required for tests.

## Publish self-contained binaries

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Version 1.0.0
```

Output:

```
artifacts\publish\hub\     RemoteDeskHub.Desktop.exe, RemoteDeskHub.HubHost.exe and runtime
artifacts\publish\agent\   RemoteDeskHub.Agent.exe and runtime
```

Both applications are published self-contained `win-x64`, so target computers do not need the .NET
runtime installed. Debug symbols are omitted from the installer payload. The hub folder includes
the headless host; the console uses it when present and falls back to in-process hosting when it is
not.

## Load and fault harness

The load harness is not part of the published product. Run it from source:

```powershell
dotnet run --project tools\RemoteDeskHub.LoadTest -- --agents 20 --iterations 25 --scenario all
```

See [LOAD_TESTING.md](LOAD_TESTING.md) for scenarios, measured results and limitations.

## Build the MSIs

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-installers.ps1 -Version 1.0.0
```

Output:

```
artifacts\installers\RemoteDesk-Hub-1.0.0-x64.msi
artifacts\installers\RemoteDesk-Agent-1.0.0-x64.msi
artifacts\installers\SHA256SUMS.txt
```

The script calls `publish.ps1` first, then WiX with:

```
-arch x64
-ext WixToolset.UI.wixext
-ext WixToolset.Firewall.wixext
-d ProductVersion=<version>
-d PublishDir=<publish folder>
-d LicenseRtf=installer\License.rtf
```

### Sign the binaries and MSIs

Signing is optional. Supply a PKCS#12 code-signing certificate and its password:

```powershell
.\scripts\build-installers.ps1 -Version 1.0.0 `
  -SigningPfxPath C:\secure\codesign.pfx `
  -SigningPfxPassword '<password>'
```

The published hub and agent executables are signed before WiX packages them, then both MSIs are
signed and timestamped. `SHA256SUMS.txt` is written after signing. Use `scripts\sign-artifacts.ps1`
directly to sign an existing artifact list:

```powershell
.\scripts\sign-artifacts.ps1 `
  -PfxPath C:\secure\codesign.pfx `
  -PfxPassword '<password>' `
  -Files .\artifacts\installers\RemoteDesk-Hub-1.0.0-x64.msi, .\artifacts\installers\RemoteDesk-Agent-1.0.0-x64.msi
```

### Installer smoke test

Run this from an **elevated** shell. It installs the baseline MSI, upgrades to the second MSI, checks
the service/firewall/registry state and uninstalls both products:

```powershell
.\scripts\build-installers.ps1 -Version 1.0.0
.\scripts\build-installers.ps1 -Version 1.0.1
.\scripts\test-installers.ps1 -Version 1.0.0 -UpgradeVersion 1.0.1 `
  -InstallerDir .\artifacts\installers -UpgradeInstallerDir .\artifacts\installers
```

The same path runs automatically in the `Installer E2E` GitHub Actions job. See [CI.md](CI.md).

### Installer contents

Both packages are per-machine MSIs with a minimal WiX UI (EULA, progress, finish), a Start-menu
shortcut policy for the hub, automatic major upgrades and ARP registration.

- Hub: installs to `%ProgramFiles%\RemoteDesk Hub`, adds a Start-menu shortcut, and creates a
  Windows Firewall rule for inbound TCP **7443**, scoped to the local subnet.
- Agent: installs to `%ProgramFiles%\RemoteDesk Agent`, registers the **RemoteDeskAgent** service
  (`LocalSystem`, automatic start, restart-on-failure), creates an inbound TCP **7444** firewall rule
  scoped to the local subnet, and optionally stages a pairing package from the `PAIRINGPACKAGE` MSI
  property. Install with `ENABLE_RDP_FIREWALL=1` to also create the TCP **3389** rule; the optional
  rule opens the port for RDP but does not enable Remote Desktop or grant sign-in rights.

The `PAIRINGPACKAGE` custom action runs the installed agent with
`--pair "<file>" --data "C:\ProgramData\RemoteDesk Agent" --stage-pairing`. It is skipped on upgrade
and when the property is empty.

## Database migrations

The initial EF Core migration is committed under
`src/RemoteDeskHub.Infrastructure/Data/Migrations`. If you change the model, regenerate it with the
local tool:

```powershell
& "$env:USERPROFILE\.dotnet\tools\dotnet-ef.exe" migrations add <Name> `
  --project src\RemoteDeskHub.Infrastructure `
  --startup-project src\RemoteDeskHub.Infrastructure `
  --output-dir Data\Migrations
```

`dotnet ef` is not on `PATH` by default on this workstation; the explicit path above is required.
The hub applies migrations at startup (`DatabaseInitializer.InitializeAsync`).

## Code signing

The checked-in WiX sources do not sign the MSIs or executables. For distribution, sign both
binaries and the MSIs with an Authenticode certificate after publishing/building, for example:

```powershell
signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 `
  artifacts\installers\RemoteDesk-Hub-1.0.0-x64.msi
```

Signing must happen after `build-installers.ps1`; re-running the script overwrites the MSIs.
