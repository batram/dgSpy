# Remote hosts

Remote deployment is package-based. A user receives one centrally provisioned ZIP, extracts it on the
debugger host, and runs its launcher in an interactive Windows session. The remote does not run a local
Gateway or MCP endpoint, and ordinary operation must not depend on an additional transport product or
long-running setup command.

## Current status

The repository builds a centrally provisioned self-contained x64 host ZIP. The extension opens one
persistent authenticated outbound connection, the Gateway routes RPC over it, and `list_hosts` reports
connected, unavailable, and error states. The local loopback RPC path remains available. Provisioning
selects authenticated plaintext for trusted isolated networks or pinned mutual TLS per package.

Build and verify the portable baseline on the central development machine:

```powershell
.\pack-remote-host.ps1 -HostId 'win11-clean' -GatewayAddress '192.168.250.1'
.\tests\verify-remote-host-package.ps1 -ArchivePath .\artifacts\remote-host\dgSpy-remote-host-win11-clean-win-x64.zip
```

## Target minimal flow

```text
AI agent
    |
    | MCP at 127.0.0.1:7350/mcp
    v
central dgSpy.Gateway
    ^
    | persistent authenticated outbound host connection
    |
remote dnSpy + dgSpy extension
    |
    v
debug target
```

Only `dgSpy.Gateway` accepts MCP. The remote extension opens the host connection, registers its
centrally provisioned `host_id`, and carries routed RPC requests and responses on that same connection.
The remote exposes no network-reachable debugger listener; the existing loopback listener may remain for
local use. The Gateway keeps MCP bound to loopback and listens for remote hosts on a separately configured
local interface and port.

## Per-host provisioning

The central machine creates a package for one expected remote. It generates and records the same stable
identity and strong credential on both sides, and writes the Gateway endpoint into the package. The
intended command shape is:

```powershell
.\pack-remote-host.ps1 -HostId 'win11-clean' -GatewayAddress '192.168.250.1' -GatewayPort 7352
```

For mutual TLS, provision the package on a separate listener port:

```powershell
.\pack-remote-host.ps1 -HostId 'win11-clean-tls' -GatewayAddress '192.168.250.1' -UseTls -GatewayTlsPort 7353
```

The output contains no Gateway executable:

```text
dgSpy-remote-host-win11-clean.zip
|-- dnSpy.exe
|-- bin\Extensions\dgSpy\
|-- launcher\
|-- remote-host.json
|-- state\host.id
|-- state\gateway.token
`-- manifest.json
```

The same provisioning operation adds the expected `host_id` and credential to `gateway-hosts.json`.
Start the Gateway with `DGSPY_HOSTS_FILE`, `DGSPY_REMOTE_ADDRESS`, and `DGSPY_REMOTE_PORT` pointing at
that configuration and the dedicated host-listener bind address. The remote user only extracts the ZIP and runs:

```powershell
.\launcher\Start-dgSpyRemoteHost.cmd
```

Local deployment acceptance is automated by `tests\run-remote-registration-smoke.ps1`. It verifies the
manifest, starts the extracted self-contained host, routes `get_host_info` and `launch`, restarts the
Gateway while dnSpy and its target remain live, and verifies the same `session_id` and a non-regressing
event cursor after registration reconnects.

On startup, the extension connects outward, authenticates, registers, and becomes visible through
`list_hosts`. Unknown identities, invalid credentials, duplicate live connections, protocol mismatch,
and a certificate/identity mismatch fail closed. Disconnect uses bounded reconnect backoff and never
implicitly resumes, detaches, or terminates a paused target.

## Minimal mutual TLS

TLS packages protect the host connection with pinned self-signed mutual TLS:

- The central machine owns one self-signed Gateway server certificate.
- Every remote package receives its own self-signed client certificate.
- The package pins the exact Gateway certificate.
- The Gateway pins each exact client certificate to one configured `host_id`.
- Certificates and generated PFX password files load from package/configuration files; provisioning
  does not modify OS trust stores.
- TLS 1.2 or later is required.

The Gateway exposes two independent boundaries:

```text
127.0.0.1:7350       MCP clients
configured-IP:7352   optional plaintext remote hosts
configured-IP:7353   optional mutually authenticated TLS remote hosts
```

The host listeners may be enabled together or separately. Set `DGSPY_REMOTE_DISABLE_PLAINTEXT=true` to
disable plaintext. TLS additionally requires `DGSPY_REMOTE_TLS_PORT`,
`DGSPY_GATEWAY_SERVER_CERTIFICATE_FILE`, and `DGSPY_GATEWAY_SERVER_CERTIFICATE_PASSWORD_FILE`.
Neither listener accepts MCP. Removing a client-certificate pin revokes that package. Replacing either
certificate requires repackaging and an explicit matching pin update.

## Responsibilities

- The AI agent knows only the local MCP endpoint and selects a `host_id` in tool calls.
- The central Gateway owns MCP controller identity, coarse access mode, redacted audit records, expected
  host identities, credentials/certificate pins, active host connections, and routing.
- The remote package owns dnSpy, the extension, its provisioned identity/private credential, launcher,
  manifest, and reconnection behavior.
- The remote target remains under dnSpy's debugger engine. Safe detach is mandatory before closing a host
  with an active attachment.
