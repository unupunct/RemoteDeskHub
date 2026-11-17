# Network configuration

RemoteDesk is designed for a private, routed LAN. It does not use the internet, cloud relays or
third-party brokers. All traffic is HTTPS with mutual TLS between the hub and agents.

## Ports

| Port | Protocol | Direction | Listener | Purpose |
| --- | --- | --- | --- | --- |
| 7443 | TCP / HTTPS (TLS 1.3) | Agents → hub, console → hub | Hub (`0.0.0.0` by default) | Enrollment, heartbeat, telemetry, WebSocket live stream, hub info |
| 7444 | TCP / HTTPS (TLS 1.3) | Hub → agents | Agent (`0.0.0.0`) | Commands, processes, services, inventory, file transfer, events |
| 445 / 3389 | TCP | Hub → LAN hosts | Windows | Optional discovery probes only (no authentication, no SMB/RDP sessions) |

Ports are configurable:

- **Hub port:** Settings → Network → *Hub HTTPS port*. Saving restarts the listener.
- **Agent port:** Settings → Network → *Agent management port*. The value is sent to agents in their
  heartbeat configuration and used for new pairing packages.
- **Per-agent override:** `RemoteDeskHub.Agent.exe --port <n>` or the installed service's command
  line (`--data`/`--port`).

The default ports are compiled into `HubConstants` and seeded into settings on first run.

## Bind addresses

- Hub default `0.0.0.0` – listens on all IPv4 interfaces. Use this on the management computer.
- `127.0.0.1` – loopback only. Agents on other computers cannot connect; useful for evaluation.
- A specific LAN IP – restricts the listener to one interface.

The agent management API always binds all IPv4 interfaces on its configured port. Restrict access
with Windows Firewall (local subnet) and VLAN/ACL rules.

Both Kestrel listeners are IPv4. IPv6-only networks are not supported in this release.

## TLS

- The hub listener enforces **TLS 1.3**. Clients that cannot negotiate TLS 1.3 are rejected during
  the handshake. The outbound hub client and the agent enrollment client offer TLS 1.2/1.3, so a
  TLS 1.3 server is selected.
- The hub presents `hub-server` (two-year certificate, server-auth EKU, SANs for `localhost`, the
  machine name, `<machine>.local`, loopback and every local IPv4 address).
- Agents validate the hub certificate against the **CA certificate in the pairing package**
  (custom root trust), require the serverAuth EKU and pin the **hub TLS server certificate**
  thumbprint. The agent management API requires a client certificate and pins the hub console
  certificate thumbprint.
- The hub requires a CA-issued client certificate for `/api/hub/*` and `/ws/live`, and a registered
  agent certificate for `/api/agent/*`. `/api/health` and `/api/enrollment/*` are reachable without a
  client certificate, but still over TLS.
- Agent heartbeat requests must include a client certificate whose thumbprint matches an approved,
  non-revoked agent and whose agent/computer IDs match the heartbeat payload.

There is no option to disable TLS. `security.requireTls` exists as a compatibility setting but the
listener is always HTTPS.

## Firewall

The installers add these rules:

| Rule | Port | Profile | Scope |
| --- | --- | --- | --- |
| RemoteDesk Hub (HTTPS) | TCP 7443 in | All profiles | Local subnet |
| RemoteDesk Agent (management) | TCP 7444 in | All profiles | Local subnet |
| RemoteDesk Agent (Remote Desktop) | TCP 3389 in | All profiles | Local subnet, only with `ENABLE_RDP_FIREWALL=1` |

The agent MSI creates the TCP 3389 rule only when installed with `ENABLE_RDP_FIREWALL=1`. A normal
install leaves Windows Firewall unchanged for RDP. The optional rule does not enable Windows Remote
Desktop or grant sign-in rights; enable **Settings → System → Remote Desktop** and add the operator
account to **Remote Desktop Users** separately. Remove the rule if RDP is not permitted in your
environment:

