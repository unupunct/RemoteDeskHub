# Testing

RemoteDesk Hub has two automated test projects: 171 unit tests and 108 integration tests, **279
tests total**. All pass on the development machine. A separate loopback load/fault harness is
documented in [LOAD_TESTING.md](LOAD_TESTING.md). This document explains what each suite covers,
how the integration harness works, how to reproduce the results, and — importantly — what was not
tested.

## 1. Reproduce the results

```powershell
Set-Location 'D:\MyAIProjects\RemoteDesk Hub'

# Full build
dotnet build RemoteDeskHub.slnx -c Release -v minimal

# Both test projects
dotnet test RemoteDeskHub.slnx -c Release --no-build --nologo

# Or one project at a time
dotnet test tests\RemoteDeskHub.UnitTests\RemoteDeskHub.UnitTests.csproj -c Release --no-build
dotnet test tests\RemoteDeskHub.IntegrationTests\RemoteDeskHub.IntegrationTests.csproj -c Release --no-build
```

`scripts\build.ps1` runs restore, build and both test projects in one step.

Tests do **not** require administrator rights. They create a unique data root under
`%TEMP%\RemoteDeskHubTests\<guid>` and delete it afterwards. Kestrel binds loopback ports assigned by
the OS (port `0`).

### Result of the recorded run

| Project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| `RemoteDeskHub.UnitTests` | 171 | 0 | 0 |
| `RemoteDeskHub.IntegrationTests` | 108 | 0 | 0 |
| **Total** | **279** | **0** | **0** |

Environment: Windows 11 Pro x64, .NET SDK 10.0.403, build configuration `Release`, date
2026-11-16. The solution builds with **0 warnings and 0 errors**.

## 2. Unit tests (171)

Unit tests reference `Shared`, `Core`, `Infrastructure` and `Networking`, but exercise pure logic
only: no database, no listener, no file system state beyond temporary paths. They run quickly and
are deterministic.

| Test class | Tests | Coverage |
| --- | ---: | --- |
| `NetworkAddressGuardTests` | 25 | Private/loopback classification, LAN address parsing, scan scope excludes network and broadcast addresses, public and invalid addresses rejected |
| `ProtectedResourceTests` | 23 | Protected process list (case-insensitive, with and without `.exe`), critical service list, ordinary processes/services allowed |
| `PathGuardTests` | 26 | Absolute local-drive paths required, UNC and relative paths rejected, protected system locations blocked, parent-segment normalisation, root containment with sibling-prefix protection, traversal rejection, alternate data streams rejected, symlinked files rejected |
| `PermissionTests` | 25 | Administrator has every permission, Viewer is read-only, Operator day-to-day permissions, session sign-in/out delegation, role descriptions |
| `PasswordHasherTests` | 12 | PBKDF2 round trip, wrong password, malformed hash, unique salt per call, URL-safe token generation, deterministic SHA-256 token hash, complexity rules, token masking |
| `CredentialRulesTests` | 9 | Username length and character rules, overlong names rejected |
| `ProtocolSerializationTests` | 7 | `OperationResult` and generic result round trip, camelCase enums, required DTO fields, command result, transfer progress clamping, paged results |
| `CommandSafetyTests` | 13 | Critical command detection, ordinary-command false-positive checks, exact allowlist matching, whitespace normalisation and comment handling, destructive vs critical separation |
| `SharedFolderPlannerTests` | 14 | First sync, unchanged files, local/central edits, simultaneous-edit conflict naming, deletion tombstones, restore when central changed, unobserved local preservation, skipped paths, stale rows |
| `SharedFolderScannerTests` | 4 | Relative SHA-256 manifests, staging exclusion, oversized-file skip reporting, file-count truncation, invalid-root error |
| `SharedFolderPathGuardTests` | 13 | Relative shared paths, traversal rejection, rooted and UNC rejection, ADS and reserved-name rejection, trailing dot/space rejection, staging rejection |
| **Total** | **171** | |

The security tests are deliberately table-driven. For example, `ProtectedProcesses_BlocksWindowsCore`
runs once per protected name and `IsPasswordStrong_EnforcesComplexity` runs once per rule, so a
failure identifies the exact input that regressed.

## 3. Integration tests (108)

Integration tests use the real stack:

- Real EF Core SQLite database in a temporary folder.
- Real migrations and default seeding.
- Real `CertificateAuthority` with DPAPI-protected keys.
- Real `HubServer` on Kestrel with TLS 1.3 and mutual TLS on loopback.
- Real hub services and a signed-in administrator.
- `FakeAgentTransport` where the test needs to control an agent response without running a second
  process.

