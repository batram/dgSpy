# HookLab: simple implementation plan

## Goal

HookLab adds basic Harmony method hooking to dnSpy. A user or agent can select a method in a running
x64 CLR v4/.NET Framework process, install an observation hook, see hook events, inspect the hooks
currently installed by HookLab, and remove them. The same behavior is available from the dnSpy GUI and
from MCP.

This is the whole first product. The implementation should be a clear vertical slice, not a framework
for imagined future plug-ins or a collection of separately owned task packages.

## What the finished first version does

1. Attach dgSpy to an x64 CLR v4 process.
2. Select an exact managed method from the normal dnSpy code view or identify it through MCP.
3. Install a Harmony prefix, postfix, or finalizer that observes calls without changing program
   behavior.
4. Record bounded events containing the hook ID, method identity, hook kind, time, thread, and a
   bounded representation of the useful values available at that hook point:
   - prefix: arguments;
   - postfix: arguments and return value;
   - finalizer: exception, when present.
5. List installed hooks and their current state.
6. Read new events and dropped-event counts without leaving the target paused.
7. Remove one hook or all HookLab hooks from the target.
8. Perform steps 2-7 from either MCP or the dnSpy GUI, through the same implementation.

Hook installation must validate the selected process/runtime, module MVID, metadata token, method
signature, and current IL hash. A mismatch refuses the operation instead of guessing another method.
Hook callbacks must be bounded, must not wait for the GUI or MCP, and must fail open so observation does
not replace the target's result or exception.

## Explicitly not part of the first version

- changing arguments, results, exceptions, or whether the original method runs;
- retries, UI-thread scheduling, or application-specific recovery policies;
- arbitrary or compiled user C# hooks;
- transpilers, reverse patches, or native/profiler hooks;
- exportable projects, packages, automatic loaders, or process watchers;
- hooks that survive debugger detach or dnSpy restart;
- reconnectable discovery records and long-lived credential management;
- generic third-party extension-provider infrastructure;
- public general-purpose atomic-action workflows;
- Mono/Unity, CoreCLR, or x86 support;
- remote-host support beyond what falls out naturally from the existing dgSpy connection.

These are not deferred requirements that the first implementation must prepare for. If one becomes a
real requirement later, design it from the working product and measured constraints at that time.

## Starting point in this repository

The repository already proves the hard mechanical core:

- `HookLab.Bootstrap` can be byte-loaded into a CLR v4 target and start a resident worker.
- `HookLab.Probe.CorDebug` can install a guarded Harmony hook, buffer events, and unpatch it.
- the extension's payload action can prepare, commit, drain, and shut down the probe;
- `tests/run-hooklab-install-smoke.ps1` has installed a hook in a live target, observed calls, rejected
  bad guards, removed the hook, and detached without killing the target;
- packaging places one verified bootstrap payload in the deployed host tree.

This is useful implementation, not a required architecture. Keep code that directly helps the simple
product and delete or collapse code whose only purpose was satisfying the archived plans. In
particular, do not expose prepare/commit/drain, leases, breakpoints, payload generations, provider
schemas, or atomic-action terminology to HookLab users if a single HookLab operation can own those
details.

The current live smoke used the shipping net10 dnSpy host against a net48 target. Treat the retained
net48 host, the GUI, and the full modernization gate as unverified until they are run during this work.

## One implementation through-line

### 1. Establish the current baseline, then simplify it

Run the focused unit tests and the existing HookLab live smoke before changing behavior. Record what
actually passes. Trace the live smoke from extension request to bootstrap and probe, and identify the
smallest path that installed, observed, and removed the hook.

Refactor around that path. Remove unused abstractions and branches rather than completing them. Likely
deletion candidates include unused generic provider composition, transport/discovery machinery that is
not needed for the chosen event path, and general atomic-action features that exist only to support
future operations. These are candidates, not mandatory deletions: inspect callers and tests, preserve
ordinary dgSpy behavior, and let the simplest working end-to-end design decide.

It is acceptable to keep the existing payload action temporarily as a private bootstrap mechanism.
It is not acceptable to make every GUI or MCP feature speak its low-level multi-step protocol.

### 2. Create one HookLab service

Add one host-side service that owns all HookLab state and operations for the attached target. Its API
should express product actions, approximately:

- install an observation hook;
- list hooks;
- read events after a cursor;
- remove one hook;
- remove all hooks.

