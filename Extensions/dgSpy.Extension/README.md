# dgSpy extension structure

- `ExtensionEntryPoint.cs`: MEF composition and dnSpy application lifetime only.
- `Rpc/`: loopback transport, request dispatch, and structured RPC errors.
- `Debugger/`: debugger-dispatcher scheduling, lifecycle control, debugger-state rules, breakpoint
  settings and exception settings (`RpcHost.Breakpoints.cs`), stepping (`RpcHost.Stepping.cs`), and
  Phase 7 memory/disassembly/set-IP operations (`RpcHost.Advanced.cs`), and Phase 8 object IDs, Autos,
  module breakpoints, breakpoint interchange, exception policies and value export
  (`RpcHost.Completeness.cs`, `RpcHost.BreakpointInterchange.cs`).

A dnSpy *settings* write does not take effect on the dispatcher hop that makes it: the setter posts the
real work back to the dispatcher even when the caller is already on it. Write on one hop and read back
on the next — dispatcher delivery is FIFO, so the queued work runs in between. Describing a breakpoint
in the same callback that changed it reports the old settings and looks like a write that did nothing.
- `Events/`: normalized debugger/lifecycle events and a separate debugger-output stream, both with bounded
  non-destructive cursor reads, cancellable shared-signal waits, and truncation reporting.
- `Evaluation/`: expression evaluation, member expansion, assignment, watches and module listing. Every
  evaluation runs on the `EvaluationQueue`, never the dispatcher — func-eval executes code inside the
  target and would otherwise stall event delivery for the whole session.
`invoke_method` and `create_object` are deliberately separate always-side-effecting tools, not flags on
`evaluate`. They enable func-eval, use dnSpy's hard func-eval timeout, write an Output-window audit record,
and return its id. `get_registers` reads the stopped x64 Windows thread context directly for CorDebug,
since dnSpy has no public register contract. Mono remains explicitly unsupported because its soft-debugger
thread id is not a Windows OS thread id and cannot safely be passed to `GetThreadContext`.

- `Decompiler/`: metadata, symbol search, typed relationship analysis, IL, decompilation and breakpoint-by-name. Metadata and
  decompilation also run on the `EvaluationQueue`: dnlib loads lazily and dnSpy caches one `ModuleDef`
  per module, so two concurrent readers of the same module race, and a large decompile on the
  dispatcher would stall event delivery for the whole session.
- `Identity/`: stable protocol identities derived from dnSpy values, plus host identity and capability
  advertisement (`RpcHost.Host.cs`).

The static half of the capability contract — the operation list, each operation's time bound, and the
per-engine behavior flags — lives in `dgSpy.Protocol.CapabilityCatalog`, because the gateway derives
its per-tool deadlines from the same table. A new operation belongs in that table as well as in the
dispatch switch; a gateway unit test fails if the two disagree.

Add each new tool family in a focused `RpcHost.<Family>.cs` partial under its owning directory. Keep
shared dnSpy objects and shutdown ownership in `Rpc/RpcHost.cs`; keep pure rules independent of WPF
and dnSpy implementation assemblies so `dgSpy.Extension.Tests` can link and test them directly.
