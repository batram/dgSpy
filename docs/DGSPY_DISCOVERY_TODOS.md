# dgSpy discovery TODOs

## Scope

This document tracks defects in dgSpy's MCP-facing discovery behavior. It does not track upstream
dnSpy CoreCLR engine defects; those are recorded separately in
[DNSPY_CORECLR_TODOS.md](DNSPY_CORECLR_TODOS.md).

## Resolved: isolate unreadable processes during name-filtered discovery

### Observed behavior

The originating session recorded this `list_programs` failure:

```yaml
internal_error: Invalid access to memory location.
```

It reproduced for every non-empty `process_names` filter, including an absent name, while PID-only
discovery continued to work. A PID-plus-name control proved Barnyard itself did not throw.

### Cause

dgSpy passes `process_names` into dnSpy's `AttachableProcessesService`. The name-filter path in
`AttachableProcessesServiceImpl.IsValidProcess()` reads `Process.MainModule.FileName` for every
candidate process. Modern .NET reports failures from that API as
`System.ComponentModel.Win32Exception` (the observed request surfaced Windows error 998), but the
inherited dnSpy code catches only `InvalidOperationException` and `ArgumentException`. One unrelated
or racing process therefore faults the provider enumeration and dgSpy exposes the exception message as
a generic `internal_error`.

The request is a dgSpy issue because `list_programs` advertises bounded process-name discovery and
must provide a reliable MCP result even when an individual Windows process cannot be inspected. The
underlying exception gap is in inherited dnSpy attach-service code, so any shared-file change must be
narrow, covered, and recorded in the upstream drift allowlist.

Name matching has a second contract mismatch: the prefilter sees the executable filename including
`.exe`, so exact `"Barnyard"` does not match while `"Barnyard*"` does.

### Implemented solution

1. dgSpy resolves names against `Process.ProcessName`, isolates expected per-candidate inspection
   failures, and passes only the resulting PID intersection into dnSpy. It no longer invokes dnSpy's
   `MainModule.FileName` name-filter path.
2. Matching is case-insensitive against the executable stem, supports `*` and `?`, accepts a trailing
   `.exe`, and rejects path-shaped selectors.
3. A zero-match name query returns immediately instead of passing an empty PID array, which dnSpy
   interprets as an unfiltered system scan.
4. Each discovery starts a new cache generation immediately. An older concurrent call cannot publish
   over a newer result, and a failed refresh cannot leave old `program_id` entries valid.
5. Structured provider error provenance is implemented in the following slice so a future operation
   failure is not reduced to a misleading raw operating-system message.

### Acceptance criteria

- One candidate throwing `Win32Exception` cannot abort the complete listing.
- Other matching programs are returned.
- An absent name returns an empty result rather than `internal_error`.
- Exact and wildcard process-name semantics are documented and covered.
- PID-only discovery does not introduce unnecessary main-module reads.
- Cancellation and unexpected provider defects are not swallowed.
