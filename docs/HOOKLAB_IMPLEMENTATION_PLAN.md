# HookLab implementation plan

## Product goal

HookLab brings UnityExplorer-style Harmony hook creation to any attached x64 CLR v4 process. dgSpy
gets HookLab into the process once. After that, the resident runtime compiles, applies, edits, toggles,
observes, and removes hooks without debugger stops.

The GUI and MCP are two front ends over the same service and resident hook registry. No public
operation exposes carrier methods, atomic actions, prepare/commit, payload generations, or pipe details.

## Finished workflow

1. Attach dgSpy to an x64 CLR v4 process.
2. Optionally run **Initialize HookLab** in the GUI or call MCP `initialize_hooklab`; the first create
   or compatibility-install operation can invoke the same autonomous initialization path lazily.
3. HookLab autonomously finds a usable managed evaluation point, injects the resident runtime, verifies
   its command channel, and restores the target's previous running or paused state.
4. Select a method in dnSpy or identify it through MCP.
5. Create a hook from a template or editable C# source.
6. The resident runtime compiles and applies Prefix, Postfix, Finalizer, and Transpiler methods directly.
7. Edit, recompile, enable, disable, inspect, or remove hooks while the target keeps running.

Initialization is idempotent. Status, toggle, event, and removal operations do not create a resident
runtime. A create or compatibility-install operation initializes lazily when needed, but carrier,
debugger-stop, bootstrap, and payload details remain internal to that initialization operation.

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

`install_hook` remains the observational and compatibility operation; explicit compiled-hook lifecycle
uses `create_hook` and `update_hook`. Hook creation takes exact method identity from dgSpy discovery plus
source or a built-in template.

## Current checkpoint

The resident-runtime foundation is implemented and package-proven. MCP and GUI expose idempotent
initialization and status; the x64 native bootstrap initializes an ordinary CLR v4 process without a
caller-supplied carrier, while the managed carrier path remains a bounded fallback. Initialization
preserves the target's running or paused state, verifies the resident pipe, and retains it with zero
hooks. Every observational hook, including the first, then installs and removes through that pipe while
the target runs. Carrier, stop, atomic-action, and payload-generation details are absent from the public
schema.

The observational product exposes `install_hook`, `list_hooks`, `get_hook_events`, `remove_hook`, and
`remove_all_hooks` for guarded Prefix, Postfix, and Finalizer recording hooks. The GUI adopts a target
attached directly through dnSpy, can initialize explicitly or on first creation, receives events, and
removes one or all hooks through the same service. Commit `c1258b616` completed the single immutable C#
build/package/install pipeline. The latest exact 1,902-file package passed the final CorDebug gate,
including 414 debugger checks, 52 atomic-action checks, and 52 HookLab GUI/install checks.

Resident source compilation is now package-proven for complete C# units containing one public static
Prefix, Postfix, or Finalizer method, or paired Prefix/Postfix methods. Harmony supplies named argument
binding, `ref`
argument mutation, `__instance`, `__args`, `__result`, paired `__state`, and `___fieldName` injection.
HookLab compiles before mutation, requires monotonically increasing revisions, preserves the last good
revision after compiler failure, permits Prefix/Postfix phase replacement on the same exactly guarded
method, lists compiled revision state, and removes every method in a paired patch. The disposable
Windows PowerShell acceptance target proves create, update, failed-update rollback, and removal while
the target runs.

`get_hook_template` supplies editable no-op Prefix, Postfix, paired Prefix/Postfix,
exception-preserving Finalizer, or identity Transpiler C# for an exactly selected non-generic method.
It derives the declaring type, original parameter names and
CLR by-reference arguments as Harmony-compatible `ref` parameters, `__instance`, `__result`, and
paired `__state` directly from module metadata. Missing or invalid C# parameter names receive stable
positional names, while keywords are escaped.
The packaged PowerShell acceptance proves both static and instance template shapes and proves that
this read-only operation does not initialize HookLab or alter the target. Generated templates compile
and patch through the same resident Harmony path as hand-written source, including a real method with
both `ref` and original `out` parameters. Invalid template selections, non-method tokens, and generic
targets fail with stable public errors.

