# dgSpy Assembly Editing and Project Export Plan

**Status:** separate future capability track; not implemented or scheduled. Live debugging remains in
[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md), and arbitrary target C# execution remains in
[TARGET_CODE_EXECUTION_PLAN.md](TARGET_CODE_EXECUTION_PLAN.md).

## Objective and boundary

Expose dnSpy's assembly editor and project exporter without automating its WPF UI. Artifact editing is
copy-on-write and transactional. Live application is limited to capability-gated replacement of existing
method bodies; structural runtime mutation is out of scope.

C# is the only generated or compiled source language. Visual Basic parity is out of scope. IL remains a
supported low-level method-body representation.

## Artifact edit transactions

1. `begin_assembly_edit` opens an exact loaded document or file identified by path plus original SHA-256
   and returns an opaque edit ID. The editable copy is isolated from the source document.
2. Transaction operations replace C# or IL method bodies; add, remove, or modify types and members; and
   edit metadata and resources through dnSpy/dnlib services.
3. `preview_assembly_edit` returns a deterministic change manifest, diagnostics, affected symbol identities,
   input hash, prospective output hash where available, and signing warnings without writing a file.
4. `commit_assembly_edit` validates the complete artifact and writes a new output path by default.
   Overwriting the input requires a separate host-write permission and explicit `overwrite: true`.
5. `discard_assembly_edit` closes all transaction-owned objects and produces no artifact. Validation or
   commit failure must not mutate the original document or a running target.

Every commit reports output path, SHA-256, changed symbols, validation results, and strong-name or
Authenticode consequences. Paths are canonicalized, constrained to configured roots, and protected from
traversal and accidental overwrite.

## C# project export

Add `export_csharp_project` over dnSpy's project-export services:

- Accept exact assembly/document identities, a validated destination, project version, and bounded options
  for resources, ResX generation, and BAML-to-XAML where supported.
- Generate C# only, never open Visual Studio or another GUI, and return a manifest of produced files,
  warnings, errors, total bytes, and a manifest SHA-256.
- Treat partial export as an explicit result. Write through a temporary destination and publish the final
  directory only after successful validation so a failed export is not mistaken for a complete project.

## Live method-body replacement

Add a distinct `replace_live_method_body` operation only for engines that prove support:

1. Require a paused exact session/process/runtime/module, metadata token, expected `state_version`, original
   method-body hash, and a validated replacement produced by an edit transaction.
2. Require an unchanged signature, generic shape, locals contract where the engine requires it, and any
   other engine-specific compatibility checks before touching the target.
3. Retain the original body as a rollback artifact and return its digest with the audit ID. Rollback is a
   separate explicit operation and is never claimed to be guaranteed after execution has entered new code.
4. Report unsupported or incompatible changes before mutation. Adding types, fields, or methods cannot be
   applied live; save the artifact and restart or relaunch the target instead.

## Authorization and audit

Use independent permissions for editing artifacts, writing host files, and replacing live method bodies.
Audit caller identity, input/output hashes, edit ID, changed symbols, destination, live target identity,
original/replacement body hashes, outcome, and rollback availability without logging source or secrets by
default.

## Exit criteria

- A C# method and an IL method can be changed transactionally and saved to new valid assemblies while the
  inputs remain byte-identical.
- Structural edits and resource/metadata changes survive reload and are represented in the manifest.
- C# project export produces a validated bounded manifest and handles resources and BAML capability-wise.
- Path traversal, stale hashes, overwrite without permission, invalid IL/C#, and signing-policy failures do
  not modify the source or target.
- A supported CorDebug fixture can replace and roll back a compatible method body by exact identity; an
  incompatible or structural live edit is rejected before mutation.
- Live replacement remains unsupported on Mono/Unity until exercised against a disposable live fixture.