`TestHub.CreateAsync` builds a `ServiceCollection`, registers `AddRemoteDeskHubCore(root)` and
`AddRemoteDeskHubNetworking()`, replaces `IClock` with `MutableClock`, initializes the database,
seeds settings, creates the `admin` account and signs in. `TestHub.DisposeAsync` disposes the
provider and deletes the data folder.

`MutableClock` lets time-dependent tests advance time explicitly — lockout expiry, offline
detection, alert recovery, audit purge and certificate validity are stable rather than sleep-based.

| Test class | Tests | Coverage |
| --- | ---: | --- |
| `RemoteAdminTests` | 24 | Command execution and history, destructive confirmation, empty/overlong commands, audit entry, critical-command blocking and allowlisting, Viewer denial, protected process refusal, process termination confirmation, critical service refusal, ordinary service confirmation, invalid service action/startup type, two-administrator approval flow, approval mismatch/expiry/rejection, transcript SHA-256 audit detail, upload/download hash verification, delete confirmation, missing local file, transfer history, CSV/HTML/PDF report generation |
| `AgentLifecycleTests` | 16 | Heartbeat marks online and returns config, missing/unknown/mismatched certificate rejection, telemetry approval, metric persistence, CPU alert raised once and resolved on recovery, cooldown suppresses immediate retrigger, acknowledge/resolve, offline detection, bucketed series, unmanaged computers excluded, default threshold synchronisation, replayed heartbeat nonce, stale timestamp, replayed telemetry nonce |
| `AuthenticationTests` | 14 | Initial administrator creation, second creation rejected, weak password and bad username rejected, correct/incorrect login, unknown user, five-failure lockout until clock advance, disabled account, last administrator protection, Viewer cannot create users, Viewer cannot call user/audit/settings/automation service methods, role change and password reset, current-password requirement, no plaintext password storage |
| `CertificateAuthorityTests` | 12 | CA/server/client creation, DPAPI encryption of private keys, idempotent reuse, server certificate chain and serverAuth EKU, client certificate rejected as server certificate, self-signed certificate rejected, agent CSR signing, CSR-supplied SAN/EKU ignored, missing CA recreates leaves, foreign certificate detection, pairing package contains only public material, non-HTTPS pairing URL rejected |
| `EnrollmentTests` | 11 | Full approval flow, auto-approve immediate issuance, unknown token, public source address, expired token, single-use token with idempotent installation retry, rejection prevents approval, revocation blocks heartbeat, token creation requires permission, malformed CSR |
| `HubHttpTests` | 11 | Anonymous health endpoint, `/api/hub/info` requires the console certificate, accepts it, rejects an agent certificate, heartbeat requires a certificate, unregistered certificate forbidden, invalid enrollment token returns 400, full enrollment and heartbeat over HTTPS, certificate renewal replaces the thumbprint and rejects the old certificate, mismatched renewal machine name rejected, stop closes the listener |
| `AuditChainTests` | 8 | Empty log verifies, entries are hash-linked, tampering breaks the chain, deleting an entry breaks the chain, query filters by search and time, purge keeps entries within retention, purge removes expired entries and keeps the chain valid, blank actor defaults to `SYSTEM` |
| `BootstrapTests` | 9 | New database is first run and creates schema, administrator creation ends first run, seeding creates groups/rules/settings, initialization is idempotent, typed settings set/get, invalid setting falls back to default, backup create and verify, restore is staged without touching the live database, staged restore is applied on the next startup |
| `SharedFolderIntegrationTests` | 3 | Hub-local import, idempotent repeat cycle, changed file, simultaneous edit conflict copy, conflict upload, tombstone deletion, app-added files/folder, agent manifest pull, hub-local push through central store to agent |
| **Total** | **108** | |

### What the HTTP tests actually prove

`HubHttpTests.FullEnrollmentAndHeartbeat_OverHttps` is the closest automated equivalent of a real
deployment:

1. Start a real hub listener on a loopback port.
2. Generate an agent CSR and request enrollment with a real pairing token.
3. Approve the request through the service layer.
4. Attach the issued certificate and its private key to an `HttpClient`.
5. Send a heartbeat over TLS with the agent certificate and assert the response.
6. Stop the hub and assert the listener is closed.

This exercises Kestrel configuration, the certificate authority, chain validation, the
authorization middleware, JSON serialization and the lifecycle service together.