The exact class and DTO names are implementation choices. Keep the request and result models small,
version them only where a real process boundary requires it, and return actionable failures. The
service owns any payload preparation, natural-arrival breakpoint, evaluation, transport, buffering,
and cleanup required underneath.

Use one stable HookLab hook ID. Make install idempotent for the same ID and exact definition; refuse a
conflicting reuse. Removal affects only HookLab-owned patches. On target exit or detach, discard host
state and make a best effort to unpatch while the debugger still has a usable target. Report uncertainty
truthfully when the target exits or communication is lost.

Choose the least complex event path that meets the finished behavior. Events may use an existing pipe,
a much smaller replacement, or another target-to-host mechanism, but callers see only cursor-based
event reads. Do not retain authentication, discovery, rotation, or reconnection machinery unless the
actual same-session local path needs it. Event callbacks enqueue bounded data and return immediately.

### 3. Expose the service directly through MCP

Add a small, explicit MCP surface rather than a generic extension-provider protocol:

- `install_hook`
- `list_hooks`
- `get_hook_events`
- `remove_hook`
- `remove_all_hooks`

Names may be adjusted to match existing dgSpy conventions, but keep one tool per user action. Tool
schemas should accept exact target/method identity from existing discovery tools and only the few
observation choices described above. Results include hook state, verification evidence where useful,
event cursor/dropped counts, and clear cleanup ambiguity.

Authorization and audit should use dgSpy's existing mutation/tool mechanisms. Do not introduce another
permission framework for HookLab. Installation and removal are mutations; listing and reading events
are reads.

### 4. Add the dnSpy GUI over the same service

Add a HookLab tool window and a method-context action. The minimum GUI is:

- an `Add Hook...` action on a selected method;
- a small dialog for hook ID, prefix/postfix/finalizer, and capture bounds;
- a list of installed hooks with state and Remove/Remove All actions;
- an event list that updates while the target runs and shows dropped counts;
- clear errors when the target or method identity changed.

The GUI calls the same HookLab service as MCP. It does not invoke MCP internally and does not contain a
second hook manager. Keep UI state disposable and rebuild it from service state after window reopen.
Confirm every new MEF import is actually exported and retain composition coverage, because dnSpy drops
unsatisfied parts silently.

### 5. Make the vertical slice reliable and delete the scaffolding it replaces

Cover the shared service rather than duplicating most behavior tests at the GUI and MCP layers. Keep a
small number of boundary tests for tool schemas, MEF composition, and GUI command wiring. Extend the
live smoke so it uses the public HookLab MCP tools, not private payload or atomic-action operations.

Once the public vertical slice works, remove obsolete public operations, DTOs, tests, and documentation
that were used only by the prototype path. If some low-level operation remains useful internally, make
that ownership obvious and stop advertising it as product surface. Do not preserve an abstraction just
because earlier tasks spent time building it.

## Acceptance

The first version is complete when one unattended end-to-end run, on the hidden desktop:

1. launches and attaches to the net48 x64 fixture;
2. identifies a method through normal dgSpy discovery;
3. installs each supported observation hook through the public MCP surface;
4. proves the method continues returning and throwing exactly as it did without the hooks;
5. receives correctly ordered, bounded events while the target runs;
6. shows the same hooks and events in the HookLab GUI;
7. rejects a wrong MVID, token, signature, and IL hash without installing anything;
8. reports overflow rather than blocking or growing without bound;
9. removes one hook and then all hooks, after which no new events appear;
10. detaches cleanly and leaves the target running;
11. passes the focused HookLab, extension, composition, protocol, Gateway, and packaging tests; and
12. passes the applicable CorDebug modernization gate for both the shipping net10 host and retained
    net48 host.

GUI automation and every smoke that launches dnSpy must run on the private hidden desktop described in
`AGENTS.md`. Run dgSpy Protocol, Gateway, and Extension tests sequentially to avoid assembly locks.

## How agents should execute this plan

One capable agent owns the repository and the whole through-line at a time. The agent may revise the
implementation order as source evidence demands, edit any necessary in-scope file, and remove obsolete
code. Do not split the work into tiny hand-off documents, fixed file-ownership packages, or independent
tasks whose local acceptance can pass while the product remains unusable.

At each useful checkpoint, leave the repository buildable and verify the narrowest real vertical slice
available. A commit may contain a coherent cross-layer increment. Before declaring completion, the agent
must inspect the complete diff, run the end-to-end acceptance path, and state separately what was
measured, what is inferred, and what remains unverified.
