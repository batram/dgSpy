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
6. The resident runtime compiles and applies Prefix and Postfix methods directly. Finalizer and
   Transpiler remain later slices.
7. Edit, recompile, enable, disable, inspect, or remove hooks while the target keeps running.

Initialization is idempotent. Hook operations fail with `hooklab_not_initialized` and point to
`initialize_hooklab`; they never start an implicit debugger workflow.

## Public operations

- `initialize_hooklab`
- `get_hooklab_status`
- `get_hook_template`
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

The resident-runtime foundation is implemented and package-proven. MCP and GUI expose idempotent
initialization and status; the x64 native bootstrap initializes an ordinary CLR v4 process without a
caller-supplied carrier, while the managed carrier path remains a bounded fallback. Initialization
preserves the target's running or paused state, verifies the resident pipe, and retains it with zero
hooks. Every observational hook, including the first, then installs and removes through that pipe while
the target runs. Carrier, stop, atomic-action, and payload-generation details are absent from the public
schema.

The verified observational product exposes `install_hook`, `list_hooks`, `get_hook_events`,
`remove_hook`, and `remove_all_hooks` for guarded Prefix, Postfix, and Finalizer recording hooks. The
GUI adopts a target attached directly through dnSpy, initializes automatically from Add Hook, receives
events, and removes one or all hooks through the same service. Commit `c1258b616` completed the single
immutable C# build/package/install pipeline; its exact 1,902-file package passed the final CorDebug gate,
including 414 debugger checks, 52 atomic-action checks, and 49 HookLab GUI/install checks.

Resident source compilation is now package-proven for complete C# units containing one public static
Prefix, one public static Postfix, or one of each. Harmony supplies named argument binding, `ref`
argument mutation, `__instance`, `__args`, `__result`, paired `__state`, and `___fieldName` injection.
HookLab compiles before mutation, requires monotonically increasing revisions, preserves the last good
revision after compiler failure, permits Prefix/Postfix phase replacement on the same exactly guarded
method, lists compiled revision state, and removes every method in a paired patch. The disposable
Windows PowerShell acceptance target proves create, update, failed-update rollback, and removal while
the target runs.

`get_hook_template` now supplies editable no-op Prefix, Postfix, or paired Prefix/Postfix C# for an
exactly selected non-generic method. It derives the declaring type, original parameter names and
CLR by-reference arguments as Harmony-compatible `ref` parameters, `__instance`, `__result`, and
paired `__state` directly from module metadata. Missing or invalid C# parameter names receive stable
positional names, while keywords are escaped.
The packaged PowerShell acceptance proves both static and instance template shapes and proves that
this read-only operation does not initialize HookLab or alter the target. Generated templates compile
and patch through the same resident Harmony path as hand-written source, including a real method with
both `ref` and original `out` parameters. Invalid template selections, non-method tokens, and generic
targets fail with stable public errors.

The first GUI source-editor slice is complete. **Create Custom Hook...** on the selected dnSpy method
opens the shared generated Prefix, Postfix, or paired Prefix/Postfix source, permits editing the hook
ID and complete C# unit, and compiles and installs revision 1 through the same service as MCP. Compiler
failure leaves the dialog and entered source open, and installed rows distinguish compiled C# from
observational hooks and show the revision. Invoking the editor again for its default installed ID now
reopens the installed source and automatically submits revision N+1 instead of exposing the resident
revision invariant to the user. HookLab dialogs use dnSpy's native themed window style. Explicit row
Edit actions, enable/disable without removal, Finalizer, Transpiler, generic-target handling, and broader
compilation references remain unfinished. Initialization and packaging are no longer the active design
problem.

## One implementation through-line

### 1. Initialization product operation - complete

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

The shipping x64 path uses the native bootstrap first and retains the managed evaluation/carrier path as
a bounded fallback. Carrier choice is internal and never tied to the method the user later chooses to
hook.

### 2. Route every hook through the resident runtime - complete for observational hooks

Delete the first-hook prepare/commit special case. Once initialized, the first hook and every later hook
use the same pipe `install` command. Creating, editing, toggling, and removing hooks must not pause or
resume the target.

Keep exact MVID, token, signature, and IL digest checks. Resolve the guarded method to a live
`MethodBase` inside the resident runtime before compiling or patching. Hook IDs are stable and
conflicting reuse is refused.

### 3. Add resident arbitrary C# hooks - complete for Prefix and Postfix

Follow UnityExplorer's useful model: each hook owns editable source, compiled patch methods, target
identity, enabled state, diagnostics, and Harmony patch handles. Provide templates for call logger,
Prefix, Postfix, Finalizer, Transpiler, and custom C#.

Start with one complete compiled Prefix path rather than implementing every phase incompletely. Define
a resident hook record containing stable ID, guarded target identity, source, revision, compiled patch,
enabled state, bounded diagnostics, and the last successful revision. Compile and validate a candidate
before changing the active patch; a failed create changes nothing, and a failed update leaves the
previous working hook installed. Then add enable/disable without deleting source or diagnostics.

Verified Prefix/Postfix source may use Harmony conventions including named original arguments, `ref`
argument mutation, `__instance`, `__args`, `__result`, paired `__state`, `___fieldName` injection, and
a boolean Prefix that skips the original. Compilation happens in the target context. Compiler errors
are bounded structured diagnostics. Finalizer and Transpiler remain later phases; do not add custom
binding or IL machinery that duplicates Harmony.

Do not reintroduce a generic provider framework, export-project system, credential lifecycle, or
cross-process compiler service before the resident compiler proves it is needed.

### 4. Make the GUI a hook editor - create slice complete

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
- native autonomous initialization plus a bounded managed fallback;
- `initialize_hooklab` and `get_hooklab_status` in MCP and GUI;
- read-only `get_hook_template` for metadata-derived Prefix/Postfix source without initialization;
- `Prepare()` creates and retains a resident runtime and pipe without a hook;
- resident `install`, `uninstall`, `status`, and event drain;
- exact guards, bounded observer events, cleanup, packaging, and host-only deployment;
- shared GUI/MCP service and live-tested observational Prefix/Postfix/Finalizer hooks;
- one immutable C# build, package, install, and registration pipeline with package-level composition and
  live CorDebug coverage.

Still missing:

- an explicit Edit action for arbitrary selected compiled rows (default-ID reopen already increments);
- enable/disable state that preserves source and diagnostics;
- public `create_hook`, `update_hook`, `enable_hook`, and `disable_hook` operations;
- matching editable source and diagnostics in GUI and MCP hook state;
- compiled Finalizer and Transpiler after Prefix/Postfix are proven;
- generic-target handling and broader compilation references when a concrete hook requires them;
- package-level acceptance for toggling; behavior-changing source, failed-update rollback, removal,
  and clean detach are already proven against an ordinary CLR v4 PowerShell target.

## Execution rule

One capable agent owns this through-line at a time. Keep the repository buildable at useful
checkpoints, test real process boundaries, and update `docs/local/HOOKLAB_CURRENT_STATUS.md` before a
context compression or handoff. Do not restart from archived plans or split this into tiny tasks.
