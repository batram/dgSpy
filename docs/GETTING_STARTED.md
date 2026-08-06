# dgSpy quick start

Build and start from a checkout:

```powershell
.\build-dgspy.ps1 -NoDeploy
.\Start-dgSpy.ps1 start
.\Start-dgSpy.ps1 doctor
```

For normal use, publish `pack-dgspy.ps1`, extract the resulting ZIP, and register its `dgspy.exe mcp`
command with the MCP client. `dgspy configure codex --apply` performs that one-time per-user registration;
new sessions then start or reuse the loopback Gateway automatically. URL-only clients can use `dgspy start`
and may need an MCP reconnect after a late start.

## Deploy a debugger host

Local deployment installs an immutable per-user dnSpy version and keeps one rollback version:

```powershell
dgspy deploy-local --source C:\path\to\packaged\dnSpy --version 1.0.0
```

Add `--desktop-shortcut` or `--start-menu-shortcut` only when wanted. Installation does not change
`PATH`, install a service, or create an autostart task.

Remote deployment creates but does not transfer or execute a self-contained host ZIP:

```powershell
dgspy pack-host --host-id lab-pc --gateway-address 192.168.1.10
```

Restart the Gateway after adding or revoking a remote host so it reloads the registry. The CLI output
reports this explicitly; the next client-spawned start automatically includes both managed local and
provisioned remote hosts.

Agents should begin with `get_started`, use `doctor` for failures, and call the matching plan tool before
either deployment mutation. Detach safely before closing dnSpy.

## Debugging

`step_into`, `step_over`, and `step_out` remain the primitives. `step_and_inspect` combines one step,
the event wait, stack, frame, watches, and exception. `trace_calls` is bounded best-effort managed tracing:
optimized/inlined code, native/runtime calls, missing sequence points, and async thread changes can hide calls.