### What integration tests do not use

- No second physical or virtual machine. The "agent" is an `HttpClient` on the same host.
- No installed Windows service and no MSI installation.
- No real network switch, DNS, firewall or subnet routing.
- No real `mstsc`/`msra` session.
- `FakeAgentTransport` stands in for the agent management API in `RemoteAdminTests`; the real
  `AgentClient` is exercised separately by certificate and HTTP tests, but not against a live agent
  process end to end.
- No automated WPF UI tests. View models are not covered by the test projects; the verified surface
  is the service, security and transport layer behind them.

## 4. Manual verification that was performed

- `dotnet build RemoteDeskHub.slnx -v q` completed with 0 warnings and 0 errors.
- Both test projects were executed and reported the counts above.
- The loopback load harness was run for `load`, `fault` and `audit` scenarios; results are recorded
  in [LOAD_TESTING.md](LOAD_TESTING.md).
- Both MSIs were built with WiX 5.0.2 (see section 6).
- The MSI databases were opened read-only through the Windows Installer COM API to confirm product
  properties, features, firewall rows, the agent service row and the pairing custom action.
- No MSI install or uninstall was performed, because the available shell is not elevated.
- The hub-local shared folder was exercised against a real `C:\zzz` while the WPF console was running; the file appeared in the central table and blob store after the background cycle.
- No multi-machine smoke test was performed.

## 5. Security-specific test mapping

| Requirement | Test evidence |
| --- | --- |
| Unique agent identity | `CertificateAuthorityTests.SignAgentCertificate_ProducesTrustedClientCertificate`, enrollment tests |
| Enrollment approval | `EnrollmentTests.FullEnrollment_RequiresApproval_ThenIssuesCertificate`, rejection test |
| Certificate validation | `CertificateAuthorityTests` chain, EKU, CSR SAN/EKU stripping, CA-recovery and foreign-certificate tests; `HubHttpTests` certificate matrix |
| Certificate renewal | `HubHttpTests.AgentCertificateRenewal_ReplacesThumbprintAndRejectsOldCertificate`, `AgentCertificateRenewal_WithMismatchedMachineName_IsRejected` |
| Command policy | `CommandSafetyTests`, `RemoteAdminTests.CriticalCommand_IsBlockedAndAuditedByDefault`, `CriticalCommand_RunsWhenExactCommandIsAllowlisted`, `DestructiveCommand_RequiresSecondAdministratorApproval`, `Requester_CannotApproveOwnCommand` |
| Backup/restore integrity | `BootstrapTests.Restore_IsStaged_AndDoesNotOverwriteLiveDatabase`, `BootstrapTests.Restore_StagedDatabase_IsAppliedOnNextStartup` |
| RBAC | `PermissionTests`, `AuthenticationTests.Viewer_CannotCreateUsers`, `AuthenticationTests.Viewer_CannotCallAdministrativeServiceMethods`, `RemoteAdminTests.Viewer_CannotRunCommands`, `EnrollmentTests.CreateToken_RequiresEnrollAgentsPermission` |
| Least privilege / guard rails | `ProtectedResourceTests`, `RemoteAdminTests` protected process/critical service/confirmation tests, `PathGuardTests` |
| Revocation | `EnrollmentTests.RevokeAgent_BlocksHeartbeat`, heartbeat `403` handling |
| Audit logging | `AuditChainTests` (tamper, delete, purge), `RemoteAdminTests.Command_AuditEntryIsWritten`, `Command_AuditEntryContainsTranscriptHashes`, concurrent audit writers in the load harness |
| Replay protection | `AgentLifecycleTests` replayed heartbeat, stale timestamp and replayed telemetry tests |
| Rate limiting | Configured in `HubServer` and `AgentApiServer`; not asserted by an automated timing test |
| DPAPI for secrets | `CertificateAuthorityTests.PrivateKeys_AreEncryptedWithDpapi` |
| LAN-only enrollment | `EnrollmentTests.Enrollment_FromPublicAddress_IsRejected`, `NetworkAddressGuardTests` |
| No hardcoded credentials | Test passwords are constants in test code; production code contains no seeded password |

Rate limiting is the one mandatory control without a dedicated automated assertion. It is configured
declaratively (30 enrollment requests/minute per IP, 120 enrollment status requests/minute per token,
1200 agent requests/minute per thumbprint, 600 agent-API requests/minute) and returns `429`, but no
test drives the limiter to its threshold because wall-clock windows make such a test slow and flaky.
The in-process tests also do not exercise the Windows service ACL hardening or the `icacls` call;
that is best-effort at runtime and depends on the service running as LocalSystem or an administrator.

