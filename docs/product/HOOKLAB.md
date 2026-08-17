# HookLab

HookLab adds exactly guarded Harmony hooks to an attached x64 desktop CLR v4 process. It supports direct
authoring through dgSpy and automatic application through the separately installed HookLab watcher.
CoreCLR-only targets, x86, and Mono/Unity residents are outside the current HookLab boundary.

## Resident model

dgSpy initializes one resident HookLab runtime in the selected process. Initialization is idempotent,
selects its own safe arrival path, verifies the resident command channel, and restores the process's
previous running or paused state. Creating the first hook can initialize the resident lazily.

The resident managed assemblies remain loaded until the target process or AppDomain exits. Removing all
hooks removes their behavioral effect but is not an assembly unload operation.

Every target method is guarded by exact module MVID, metadata token, signature, and original IL SHA-256.
The public `module_id` used to select the live module is opaque and session-scoped; rediscover it after
every attach and fail on zero or ambiguous resolution.

## Supported hooks

HookLab supports:

- bounded observation Prefix, Postfix, and Finalizer hooks;
- compiled custom C# Prefix and Postfix hooks, including a paired Prefix/Postfix source document;
- compiled exception-preserving, replacing, or suppressing Finalizers;
- compiled Harmony `IEnumerable<CodeInstruction>` Transpilers.

Harmony supplies its normal binding conventions, including named original arguments, `ref` mutation,
`__instance`, `__args`, `__result`, paired `__state`, `___fieldName`, `__exception`, and
`CodeInstruction`. HookLab does not add a parallel argument-binding or IL-rewriting layer.

Compilation occurs in the target context. A candidate is compiled and validated before it replaces the
active patch. A failed create changes nothing; a failed update preserves the last good source, revision,
enabled state, diagnostics, and active behavior. Revisions increase monotonically. Enable and disable
preserve compiled state and do not recompile on enable.

Observation and compiled hooks may coexist on the same method and phase without removing each other's
Harmony ownership.

## Product surfaces

The MCP surface includes:

- `initialize_hooklab` and `get_hooklab_status`;
- `get_hook_template`;
- `install_hook`, `create_hook`, and `update_hook`;
- `list_hooks`, `enable_hook`, and `disable_hook`;
- `get_hook_events`;
- `remove_hook` and `remove_all_hooks`;
- `export_hook_package`.

The GUI uses the same service directly rather than calling MCP. Method context commands create
observation or custom C# hooks. The HookLab window exposes real initialization, editing, enable/disable,
show, event, and removal operations. Observation rows are deliberately non-editable. Method glyphs show
single or multiple installed hooks without guessing which row a click should mutate.

## Standalone watcher

`HookLab.Injector` is the package-neutral standalone injection and reconciliation boundary.
`HookLab.Watcher` validates immutable packages and editable profiles, discovers matching processes,
applies desired hooks, adopts authenticated residents, records bounded status/audit output, and supports
pause and profile enable/disable. The separately packaged, unelevated `HookLab.Watcher.Companion`
presents exact lifecycle state, pause/resume, profile enable/disable, installed-task start/restart, and
the bounded audit tail in the notification area. Exiting the companion does not stop the elevated,
headless watcher. Both run without dnSpy, the Gateway, or MCP.

The supported package contains a closed watcher layout and installer. See
[HookLab watcher installation and operation](../guides/HOOKLAB_WATCHER.md) for installation, task supervision,
commands, upgrade behavior, and uninstall guarantees.

`export_hook_package` freezes one retained compiled dgSpy hook into a canonical watcher deployment below
`DGSPY_EXPORT_ROOT`. It revalidates the exact live process and guarded method identity, writes a closed
package, and creates a disabled sibling profile. Export never enrolls or enables automatic deployment.
The watcher can atomically enroll that canonical export into its separately protected enrollment root.
Enrollment verifies the deployment again, rejects identity conflicts, and durably disables the profile
before publication; automatic application begins only after an explicit `enable-profile` command.

The debugger-integrated and standalone adapters currently retain separate resident ownership paths.
Neither may inject a competing generation merely because it cannot adopt a resident it discovered.
Authenticated cross-adoption and preservation of foreign-owned hooks remain roadmap work.

## Current limitations

- Generated templates do not support generic methods or methods on generic declaring types.
- Resident compilation references expand only for a concrete target the current target-AppDomain set
  cannot compile.
- Source size remains bounded by the bootstrap parameter contract.
- The editor provides C# classification and normal editing, but not a HookLab-owned Roslyn project,
  semantic completion, or advisory Roslyn diagnostics.
- Generated parameters use conservative verbatim identifiers rather than polished conditional escaping.
- Cross-adoption between watcher-created and dgSpy-created residents is not yet a supported workflow.

These limitations are recorded and ordered in [Dream of roads](../roadmap/dream_of_roads.md). Detailed tasks,
investigations, handoffs, and live evidence belong in the separate `docs/local` work repository.

## Safety invariants

- Initialization and hook mutations must preserve the target's prior execution state unless the caller
  explicitly requests a debugger state change.
- Disconnect, timeout, watcher exit, upgrade, and uninstall do not implicitly resume, detach, terminate,
  restart, remove hooks, or modify target binaries.
- Unknown and foreign-owned resident hooks are preserved.
- Exact target guards fail closed; display names and first matches are never identity.
- Elevated watcher inputs come only from closed, verified, immutable installation or enrollment
  inventories. Mutable control, status, and audit state remains separate.
- Loaded resident payloads are retired only after their exact target identity exits.
