# Central Gateway remote hosts

Build a deploy-only x64 host with `.\pack-remote-host.ps1`. The resulting archive includes the
self-contained net10 dnSpy runtime, matching dgSpy extension, process-scoped launcher, and hash
manifest; it deliberately excludes the Gateway. See the [build baseline](DGSPY_BASELINE.md) for bundle
deployment and state handling.

The supported routing shape keeps every dgSpy extension on `127.0.0.1`. A central Gateway connects to
remote extensions through local SSH tunnel ports and routes MCP tools by stable `host_id`.

This currently secures the Gateway-to-extension hop. The Gateway itself still accepts only loopback MCP
clients; client identity, leases, permissions, audit, and HTTPS remain roadmap work.

## Configure a debugger host

Set a stable identity and RPC credential in the environment that starts dnSpy:

```powershell
$env:DGSPY_HOST_ID = 'uch-dev'
$env:DGSPY_RPC_TOKEN = '<random per-host secret>'
```

The extension continues to listen only on `127.0.0.1:7351`.

## Open the tunnel on the Gateway machine

```powershell
ssh -N -L 127.0.0.1:7451:127.0.0.1:7351 user@debug-host
```

Use a different local port for every host. SSH supplies encryption and server authentication; configure
normal SSH host-key verification and key-based user authentication rather than disabling either.

## Register hosts

Create a registry such as `hosts.json`. Credentials stay in environment variables or separate files;
do not put token values directly in the registry.

```json
{
  "hosts": [
    {
      "host_id": "local-dev",
      "display_name": "Local dnSpy",
      "address": "127.0.0.1",
      "port": 7351,
      "token_environment": "DGSPY_RPC_TOKEN_LOCAL"
    },
    {
      "host_id": "uch-dev",
      "display_name": "UCH debugger host",
      "address": "127.0.0.1",
      "port": 7451,
      "token_environment": "DGSPY_RPC_TOKEN_UCH"
    }
  ]
}
```

Start the central Gateway with the registry and referenced credentials:

```powershell
$env:DGSPY_HOSTS_FILE = 'C:\path\to\hosts.json'
$env:DGSPY_RPC_TOKEN_LOCAL = '<local host secret>'
$env:DGSPY_RPC_TOKEN_UCH = '<UCH host secret>'
dotnet run --project .\dgSpy.Gateway\dgSpy.Gateway.csproj -c Release
```

`token_file` may replace `token_environment`; relative paths resolve beside the registry. The Gateway
rejects non-loopback addresses, duplicate identities, missing credentials, invalid ports, unknown hosts,
and host identities that do not match the authenticated extension handshake.

Call `list_hosts` to inspect configured connection state. Pass `host_id` to every other tool. When exactly
one host is registered, omitting it retains the local single-host convenience behavior.
