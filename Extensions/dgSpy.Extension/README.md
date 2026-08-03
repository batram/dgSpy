# dgSpy extension structure

- `ExtensionEntryPoint.cs`: MEF composition and dnSpy application lifetime only.
- `Rpc/`: loopback transport, request dispatch, and structured RPC errors.
- `Debugger/`: debugger-dispatcher scheduling and debugger-state rules.
- `Events/`: bounded event storage, cursor waiting, and event recording.
- `Identity/`: stable protocol identities derived from dnSpy values.

Add each new tool family in a focused `RpcHost.<Family>.cs` partial under its owning directory. Keep
shared dnSpy objects and shutdown ownership in `Rpc/RpcHost.cs`; keep pure rules independent of WPF
and dnSpy implementation assemblies so `dgSpy.Extension.Tests` can link and test them directly.