The first GUI source-editor and public compiled-hook lifecycle slice is complete. **Create Custom Hook...**
on the selected dnSpy method opens the shared generated Prefix, Postfix, or paired Prefix/Postfix source,
permits editing the hook
ID and complete C# unit, and compiles and installs revision 1 through the same service as MCP. Compiler
failure leaves the dialog and entered source open, and installed rows distinguish compiled C# from
observational hooks and show the revision. Invoking the editor again for its default installed ID now
reopens the installed source and automatically submits revision N+1 instead of exposing the resident
revision invariant to the user. HookLab dialogs use dnSpy's native themed window style. Hooked methods
show a dedicated teal hook glyph on the method-definition line; hover summarizes the method's hooks,
left-click opens HookLab and selects the exact module/token row, and the glyph context menu provides
**Show in HookLab**. Any selected compiled row can now be reopened with **Edit**, preserving its exact
ID and source and automatically advancing to revision N+1; observer rows remain non-editable. Hook rows
show and toggle Enabled/Disabled state without discarding source, revision, compiled methods, or target
identity. MCP now exposes explicit `create_hook` and `update_hook` operations: create accepts only revision
1 and refuses an existing ID, while update requires an existing hook, a higher revision, and the same
exactly guarded target. Both return editable source and successful diagnostic state; compiler failure
leaves the last good revision untouched. `install_hook` remains the observational and compatibility path.
The GUI labels observation hooks separately from custom C# hooks. Row menus expose every real row action,
including removal. A single-hook method glyph behaves like a breakpoint toggle and provides Edit,
Enable/Disable, and Remove; a multi-hook glyph opens HookLab instead of applying an ambiguous mutation.
Enabled methods use the bright teal glyph; an all-disabled method uses a muted slashed glyph, so the
clickable margin reflects runtime state without opening HookLab. Methods with multiple hooks add a
high-contrast stack badge in either state so an ambiguous click is visually apparent before interaction.
The C# source surface now uses dnSpy's native code editor rather than a wrapping WPF text box. It owns
the native text buffer and view for the dialog lifetime, uses normal code-editor scrolling, selection,
clipboard, undo, and indentation behavior, and round-trips generated or installed source through the
same create/update lifecycle. A Roslyn workspace, semantic diagnostics, completion, and IntelliSense
remain deliberately out of scope.
The toolbar reflects actionable state: initialization becomes a disabled **HookLab Initialized** state
once ready, **Remove** requires a selected row, and **Remove All** requires at least one installed hook.
Generic-target handling and any demonstrated need for broader compilation references remain deferred.
Initialization and packaging are no longer the active design problem.

## Deferred editor UX backlog

These are worthwhile improvements, but neither belongs on the current HookLab implementation path.

- **Roslyn-backed custom-hook editing.** The dialog currently uses dnSpy's native C# text view without
  a Roslyn project. A later slice can import the exported `ILanguageCompilerProvider`, initialize a
  one-document C# project, and host the resulting `ICodeDocument` to gain semantic highlighting,
  completion, signature help, and advisory diagnostics. Give that editor project metadata references
  for the selected target module, its resolvable dependencies, framework assemblies, and the pinned
  Harmony assembly. Continue sending the document's raw source to HookLab's target-side CodeDOM
  compiler: resident compilation and installation remain authoritative because they see the target
  AppDomain, and Roslyn may accept newer syntax than that compiler. Do not couple this work to dnSpy's
  assembly-rewriting Edit Class workflow.
- **Natural generated parameter names.** Templates currently render every original method parameter as
  a C# verbatim identifier, such as `@input`. This is valid and Harmony binds the compiled name as
  `input`, but the blanket escaping is noisy and differs from normal Harmony examples. A later cleanup
  should emit ordinary valid non-keyword names unchanged, use `@` only for C# keywords, retain `__N`
  fallbacks for missing or invalid metadata names, and prevent collisions with other parameters and
  Harmony's reserved injected names. Keep the passive `void Prefix(...)` default; behavior-replacing
  `bool Prefix(ref T __result, ...)` is better offered as a separate explicit template.

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

### 2. Route every hook through the resident runtime - complete

The first-hook prepare/commit special case is gone. Initialization may establish the resident runtime;
once initialized, the first hook and every later hook use the resident command channel. Creating,
editing, toggling, and removing hooks do not pause or resume the target.

Keep exact MVID, token, signature, and IL digest checks. Resolve the guarded method to a live
`MethodBase` inside the resident runtime before compiling or patching. Hook IDs are stable and
conflicting reuse is refused.

### 3. Add resident arbitrary C# hooks - complete for Prefix, Postfix, Finalizer, and Transpiler

Follow UnityExplorer's useful model: each hook owns editable source, compiled patch methods, target
identity, enabled state, diagnostics, and Harmony patch handles. Provide Prefix, Postfix, paired
Prefix/Postfix, Finalizer, and Transpiler templates plus complete custom C# source.

