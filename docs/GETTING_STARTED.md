# dgSpy quick start

Install dgSpy and connect it to your agent with one script. The agent starts the Gateway and dnSpy when
you first ask it to use dgSpy.

## From a GitHub release (fastest)

1. Download `dgspy-win-x64.zip` from the
   [latest release](https://github.com/batram/dgSpy/releases/latest).
2. Extract it.
3. In PowerShell, run one of:

```powershell
.\install-dgspy.ps1 codex
```

```powershell
.\install-dgspy.ps1 claude
```

Restart the agent, then say:

```text
Use dgspy and go local.
```

The agent starts dnSpy automatically. You are ready to debug.

## From a repository checkout

Install the build prerequisites once: Windows x64, Git, .NET SDK 10, and the .NET Framework 4.8
developer pack. Initialize the submodules, then run the same installer:

```powershell
git submodule update --init --recursive
.\install-dgspy.ps1 codex
```

Use `claude` instead of `codex` for Claude Code. The installer builds the complete package first; no
separate build, packaging, extraction, or MCP configuration command is needed.

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

For a debugger on another Windows machine, continue with [Remote hosts](REMOTE_HOSTS.md).