## 6. Build and installer verification

| Check | Result |
| --- | --- |
| `dotnet build` (Debug) | 0 warnings, 0 errors |
| `dotnet build` (Release, via scripts) | 0 warnings, 0 errors |
| Unit tests | 171 passed |
| Integration tests | 108 passed |
| Load harness | 120/120 requests at 12 agents; fault run recovered 48 requests with 0 errors; 400/400 audit rows and chain verified |
| Hub MSI produced | `artifacts\installers\RemoteDesk-Hub-1.0.0-x64.msi` |
| Agent MSI produced | `artifacts\installers\RemoteDesk-Agent-1.0.0-x64.msi` |
| Installer checksums | `artifacts\installers\SHA256SUMS.txt` |
| MSI table validation (read-only) | Hub: `MainFeature`, firewall rule TCP 7443 `LocalSubnet`, `RemoteDeskHub.HubHost.exe`. Agent: `MainFeature`, `RemoteDeskAgent` service (auto, own process), firewall rule TCP 7444 `LocalSubnet`, optional 3389 component gated by `ENABLE_RDP_FIREWALL`, `StagePairingPackage` custom action type 18 at sequence 4001 |
| MSI installation | **Not performed locally** (shell not elevated); passed in the `installer-e2e` CI job |
| MSI uninstall/upgrade | **Not performed locally**; 1.0.0 → 1.0.1 upgrade and uninstall passed in the `installer-e2e` CI job |
| Code signing | **Not performed locally** (no certificate); conditional CI step when signing secrets are configured |

The MSIs were produced by `scripts\build-installers.ps1 -Version 1.0.0`, which publishes both
applications self-contained for `win-x64` and invokes WiX with the UI and Firewall extensions. The
`installer-e2e` workflow job installs 1.0.0, upgrades to 1.0.1, verifies service, firewall and
registry state, then uninstalls both products. The 1.0.0 release run passed on GitHub-hosted
`windows-latest`; the local non-elevated shell did not run that path. Build success is not proof
that installation succeeds on every clean machine; see the limitation below.

## 7. Known testing limitations

Be explicit about these when evaluating the product:

1. **Single machine only.** The development environment had one Windows 11 computer. No test ran
   between two physical computers, two virtual machines, or across a real LAN.
2. **Loopback, not LAN.** HTTPS tests use `127.0.0.1` with OS-assigned ports. Firewall rules, subnet
   routing, switch behaviour, NAT and VPN interference were not exercised.
3. **No installed agent process.** The integration suite sends HTTP requests as an agent but never
   installs `RemoteDeskAgent`, starts the service, or runs the collectors against a live hub.
4. **No local MSI install/uninstall.** Installer tables, custom actions, service registration,
   firewall rule creation and major-upgrade behaviour were built but not executed from this
   non-elevated shell. The `installer-e2e` GitHub Actions job covers install, upgrade and uninstall
   on GitHub-hosted `windows-latest` and passed for 1.0.0. The `PAIRINGPACKAGE` custom action was not
   exercised end to end.
5. **No UI automation.** WPF windows and view models are not covered by automated tests. Rendering,
   binding failures, keyboard navigation and DPI behaviour were not asserted. The shared-folder tab
   was inspected manually in the running WPF console; its buttons were not driven by an automated UI
   test.
6. **Load testing is single-machine.** The load harness exercises concurrent heartbeats, a hub
   restart and concurrent audit writers on loopback. It does not measure real LAN latency, hundreds
   of physical agents, multi-day soak behaviour, memory growth or rate-limit thresholds.
7. **Certificate renewal is tested over HTTPS but not over a full year or a CA replacement.** The
   renewal tests use freshly issued certificates; they do not advance a real certificate past
   `NotAfter` or rotate the root CA.
8. **No adversarial network testing.** TLS downgrade attempts, malformed frames, fuzzing and
   denial-of-service behaviour were not tested beyond the explicit certificate and replay cases.
9. **No physical remote-assistance session.** The page records and launches local tools, but no
   end-to-end RDP/Quick Assist session between two computers was verified.

These gaps are documented rather than papered over. A production pilot should add a two-machine
enrollment and administration test, a clean-VM MSI install/uninstall test, and a multi-day soak run
before relying on the product at scale.








