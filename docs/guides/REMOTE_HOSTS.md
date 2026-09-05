# Remote hosts

For a debugger on this machine, use the managed per-user workflow in
[quick start and local deployment](GETTING_STARTED.md). Remote packaging is the sibling workflow for a
different Windows host.

Remote deployment is package-based. A user receives one centrally provisioned ZIP, extracts it on the
debugger host, and runs its launcher in an interactive Windows session. The remote does not run a local
Gateway or MCP endpoint, and ordinary operation must not depend on an additional transport product or
long-running setup command.

## Current status

The released dgSpy package contains one immutable self-contained x64 tree shared by dnSpy, the CLI, and
the Gateway. The installed Gateway personalizes that tree into a per-host ZIP without a source checkout,
SDK, restore, or build. The extension opens one persistent authenticated outbound connection, the
Gateway routes RPC over it, and `list_hosts` reports
connected, unavailable, and error states. The local loopback RPC path remains available. Provisioning
selects authenticated plaintext for trusted isolated networks or pinned mutual TLS per package.

Release engineering builds the portable payload once:

```powershell
dotnet run --project Build\DgSpyTool -- pipeline --repo . --artifacts artifacts --build-id release
```

An installed agent creates a personalized package with one MCP call:

```text
create_remote_host_package { host_id: "win11-clean", gateway_address: "192.168.250.1", compression: "none" }
```

`compression` is optional: `optimal` is the default, `fastest` trades size for quicker extraction,
and `none` is intended for local-network deployment to slower VMs where extraction time matters more
than transfer size.

## Several Gateway addresses

A Gateway is often reachable at different addresses depending on which network a host sits on: a LAN
adapter for machines on the office network, a Hyper-V or WSL virtual switch address for guests behind
it. `gateway_address` is the address one host dials, so hosts on different networks are provisioned
with different ones, and the Gateway listens on the union of every address it has provisioned. No
revocation, restart, or shared address is needed to add a host on a second network:

```text
create_remote_host_package { host_id: "office-iis",  gateway_address: "192.168.2.115" }
create_remote_host_package { host_id: "hyperv-lab",  gateway_address: "172.31.224.1" }
```

Each package still dials exactly one address — its own. The persisted registry records the whole set
under `listener.addresses`, keeping `listener.address` as the first of them so a registry stays
readable by a Gateway that predates this.

Every address gets its own socket, and one failing to bind leaves the others up: a virtual switch that
is currently down costs only the hosts behind it. `doctor`'s `remote_listener` check names any address
that could not be bound; only losing all of them fails provisioning outright.

To bind every interface instead of an accumulated set, pass the optional `listen_addresses`, which
replaces the set rather than widening it — the way to drop an address that no longer exists:

```text
create_remote_host_package { host_id: "roaming", gateway_address: "192.168.2.115", listen_addresses: ["0.0.0.0"] }
```

`0.0.0.0` covers every interface and absorbs any address beside it, because binding a wildcard and a
specific address on one port collides. Entries must be IP addresses assigned to this Gateway;
`gateway_address` is always included, so the host being packaged can always reach it. `DGSPY_REMOTE_ADDRESS`
accepts the same forms as a comma-separated list for a Gateway configured entirely from the environment.
A DNS hostname in `gateway_address` names the Gateway from outside rather than one of its interfaces,
so it widens the bind to every interface.

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
equivalent CLI command is:

```powershell
dgspy pack-host --host-id win11-clean --gateway-address 192.168.250.1
```

Mutual TLS is the default. Use plaintext only for an explicitly trusted isolated network:

```powershell
dgspy pack-host --host-id win11-clean --gateway-address 192.168.250.1 --plaintext
```

The output reuses the unified release tree, so it contains the CLI and Gateway binaries as inert files;
the remote launcher starts only `dnSpy.exe` and exposes no MCP endpoint:

```text
dgSpy-remote-host-win11-clean.zip
|-- dnSpy.exe
|-- bin\dgspy.exe
|-- bin\dgSpy.Gateway.exe
|-- bin\Extensions\dgSpy\
|-- launcher\
|-- remote-host.json
|-- state\host.id
|-- state\rpc.token
`-- manifest.json
```

The same provisioning operation adds the expected `host_id` and credential to `gateway-hosts.json`.
It also persists and activates the dedicated host listener in the running Gateway. An ordinary later
`dgspy start` reads that listener configuration from the registry automatically; no Gateway restart or
manual `DGSPY_REMOTE_*` environment is required. The remote user only extracts the ZIP and runs
`dnSpy.exe`: the extension recognizes `remote-host.json`, loads the packaged identity and credentials,
and connects automatically. The launcher remains available for identity overrides, initialize-only use,
and explicit startup diagnostics. Normal startup is:

```powershell
.\dnSpy.exe
```

The optional launcher is:

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

Creating a replacement package rotates the host credential and immediately closes any connection using
the previous registration. The old credential is rejected from that point onward. Revocation likewise
removes the live route and closes an existing connection immediately; retained credential files are left
for deliberate operator cleanup and cannot authenticate after revocation.

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
127.0.0.1:7350        MCP clients
configured-IPs:7352   optional plaintext remote hosts
configured-IPs:7353   optional mutually authenticated TLS remote hosts
```

Each configured address is bound separately on both host ports, so the two ports above exist once per
address. The host listeners may be enabled together or separately. Set `DGSPY_REMOTE_DISABLE_PLAINTEXT=true`
to disable plaintext. TLS additionally requires `DGSPY_REMOTE_TLS_PORT`,
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
