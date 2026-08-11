# HookLab implementation plan

## Product goal

HookLab brings UnityExplorer-style Harmony hook creation to any attached x64 CLR v4 process. dgSpy
gets HookLab into the process once. After that, the resident runtime compiles, applies, edits, toggles,
observes, and removes hooks without debugger stops.

The GUI and MCP are two front ends over the same service and resident hook registry. No public
operation exposes carrier methods, atomic actions, prepare/commit, payload generations, or pipe details.

## Finished workflow

1. Attach dgSpy to an x64 CLR v4 process.
2. Run **Initialize HookLab** in the GUI or call MCP `initialize_hooklab`.
3. HookLab autonomously finds a usable managed evaluation point, injects the resident runtime, verifies
   its command channel, and restores the target's previous running or paused state.
4. Select a method in dnSpy or identify it through MCP.
5. Create a hook from a template or editable C# source.
6. The resident runtime compiles and applies Prefix, Postfix, Finalizer, or Transpiler methods directly.
7. Edit, recompile, enable, disable, inspect, or remove hooks while the target keeps running.

Initialization is idempotent. Hook operations fail with `hooklab_not_initialized` and point to
`initialize_hooklab`; they never start an implicit debugger workflow.

## Public operations

- `initialize_hooklab`
- `get_hooklab_status`
- `create_hook`
- `update_hook`
- `enable_hook`
- `disable_hook`
- `list_hooks`
- `get_hook_events`
- `remove_hook`
- `remove_all_hooks`

`install_hook` may remain temporarily as a compatibility alias while `create_hook` lands. Hook creation
takes exact method identity from dgSpy discovery plus source or a built-in template.

## Current checkpoint

The first architectural slice is implemented and live-proven against the net48 fixture: MCP and GUI
expose initialization; the host samples for an evaluable frame and falls back to an internally selected
managed carrier; it preserves run state, loads and starts a zero-hook resident runtime, verifies its
pipe, and retains it independently of hook count. The first hook then installs, emits events, and removes
through that pipe while the target runs. Carrier and stop fields are gone from its public schema. The
GUI now adopts a target attached directly through dnSpy and Add Hook initializes automatically. MCP
controller contention is warning-first, with an explicit `claim_session(force=true)` lease-only takeover;
extending that same ownership boundary to human GUI execution commands remains required.
The next live expansion is the same hook proof against an ordinary attached PowerShell.

## One implementation through-line

### 1. Make initialization a product operation

Reuse the existing verified bootstrap, embedded payload, evaluation machinery, and resident pipe.
`HookLabBootstrap.Prepare()` already creates the runtime and endpoint without installing a hook; make
that the initialization boundary.

`initialize_hooklab` owns the complete injection procedure:

- detect and return an existing resident runtime;
- preserve the target's initial running or paused state;
- choose and reach a usable managed evaluation point without caller-supplied carrier details;
- run bootstrap preparation and its small worker-start commit once;
- connect to the endpoint and verify `status`;
- retain host state even when zero hooks exist;
- return `initialized`, `already_initialized`, or one actionable failure;
- clean temporary breakpoints, leases, and partial endpoints on every exit.

Carrier selection is internal. Prefer an already-paused evaluable managed frame. Otherwise perform a
bounded search using current thread/frame/evaluability information and temporary dgSpy-owned
breakpoints. Never tie the carrier to the method the user later chooses to hook.

### 2. Route every hook through the resident runtime

Delete the first-hook prepare/commit special case. Once initialized, the first hook and every later hook
use the same pipe `install` command. Creating, editing, toggling, and removing hooks must not pause or
resume the target.

Keep exact MVID, token, signature, and IL digest checks. Resolve the guarded method to a live
`MethodBase` inside the resident runtime before compiling or patching. Hook IDs are stable and
conflicting reuse is refused.

### 3. Add resident arbitrary C# hooks

Follow UnityExplorer's useful model: each hook owns editable source, compiled patch methods, target
identity, enabled state, diagnostics, and Harmony patch handles. Provide templates for call logger,
Prefix, Postfix, Finalizer, Transpiler, and custom C#.

Patch source may use Harmony conventions including `__instance`, `__args`, `__result`, `__exception`,
field injection, and a boolean Prefix that skips the original. Compilation happens in the target
context. Compiler errors are bounded structured diagnostics. A failed compile or replacement leaves
the previous working hook intact.

Do not reintroduce a generic provider framework, export-project system, credential lifecycle, or
cross-process compiler service before the resident compiler proves it is needed.

### 4. Make the GUI a hook editor

The HookLab window first shows Not initialized, Initializing, Ready, or Failed with Retry. It provides
**Initialize HookLab** and may offer initialization when Create Hook is chosen.

Once ready, `Add Hook...` opens an editor with templates and source. Hook rows show Compiling, Enabled,
Disabled, or Failed and provide Edit, Enable/Disable, and Remove. Diagnostics and runtime events remain
visible without consulting debugger output. The GUI calls the shared service directly, never MCP.

### 5. Verify the real vertical slice

Keep tests for bootstrap with zero hooks, initialization idempotency, state restoration, pipe-only first
install, compiler diagnostics, replacement rollback, toggling, and removal. Keep boundary tests for MCP
schemas, MEF composition, and GUI commands.

The hidden-desktop acceptance run must attach to an ordinary x64 CLR v4 PowerShell or fixture without
helper code, initialize with no caller-supplied carrier, restore run state, compile behavior-changing
hooks while running, prove compiler-failure rollback, show the same state in GUI and MCP, edit/toggle/
remove without debugger stops, detach cleanly, and run the focused product gates.

## Current starting point

Already reusable:

- verified bootstrap with embedded probe and Harmony;
- `Prepare()` creates a resident runtime and pipe without a hook;
- resident `install`, `uninstall`, `status`, and event drain;
- exact guards, bounded observer events, cleanup, packaging, and host-only deployment;
- shared GUI/MCP service and live-tested observational Prefix/Postfix/Finalizer hooks.

Still missing:

- public autonomous initialization and status;
- production carrier selection without caller guidance;
- removal of the first-hook commit path;
- resident arbitrary C# compilation and editable hook state;
- UnityExplorer-style edit/toggle/diagnostic GUI and MCP operations.

## Execution rule

One capable agent owns this through-line at a time. Keep the repository buildable at useful
checkpoints, test real process boundaries, and update `docs/local/HOOKLAB_CURRENT_STATUS.md` before a
context compression or handoff. Do not restart from archived plans or split this into tiny tasks.
