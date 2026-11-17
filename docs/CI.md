# Continuous integration

The repository contains a GitHub Actions workflow at `.github/workflows/ci.yml`. It is the
authoritative release gate for code quality, installer construction and installer smoke tests.

## Workflow jobs

| Job | What it does |
| --- | --- |
| `build-test` | Restores, builds `Release`, runs all unit and integration tests, uploads TRX results and checks for vulnerable NuGet packages |
| `codeql` | Builds the solution with the CodeQL C# extractor and uploads the analysis |
| `installer-e2e` | Installs WiX 5.0.2, builds **1.0.0** and **1.0.1** installers, optionally signs them, then installs 1.0.0, upgrades to 1.0.1 and uninstalls both MSIs |

The installer job runs on `windows-latest`, where the runner has the administrative rights needed
for per-machine MSI installation, Windows service registration and firewall rules. It verifies:

- the hub install folder (including `RemoteDeskHub.HubHost.exe`), registry values, Start-menu
  component and firewall rule (TCP 7443, `LocalSubnet`);
- the agent install folder, registry values, automatic `RemoteDeskAgent` service, TCP 7444 firewall
  rule (`LocalSubnet`), absence of the optional 3389 rule on a normal install, and service startup;
- an opt-in install with `ENABLE_RDP_FIREWALL=1` that creates and then removes the 3389 rule;
- a major upgrade from 1.0.0 to 1.0.1 for both products;
- uninstall cleanup of the registry keys, firewall rules and Windows service.

MSI logs are uploaded as the `installer-logs` artifact even when the job fails.

## Local installer smoke test

The same test can be run locally from an **elevated** PowerShell window:

```powershell
Set-Location 'D:\MyAIProjects\RemoteDesk Hub'

# Build the baseline and upgrade MSIs
.\scripts\build-installers.ps1 -Version 1.0.0
.\scripts\build-installers.ps1 -Version 1.0.1

# Install 1.0.0, upgrade to 1.0.1, uninstall
.\scripts\test-installers.ps1 `
  -Version 1.0.0 `
  -UpgradeVersion 1.0.1 `
  -InstallerDir .\artifacts\installers `
  -UpgradeInstallerDir .\artifacts\installers
```

The test refuses to run without elevation because it creates a Windows service and firewall rules.
Use `-ValidateOnly` to check that the expected MSI files exist without installing anything.

## Code signing

Signing is optional and activates only when the repository secrets are present.

| Secret | Purpose |
| --- | --- |
| `CODE_SIGNING_PFX_BASE64` | Base64-encoded PKCS#12 code-signing certificate, including its private key |
| `CODE_SIGNING_PFX_PASSWORD` | Password for the PKCS#12 file |

When configured, the workflow signs the published hub and agent executables before WiX packages
them, then signs both MSIs. Signatures are timestamped with DigiCert and validated with
`Get-AuthenticodeSignature`. `SHA256SUMS.txt` is generated after signing.

> The workflow runs on GitHub-hosted `windows-latest`. The 1.0.0 release run passed the
> `Build and test`, `CodeQL` and `Installer E2E` jobs, including the elevated install, upgrade and
> uninstall path. The local shell is not elevated, so the installer path is covered by the hosted
> workflow rather than by a recorded local run.

## Branch protection

For a production repository, require the `Build and test`, `CodeQL` and `Installer E2E` checks before
merging to `main`. Keep the signing certificate in a protected environment or secret store, and
restrict who can approve a release build.
