# ADR-0016: Data operations reference recorded keys and fingerprints

**Status:** Accepted\
**Date:** 2026-11-15

## Context

A plan cannot hold every changed row (product rule 7), yet the reviewed
plan must describe exactly what will execute, and rows that change
between comparison and execution must be detected rather than
overwritten (`architecture.md` §16). Data comparison already computes a
SHA-256 fingerprint of each differing row on each side (ADR-0014).

## Decision

-   **Record while comparing.** When data is compared for planning, a
    recorder sink writes each differing row's status, key values and
    both fingerprints to a local change set file, one per table, in key
    order. Keys use a typed, reversible encoding (`DatabaseValueCodec`).
    Nothing else about the row is stored.
-   **The plan references, never holds, the rows.** Each data operation
    carries a `RowChangeSet`: the change set id, which status it covers,
    the key and written columns, the row count and a SHA-256 over the
    stored records of that status. The plan's content hash covers all of
    it, so an approval binds the exact set of rows.
-   **Verify before use.** Validation recomputes the hash and count from
    the file; a missing, changed or truncated file invalidates the plan.
-   **Drift is checked per row at execution (Phase 6).** Before writing a
    row, both databases are read again by key and their fingerprints
    compared with the recorded ones. A row that no longer matches is
    skipped and reported, never written. The values written are the
    copy side's current values, which by then have the recorded
    fingerprint.
-   **What is written.** Inserts write the key and every compared column;
    updates set every compared non-key, non-identity column; deletes use
    the key only. All are parameterized per-row templates. Inserts are
    planned only when every write-target column left out can be filled
    by the engine (nullable, default, identity, computed, rowversion),
    and keep identity values with `IDENTITY_INSERT` on that table only.
-   **Order.** Inserts and updates run after the inserts into the tables
    they reference; deletes run after the updates and deletes of the
    tables that reference them. Cycles are reported, not broken.
-   **Lifetime.** Change set files live under the user's local
    application data folder, are deleted when their plan is discarded or
    the next comparison replaces them, and any file older than seven days
    is deleted when DataBridge starts. They are never logged.
-   **Schema and data together.** A table whose schema the same plan
    changes gets no data operations; the user synchronizes the schema
    first and compares the data again.

## Consequences

-   Key values are written to local disk for the lifetime of a plan. That
    is a deliberate exception to "no row values persisted by default",
    limited to keys, the user's own machine and a bounded lifetime.
-   Plans live in memory for the application session (history is Phase
    7); closing DataBridge discards them, and their files expire.
-   A plan's hash is over fingerprints, so it shows that the rows are the
    ones compared, not that the values are equal (domain-model §11).
-   Execution reads each changed row again on both sides, which costs one
    keyed read per row per side; batching those reads is a Phase 6
    concern.

## Alternatives considered

-   **Staging keys in the write target.** Faster set-based DML, but it
    creates objects in the write target, needs extra permissions, and
    makes planning a write.
-   **Keys only, no drift check.** Simpler, but applies changes made since
    the comparison silently, which the architecture forbids.
-   **Holding rows in the plan.** Breaks bounded memory for large tables.
