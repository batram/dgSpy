# dgSpy extension-provider and runtime-hooking MCP plan

This plan connects optional feature providers such as HookLab to dgSpy without making the core
extension depend on them. HookLab's runtime design is specified in
[HOOKLAB_IMPLEMENTATION_PLAN.md](HOOKLAB_IMPLEMENTATION_PLAN.md); timing-sensitive target bootstrap is
specified in [DGSPY_ATOMIC_ACTIONS_PLAN.md](DGSPY_ATOMIC_ACTIONS_PLAN.md).

## Stable provider contract and composition

Add a small `dgSpy.ExtensionContracts` assembly containing immutable DTOs and a minimal
`IDgSpyExtensionProvider` interface. It contains no WPF, dnSpy implementation, Gateway, or HookLab
types. The dgSpy extension discovers providers through optional MEF `[ImportMany]` lazy imports and
must compose and operate with zero providers.

A missing, malformed, or throwing provider is quarantined and reported as degraded or unavailable;
it must never compose dgSpy, its menus, or its RPC service away silently. Composition tests cover zero
providers and deliberately broken providers.

Each provider descriptor includes:

- provider ID, semantic version, and contract version;
- operation name and versioned JSON input/result schemas;
- timeout and payload bounds;
- read-only, mutation, or target-code-execution classification;
- required permission and target scope;
- supported runtime/engine capabilities and declared limits.

Identifiers are canonical and unique. Conflicts, unsupported schema features, or incomplete security
metadata fail closed and omit the operation.

## MCP surface

Generic operations enable future providers:

- `list_extension_providers`
- `list_extension_operations`
- `invoke_extension_operation`

The Gateway validates requests against the advertised schema, applies its own size/timeout bounds,
authorizes the declared classification, routes to the selected host, and validates the result. It
does not blindly relay arbitrary provider JSON.

HookLab also exposes stable first-class tools:

- `hook_probe_install`, `hook_probe_status`
- `hook_create`, `hook_list`, `hook_update`
- `hook_enable`, `hook_disable`, `hook_remove`
- `hook_get_events`, `hook_wait_for_event`
- `hook_compile`, `hook_export_project`

Typed and generic routes call the same provider dispatcher and service. Provider absence returns
`hook_provider_unavailable`, not an unknown tool-shaped success. Tools reuse dgSpy process/runtime
selectors and guarded method identities; HookLab-specific fuzzy string lookup is not introduced.

## Independent post-detach ownership

Hook resources are keyed by `host_id`, canonical image identity, PID plus process creation time,
runtime/AppDomain identity, and `probe_instance_id`. Debugger-session ownership is deliberately not
part of the key.

One renewable controller lease owns mutation. Reads may be shared. Mutation requires the controller
claim, operation permission, and expected `hooks_version`; stale versions fail without change. Lease
expiry changes control ownership, not hook state. Claim tokens are never logged.

Debugger detach preserves the probe and hooks. After Gateway/dnSpy restart, discovery exposes an
unowned or continuity-protected probe; it never silently grants control to the first caller. Explicit
claim/recovery policy reconciles the reported `hooks_version`. Target exit or AppDomain unload destroys
the resource, and process-creation identity rejects PID reuse. Removal remains explicit even when the
original debugger session is gone.

## Permissions and remote hosts

Add three dedicated permissions:

- `runtime_hooks`
- `custom_hook_code`
- `hook_export`

Inspect-only always refuses all three. Provisioned full-control local and remote hosts may enable them
explicitly, independently. An upgrade must not widen an existing deployment: absent configuration is
deny until the administrator enables the new permissions.

Expert source compiles on the selected remote host so framework and target references match. Source,
reference roots/count/bytes, diagnostics, output bytes, duration, memory where enforceable, and
compiler concurrency are bounded. Reference resolution uses an allowlisted target/SDK policy and
rejects traversal/reparse escapes. Compiler isolation does not sandbox the resulting target code.

## Audit and artifact routing

Audit records contain request/correlation ID, host and full process/runtime identity, provider and
operation version, owner, permission decision, target guards, source/package/artifact digests,
`probe_instance_id`, `patch_id`, expected/resulting `hooks_version`, outcome, duration, and whether
side effects may have occurred.

Audit never records source text, captured values, arguments, return values, exception messages that
may contain secrets, pipe credentials, claim tokens, or artifact contents. Tests use secret-shaped
values to enforce redaction.

Remote export is restricted to a configured safe artifact root with canonical containment,
reparse-point rejection, fresh staging directories, atomic publication, and no overwrite by default.
A canonical manifest lists every file and digest. Retrieval is bounded and chunked, authenticates each
request, verifies chunk and whole-artifact hashes, and never treats an incomplete transfer as valid.

## Failures and recovery

Stable failures distinguish provider unavailable/incompatible/degraded, permission denial,
owned/unowned/lease expiry, stale `hooks_version`, probe or process identity mismatch, target guard
mismatch, compiler limits, payload failure, and export failure. Timeouts state whether mutation may
have occurred and include a safe status/reconciliation operation. Provider exceptions are isolated
from the extension dispatcher and cannot strand the debugger paused.

## Verification gates

- Contract tests validate descriptors, schema restrictions, version negotiation, and duplicate
  rejection.
- Gateway tests prove generic/typed routing parity, authorization, lease/version concurrency, timeout
  ambiguity, audit redaction, and artifact containment/retrieval.
- Extension tests prove composition with absent and broken providers and dispatcher isolation.
- Live CLR tests prove install, detach-with-hooks, post-detach control, Gateway/dnSpy reconnection,
  PID-reuse rejection, explicit removal, and target exit.
- Remote tests prove selected-host compilation, central permission/ownership enforcement, identical
  artifact hashes, and redacted audit.
- Mono provider capabilities remain absent until the HookLab CLR gate is green and Mono acceptance is
  complete.