```powershell
Remove-NetFirewallRule -DisplayName "RemoteDesk Agent (Remote Desktop)"
```

If ports change, create equivalent rules. Example for a custom hub port:

```powershell
New-NetFirewallRule -DisplayName "RemoteDesk Hub (9443)" -Direction Inbound `
  -Protocol TCP -LocalPort 9443 -Profile Domain,Private -RemoteAddress LocalSubnet -Action Allow
```

Delete the old rules to avoid leaving unused openings:

```powershell
Get-NetFirewallRule -DisplayName "RemoteDesk Hub*" | Remove-NetFirewallRule
```

The public profile is deliberately not opened. If a computer is classified as "Public" by Windows,
the built-in rule does not apply; mark the network as Domain/Private or add a scoped rule explicitly.

## Discovery

Discovery is a hub feature, not an agent feature.

- Enumerates local IPv4 interfaces that are up and not loopback.
- Scans only private address space (`10/8`, `172.16/12`, `192.168/16`, plus loopback), at most a /24
  per local subnet and 4096 hosts per run.
- Probes a configurable set of TCP ports (default `7444,445,3389`) with a timeout (default 800 ms)
  and bounded concurrency (default 24).
- Resolves host names where possible, classifies likely Windows hosts, and marks agents that respond
  on 7444.
- Never performs SMB authentication, RDP login, credential spraying or public-address scanning.

Discovery results are inventory candidates, not managed agents. A computer becomes managed only after
agent enrollment and approval.

## Hub URL used by agents

The pairing package stores an absolute HTTPS URL. Agents use it for enrollment and heartbeats. Choose
a name that resolves from every target computer:

- DNS name: `https://server01.corp.local:7443` (preferred; survives IP changes).
- Static LAN IP: `https://192.168.1.20:7443`.
- Do **not** use `localhost` or `127.0.0.1` for remote agents.
- The hub certificate SAN list includes the machine name, `<machine>.local`, loopback and the
  computer's local IPv4 addresses. Agents validate the private CA chain, the serverAuth EKU and the
  pinned hub server thumbprint; they do not rely on a DNS-name match. A custom alias therefore
  works as long as the pairing package was generated by the same hub instance and pins that
  certificate.

The value is seeded from Settings → Network → *Hub URL (used in pairing packages)* and can be edited
per package in the enrollment panel.

## Rate limits and timeouts

- Hub enrollment requests: 30 requests/minute per source IP, no queue.
- Hub enrollment status: 120 requests/minute per enrollment token (or source IP when no token is
  supplied), no queue.
- Hub agent policy: 1200 requests/minute per certificate thumbprint or IP, queue 20.
- Agent API: fixed-window limiter per caller; request body limit 16 MB.
- Hub request body limit 4 MB; request headers timeout 15 s; keep-alive 2 min.
- Hub-to-agent HTTP timeout and connection pooling are in `AgentClient` (pooled connections, retry on
  transient failures).
- Enrollment polling starts at 5 seconds and backs off to 30 seconds, for up to 24 hours. A `429`
  response immediately moves the agent to the 30-second interval. The status token is sent in the
  `X-Enrollment-Token` header rather than the URL.

## Proxies and name resolution

The .NET HTTP clients do not use a system proxy for hub/agent calls; they connect directly. Ensure
DNS or `hosts` entries resolve the hub name on each agent. If a proxy is mandatory in your
environment, use a direct-connect exception for the hub address.

## Network troubleshooting checklist

1. From the agent computer: `Test-NetConnection <hub> -Port 7443`.
2. From the hub: `Test-NetConnection <agent> -Port 7444`.
3. Confirm both computers are on Domain/Private network profiles.
4. Check `Get-NetFirewallRule -DisplayName "RemoteDesk*"`.
5. Verify the hub URL in the pairing package and that its host name is in the server certificate SANs.
6. Confirm the hub listener is running: Settings shows **Hub running · port 7443**, or call
   `https://<hub>:7443/api/health` from the hub.
