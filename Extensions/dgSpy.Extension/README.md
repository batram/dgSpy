# dgSpy extension structure

- `ExtensionEntryPoint.cs`: MEF composition and dnSpy application lifetime only.
- `Rpc/`: loopback transport, request dispatch, and structured RPC errors.
- `Debugger/`: debugger-dispatcher scheduling, lifecycle control, debugger-state rules, breakpoint
  settings and exception settings (`RpcHost.Breakpoints.cs`), and stepping (`RpcHost.Stepping.cs`).

A dnSpy *settings* write does not take effect on the dispatcher hop that makes it: the setter posts the
real work back to the dispatcher even when the caller is already on it. Write on one hop and read back
on the next — dispatcher delivery is FIFO, so the queued work runs in between. Describing a breakpoint
in the same callback that changed it reports the old settings and looks like a write that did nothing.
- `Events/`: normalized debugger/lifecycle events, bounded non-destructive cursor reads, cancellable
  shared-signal waits, stop-reason lookup, and truncation reporting.
- `Identity/`: stable protocol identities derived from dnSpy values, plus host identity and capability
  advertisement (`RpcHost.Host.cs`).

The static half of the capability contract — the operation list, each operation's time bound, and the
per-engine behavior flags — lives in `dgSpy.Protocol.CapabilityCatalog`, because the gateway derives
its per-tool deadlines from the same table. A new operation belongs in that table as well as in the
dispatch switch; a gateway unit test fails if the two disagree.

Add each new tool family in a focused `RpcHost.<Family>.cs` partial under its owning directory. Keep
shared dnSpy objects and shutdown ownership in `Rpc/RpcHost.cs`; keep pure rules independent of WPF
and dnSpy implementation assemblies so `dgSpy.Extension.Tests` can link and test them directly.
