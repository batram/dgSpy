# Documentation and work

Use one simple boundary:

- `dgSpy/docs` documents the product as it exists.
- The separate `docs/local` repository records work on the product.

## Product documentation

Keep these in the main `docs` directory:

- supported behavior and current limitations;
- architecture and invariants;
- build, installation, operation, and recovery instructions;
- public tool and protocol reference;
- durable design decisions and completed feature rationale;
- the high-level product roadmap.

A reader should be able to understand and operate a checkout from the main repository alone. Current
product behavior must not be defined only by a file in `docs/local`.

## Work

Keep these in the separate `docs/local` repository:

- active tasks and TODO lists;
- implementation plans for unfinished slices;
- investigations and hypotheses;
- handoffs and current-session state;
- live-run evidence, timings, transcripts, screenshots, and review material;
- failed or superseded approaches retained for reference.

New bounded tasks go below `docs/local/work`. Existing local material can move there gradually when it
is touched; no bulk reorganization is required.

## Promotion rule

When work establishes a supported fact, update the main documentation in the same change or the next
focused documentation change. Keep detailed evidence in `docs/local`, but promote the conclusion a
user or future implementer must rely on.

When a task completes:

1. update the main documentation for the behavior that now exists;
2. remove or update the corresponding road if its product outcome is complete;
3. close or archive the task in the work repository;
4. do not leave its checklist in current product documentation.

The test is short: **what is true belongs in docs; what we are doing belongs in work.**