Start with one complete compiled Prefix path rather than implementing every phase incompletely. Define
a resident hook record containing stable ID, guarded target identity, source, revision, compiled patch,
enabled state, bounded diagnostics, and the last successful revision. Compile and validate a candidate
before changing the active patch; a failed create changes nothing, and a failed update leaves the
previous working hook installed. Enable/disable preserves that record and does not recompile on enable;
updating a disabled compiled hook keeps its successful new revision disabled until explicitly enabled.

Verified Prefix/Postfix source may use Harmony conventions including named original arguments, `ref`
argument mutation, `__instance`, `__args`, `__result`, paired `__state`, `___fieldName` injection, and
a boolean Prefix that skips the original. Compilation happens in the target context. Compiler errors
are bounded structured diagnostics. Finalizer can preserve, replace, or suppress an exception through
Harmony's `__exception` convention. Transpiler uses Harmony's `IEnumerable<CodeInstruction>` convention
directly, and its generated template is an identity transform. Do not add custom binding or IL machinery
that duplicates Harmony.

Do not reintroduce a generic provider framework, export-project system, credential lifecycle, or
cross-process compiler service before the resident compiler proves it is needed.

### 4. Make the GUI a hook editor - complete for the current scope

The HookLab window exposes explicit initialization and reflects ready state. Selecting a method and
creating a hook can also invoke the same autonomous initialization path when necessary.

**Add Observation Hook...** opens bounded observation options, while **Create Custom C# Hook...** opens
the native C# source editor with the supported templates. Rows expose Enabled/Disabled state and real
Edit, Enable/Disable, Show, and Remove actions where applicable. Runtime events remain visible without
consulting debugger output. The GUI calls the shared service directly, never MCP.

### 5. Preserve real vertical-slice acceptance

Keep tests for bootstrap with zero hooks, initialization idempotency, state restoration, pipe-only first
install, compiler diagnostics, replacement rollback, toggling, and removal. Keep boundary tests for MCP
schemas, MEF composition, and GUI commands.

The hidden-desktop acceptance attaches to an ordinary x64 CLR v4 target without helper code, initializes
with no caller-supplied carrier, restores run state, compiles behavior-changing hooks while running,
proves compiler-failure rollback and toggling, exercises packaged GUI visibility/Edit/removal, detaches
cleanly, and runs the focused product gates. Keep those boundaries covered as later slices land.

## Completed foundation and remaining boundaries

Already reusable:

- verified bootstrap with embedded probe and Harmony;
- native autonomous initialization plus a bounded managed fallback;
- `initialize_hooklab` and `get_hooklab_status` in MCP and GUI;
- read-only `get_hook_template` for metadata-derived Prefix, Postfix, paired Prefix/Postfix, Finalizer,
  and Transpiler source without initialization;
- `Prepare()` creates and retains a resident runtime and pipe without a hook;
- resident `install`, `uninstall`, `status`, and event drain;
- exact guards, bounded observer events, cleanup, packaging, and host-only deployment;
- shared GUI/MCP service and live-tested observational Prefix/Postfix/Finalizer hooks;
- one immutable C# build, package, install, and registration pipeline with package-level composition and
  live CorDebug coverage.

Still deferred:

- generated templates for generic methods and methods on generic declaring types;
- broader resident compiler references only when a concrete hook demonstrates that the current
  target-AppDomain reference set is insufficient;
- the explicitly parked editor UX items above.

## Verification and build-boundary seam

The explicit Edit action is covered by the packaged GUI smoke through an unchanged-source revision-2
round trip. Behavior-changing create/update, compiler rollback, enable/disable, removal, and clean detach
are package-proven through the PowerShell acceptance target. Retain `install_hook` for observational
hooks and compatibility; use `create_hook` and `update_hook` for explicit compiled lifecycle semantics.

The remaining build dependency cleanup from [BUILD_PIPELINE_TODO.md](BUILD_PIPELINE_TODO.md) is also
complete: the packaged component build compiles `dgSpy.Extension` against explicit dnSpy contract and
compile-dependency file references from immutable `host-raw`, while dgSpy and HookLab dependencies
remain normal project references. The global `BuildProjectReferences=false` workaround is gone and a
structural regression test protects the boundary.

Compiled Finalizer and Transpiler are complete vertical slices on the intended build graph.

## Execution rule

One capable agent owns this through-line at a time. Keep the repository buildable at useful
checkpoints, test real process boundaries, and update `docs/local/HOOKLAB_CURRENT_STATUS.md` before a
context compression or handoff. Do not restart from archived plans or split this into tiny tasks.
