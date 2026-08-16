# HookLab watcher installation and operation

The standalone watcher automatically applies approved, exactly guarded HookLab packages to matching
x64 desktop-CLR-v4 processes. It does not require dnSpy, the Gateway, or MCP while it runs.

## Build and install

Produce the verified dgSpy package through the supported pipeline:

```powershell
dotnet run --project Build\DgSpyTool -- pipeline
```

From its verified layout, install the watcher and register its per-user logon task:

```powershell
.\artifacts\layouts\local\hooklab-watcher\HookLab.Watcher.exe install
```

The executable requests elevation. Installation consumes only files owned by `hooklab-watcher` in the
closed `dgspy-layout.json` inventory. It verifies path containment, reparse-point absence, owner and ACL,
size, SHA-256, and the complete installed inventory. The default immutable root is
`%LOCALAPPDATA%\Programs\HookLab.Watcher`; mutable control, status, and audit state stays under
`%LOCALAPPDATA%\HookLab`.

The installer publishes by directory rename. An upgrade stops only a process whose full image path is
the installed `HookLab.Watcher.exe`, swaps the complete tree, registers and reads back the task, and
restores the previous tree and task if validation fails. Loaded resident hooks and their leased payloads
remain in target processes; no target is terminated and no remove-hook request is sent.

Use `install --no-task` for a verified manual installation. `--source`, `--install-root`, and
`--state-root` exist for controlled testing or an intentionally non-default deployment.

## Task boundary

The task is named `HookLab Watcher`. It is an interactive-token, highest-available, per-user `ONLOGON`
task, not a service. Its short `run-installed` action derives package and bootstrap paths from the
immutable installed executable directory, avoiding Task Scheduler's 261-character `/TR` limit. Writable
state is used for control, status, and bounded audit output. Task XML is read back after registration.
Watcher stdout is never a control channel: operational commands communicate through atomic control and
status files, which also avoids relying on an elevated task's unavailable console output.

## Operation

```powershell
$watcher = "$env:LOCALAPPDATA\Programs\HookLab.Watcher\HookLab.Watcher.exe"
& $watcher status
& $watcher pause
& $watcher resume
& $watcher disable-profile vmconnect-fullscreen-user
& $watcher enable-profile vmconnect-fullscreen-user
& $watcher verify-install
```

Use the installed executable for operational commands. Do not invoke `HookLab.Watcher.dll` through
`dotnet`: that bypasses the executable's elevation manifest. If an ordinary process cannot inspect an
elevated target, status fails while preserving the protected discovery record; it never converts an
inspection-access failure into an identity mismatch or quarantine decision.

Package and profile reloads activate only as complete valid catalog generations. `pause` prevents new
work without removing existing hooks. Disabling a profile does the same for that profile. A task or
watcher restart adopts authenticated live residents and reconciles desired state without reinjection.

The default notification policy is defined per profile (`all`, `errors`, or `none`). The durable
operational record is `%LOCALAPPDATA%\HookLab\watcher-audit.jsonl`, which rotates at its configured bound.
`status` reports lifecycle, discovery mode, catalog errors, and the latest bounded result set.
Persisted lifecycle is read back against the exact watcher PID and process-creation identity. A recorded
`running` process that no longer exists is reported as `stale`, with `recordedLifecycle` and
`processAlive` retained so historical state cannot masquerade as a live watcher.

## Uninstall

```powershell
& $watcher uninstall
```

Uninstall removes the task, stops only the exact installed watcher, and removes its immutable tree and
watcher-owned control/status/audit state. Use `uninstall --keep-state` to retain the latter. Resident
hooks, target processes, target binaries, and unrelated dgSpy state are deliberately untouched. Leased
resident payloads are retired only after their exact target identity exits.
