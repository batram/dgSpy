# dgSpy quick start

Install dgSpy and connect it to your agent with one command. The agent starts the Gateway and dnSpy when
you first ask it to use dgSpy.

## From a GitHub release (fastest)

1. Download `dgspy-win-x64.zip` from the
   [latest release](https://github.com/batram/dgSpy/releases/latest).
2. Extract it.
3. In PowerShell, run one of:

```powershell
.\install-dgspy.exe codex
```

```powershell
.\install-dgspy.exe claude
```

Restart the agent, then say:

```text
Use dgspy and go local.
```

The agent starts dnSpy automatically. You are ready to debug.

## From a repository checkout

Install the build prerequisites once: Windows x64, Git, .NET SDK 10, and the .NET Framework 4.8
developer pack. Ordinary CoreCLR debugging also requires the exact x64 runtime used by the target to
be installed, or its matching debugger components to be available beside the target or in dgSpy's
verified private cache; dgSpy never substitutes a nearest runtime version. Initialize the submodules,
then run the same installer:

```powershell
git submodule update --init --recursive
dotnet run --project Build\DgSpyTool -- pipeline
.\install-dgspy.ps1 codex
```

The C# pipeline is authoritative for build, package, installation, and agent registration. A full
install replaces an older tree; legacy manifests are not migrated.

For development updates after the MCP is already connected, update only the bundled dnSpy host payload:

```powershell
.\install-dgspy.ps1 host-only
```

This leaves the installed CLI, running Gateway, MCP process, and agent registration untouched, so Codex
does not need to restart. Then call `launch_local_host` with `replace=true` to activate the new version.
That replacement closes only dnSpy and ends any debugging sessions it currently owns.

Host-only mode is deliberately limited to changes compatible with the installed `dgSpy.Protocol.dll`.
If the package changes RPC operations or schemas, the installer refuses before changing host files and
prints the full-install command. A protocol update must replace the shared CLI/Gateway contract and
therefore requires a Codex restart; silently mixing the old app-base contract with a new extension is
not supported.

## What the installer does

- Installs the unified self-contained dnSpy, CLI, and Gateway package under
  `%LOCALAPPDATA%\Programs\dgSpyMcp`.
- Registers the `dgspy` stdio MCP server for Codex or Claude Code.
- Verifies that the installed CLI can start, but does not start the Gateway or dnSpy.
- Leaves Gateway startup to the registered `dgspy mcp` command on first MCP use.
- Keeps mutable credentials, host configuration, logs, and debugger deployment state outside the
  immutable package tree.

Downloaded releases need no SDK, Visual Studio, administrator rights, `PATH` change, service, or
autostart task.

## Check or repair the installation

The installer intentionally does not run live diagnostics. After the agent has first used dgSpy—or
when troubleshooting manually—run:

```powershell
& "$env:LOCALAPPDATA\Programs\dgSpyMcp\cli\bin\dgspy.exe" doctor
```

- If the agent does not show dgSpy tools, restart it or reconnect MCP.
- If installation reports an incomplete package, download the complete `dgspy-win-x64.zip` again.
- To update, extract a newer release and rerun the same installer command.
- If a repository install fails after its package was built, use the printed `install --package <path> --install <path>` retry command;
  it reuses the completed directory without rebuilding.

## Watching what the agent does

dnSpy has a **dgSpy MCP Activity** tool window (View menu, or `Ctrl+Alt+M`) that lists every operation
the agent triggered, in order, with its parameters, its result or error code, and how long it took.
Select a row to read the full JSON of both sides in the pane below. It sits with Output and Locals in
the bottom tool window group.

The window is a view onto a buffer the extension always fills, so opening it mid-session shows the
calls that already happened, not just the next one. The buffer holds the last 1000 calls and each
recorded payload is capped, so a `get_raw_module` result appears truncated rather than in full. The
shared secret that authenticates the Gateway is never part of an operation's parameters and is never
recorded.

For persistent development diagnosis across Gateway or dnSpy restarts, set
`DGSPY_TRANSCRIPT_FILE` before the Gateway starts. The transcript is disabled by default and remains
separate from the sparse security audit. It records every real MCP `tools/call`, including read-only
discovery and Gateway rejection, as one correlated recursively redacted request/response JSONL record.
`get_started` and `doctor` report its effective path and bounds under `development_transcript`.

```powershell
$env:DGSPY_TRANSCRIPT_FILE = "$env:LOCALAPPDATA\dgSpy\development-transcript.jsonl"
# From a dgSpy repository checkout:
powershell -NoProfile -File tools\query-gateway-transcript.ps1 `
    -Path $env:DGSPY_TRANSCRIPT_FILE
```

Payloads and files are bounded and the previous file is retained as `.1`. Credential redaction does
not remove arbitrary source, expressions, evaluated values, or paths, so enable and retain the
development transcript deliberately.

For a debugger on another Windows machine, continue with [Remote hosts](REMOTE_HOSTS.md).
