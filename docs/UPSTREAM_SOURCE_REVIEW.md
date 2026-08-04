# Upstream source review

Checked 2026-08-04 against the current repository pins and the live upstream repositories. This is an
investigation record, not an upgrade proposal. No source, package, or submodule revision was changed.

> Historical review: section 2.6 subsequently adopted the bounded dnSpyEx 6.6 host baseline. See
> [MODERNIZATION_GATE.md](MODERNIZATION_GATE.md#dnspyex-66-host-adopted-2026-08-04) for the current
> decision, retained Mono/debugger boundaries, and regression evidence.

## Result

The sources have moved on substantially, but they did not move independently. The practical upstream
for this codebase is [dnSpyEx](https://github.com/dnSpyEx/dnSpy), whose current tree still integrates
forks of ILSpy v2, NRefactory, Roslyn's expression compiler, and `Mono.Debugger.Soft`. Moving one of
those pieces directly to its original project's latest release would create more integration work than
taking the corresponding dnSpyEx change.

Do not rebase dgSpy onto dnSpyEx yet. Preserve the verified dnSpy 6.1.8 baseline and evaluate narrowly
scoped ports in this order:

1. Port selected dnSpyEx ILSpy-v2 fixes that improve `get_csharp` correctness or malformed-input safety.
2. Trial dnlib 4.5.0 as an isolated package update with the complete dnSpy and dgSpy test suites.
3. Treat Roslyn.ExpressionCompiler 5.6.0 and the dnSpyEx debugger changes as one future integration
   branch for Phase 5/7 work, not as an in-place package bump.
4. Retain dgSpy's Unity frame-fetch timeout. Neither Mono nor dnSpyEx contains an equivalent bound.

## Evidence matrix

| Dependency | dgSpy baseline | Upstream state checked | Relevant movement | Cost and decision |
|---|---|---|---|---|
| `Mono.Debugger.Soft` | dnSpy fork `2b98df22` (2020-11-11), plus dgSpy `888ded0f` | [Mono debugger-libs](https://github.com/mono/debugger-libs) remains active; dnSpyEx branch `d12451c2` (2025-11-14) | Mono added protocol/runtime support, hot reload, inline arrays, cancellation handling, and connection concurrency fixes. dnSpyEx imported newer Mono files in 2021 and later added compatibility changes. | Medium/high protocol-compatibility risk for old Unity Mono. The current `ThreadMirror.GetFrames()` in both upstream lines still waits indefinitely; it does **not** supersede `888ded0f`. Keep the local patch. Consider individual connection/cancellation fixes only after Unity smoke tests. |
| `ILSpy` / `ICSharpCode.Decompiler` | dnSpy fork `3fa5701e` (2020-11-11) | Original [ILSpy 10.1](https://github.com/icsharpcode/ILSpy/releases) is current; dnSpyEx `ilspyv2` is `b063cd99` (2026-05-31) | Original ILSpy has years of language, metadata, malformed-input, and decompiler correctness work, but also major API changes and a .NET 10 baseline. dnSpyEx's compatibility branch has targeted crash, stack-overflow, generic, naming, disassembly, runtime-async, and decompilation fixes. | Direct ILSpy 10 migration is very high cost and replaces dnSpy's decompiler integration surface. The dnSpyEx `ilspyv2` changes are lower-cost candidates and directly benefit `get_csharp`; port and test them selectively. |
| `NRefactory` | dnSpy fork `79d99d6f` (2020-11-11) | dnSpyEx fork `a346fd19` (2026-04-09) | dnSpyEx still carries it and has fixes for AST printing, function pointers, readonly structs, lambdas, indentation, and resolver behavior. | It is still required by the current ILSpy-v2/VB and AST pipeline; removal is coupled to a modern ILSpy rewrite. Do not replace it with Roslyn in isolation. Selected dnSpyEx fixes can accompany selected ILSpy-v2 ports. |
| `Roslyn.ExpressionCompiler` | dnSpy fork `09ddd061` (2019-06-10), Roslyn packages 2.10.0 | dnSpyEx `dnSpy` branch `e127e791` (2026-07-17), generated from Roslyn 5.6.0 | dnSpyEx repeatedly regenerated and integrated the evaluator from Roslyn 4.8 through 5.6. Its host also moved its Roslyn package set to 5.6.0. | High cost: generated sources, Roslyn packages, debugger contracts, and host target framework must remain aligned. This is relevant to expression evaluation and func-eval, but should be evaluated as a coordinated dnSpyEx slice when Phase 5/7 begins. |
| `dnlib` | NuGet 3.3.2 | dnSpyEx uses 4.5.0 | Four major/minor releases of metadata reader/writer changes are available; dnSpyEx already carries the version against the same broad application. | Medium cost and independently testable. It is the best bounded experiment for newer metadata and dynamic-module edge cases, but it touches much of dnSpy, so require full build/tests plus live Unity module/decompile smoke coverage before adoption. |
| dnSpy host | Original dnSpy 6.1.8 / .NET Framework 4.8 and .NET 5 tree | dnSpyEx 6.6.0 source snapshot `3f4caa4f` (2026-07-20), targeting .NET Framework 4.8 and .NET 10 | Active fixes include debugger runtime discovery, in-memory metadata handling, raw locals, break-on behavior, Mono settings, environment variables, stack-overflow prevention, current metadata flags, and coordinated dependency updates. | Highest potential benefit and highest regression cost. A wholesale rebase would invalidate the live evidence in `DGSPY_STATUS.md`. Use dnSpyEx as the reference implementation and port bounded changes; create a separate migration branch only after dgSpy has repeatable end-to-end regression coverage. |

## What dgSpy actually hits

- The local Mono hang is a real gap upstream. `ThreadMirror.GetFrames()` still uses an unbounded
  `WaitHandle.WaitAny` in the checked dnSpyEx branch. The dgSpy patch adds a three-second timeout while
  leaving the late reply in flight, so it remains necessary.
- `get_csharp` is coupled to dnSpy's ILSpy-v2 contracts, which makes dnSpyEx's maintained `ilspyv2`
  branch directly applicable. Original ILSpy 10 is not a drop-in decompiler package update.
- NRefactory is referenced by the current C#/VB AST and output pipeline. Its apparent obsolescence in
  new applications does not mean it is dead code here.
- The expression compiler is generated from and compiled with a matching Roslyn version. Updating its
  submodule without the host's Roslyn and debugger adaptations is not a valid experiment.
- dnlib is pervasive, but unlike the submodule set it is a versioned package and can be tested on an
  isolated branch without changing dgSpy's wire protocol or extension API.

## Go/no-go gates for later upgrade work

Any selected port must retain the existing dgSpy baseline and add evidence for the behavior it claims
to improve. At minimum: build both dnSpy architectures/frameworks used by the project, run the dgSpy
Protocol, Gateway, and Extension suites sequentially, and repeat the live Unity attach, pause, stack,
locals, continue, and detach smoke path. Decompiler changes also need before/after fixtures for the
specific construct or malformed input; evaluator changes need watches, conditional expressions, and
func-eval timeout/cancellation coverage.

## Sources and reproducibility

The comparison used the repository pins above, the live default/integration heads, dnSpyEx's
[`.gitmodules`](https://github.com/dnSpyEx/dnSpy/blob/master/.gitmodules), the
[ILSpy release history](https://github.com/icsharpcode/ILSpy/releases), and the repositories for
[Mono debugger-libs](https://github.com/mono/debugger-libs),
[dnSpyEx ILSpy](https://github.com/dnSpyEx/ILSpy),
[dnSpyEx NRefactory](https://github.com/dnSpyEx/nrefactory),
[dnSpyEx Roslyn.ExpressionCompiler](https://github.com/dnSpyEx/Roslyn.ExpressionCompiler), and
[dnlib](https://github.com/0xd4d/dnlib). Commit IDs are included so this time-sensitive review can be
repeated without treating branch names as immutable evidence.
