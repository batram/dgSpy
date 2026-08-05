# Remote hosts

Remote deployment is package-based. A user receives one centrally provisioned ZIP, extracts it on the
debugger host, and runs its launcher in an interactive Windows session. The remote does not run a local
Gateway or MCP endpoint, and ordinary operation must not depend on an additional transport product or
long-running setup command.

## Current status

The repository can build a self-contained x64 host ZIP containing dnSpy, the compatible dgSpy extension,
private dependencies, a process-scoped launcher, bundle-local identity/credential state, and a sorted
SHA-256 manifest. The delivered RPC transport is still the authenticated local connection between a
Gateway and extension. Extension-initiated remote registration described below is the next implementation
step, not current behavior.

Build and verify the portable baseline on the central development machine:

```powershell
.\pack-remote-host.ps1
.\tests\verify-remote-host-package.ps1
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

The same provisioning operation adds the expected `host_id` and credential to the central Gateway
configuration. The remote user only extracts the ZIP and runs:

```powershell
.\launcher\Start-dgSpyRemoteHost.cmd
```

On startup, the extension connects outward, authenticates, registers, and becomes visible through
`list_hosts`. Unknown identities, invalid credentials, duplicate live connections, protocol mismatch,
and a certificate/identity mismatch fail closed. Disconnect uses bounded reconnect backoff and never
implicitly resumes, detaches, or terminates a paused target.

## Minimal mutual TLS

After plain authenticated reverse registration works, protect that same host connection with pinned
self-signed mutual TLS:

- The central machine owns one self-signed Gateway server certificate.
- Every remote package receives its own self-signed client certificate.
- The package pins the exact Gateway certificate.
- The Gateway pins each exact client certificate to one configured `host_id`.
- Certificates load from package/configuration files; provisioning does not modify OS trust stores.
- TLS 1.2 or later is required.

The Gateway exposes two independent boundaries:

```text
127.0.0.1:7350       MCP clients
configured-IP:7352   mutually authenticated remote hosts
```

The remote-host listener accepts only registration and the versioned host RPC transport; it never
accepts MCP. Removing a client-certificate pin revokes that package. Replacing either certificate
requires an explicit matching pin update.

## Responsibilities

- The AI agent knows only the local MCP endpoint and selects a `host_id` in tool calls.
- The central Gateway owns MCP, expected host identities, credentials/certificate pins, active host
  connections, and routing.
- The remote package owns dnSpy, the extension, its provisioned identity/private credential, launcher,
  manifest, and reconnection behavior.
- The remote target remains under dnSpy's debugger engine. Safe detach is mandatory before closing a host
  with an active attachment.
