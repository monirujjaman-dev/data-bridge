# ADR-0015: Environment labels on connection profiles

**Status:** Accepted\
**Date:** 2026-11-15

## Context

`requirements.md` §10 left open whether profiles carry an environment
label such as Production, and whether a label changes approval. A plan
that writes to the wrong database is the most expensive mistake the
product can help make, and the server cannot say whether a database is
production.

## Decision

-   A profile has an optional label: Development, Test, Staging,
    Production, or none (the default). The user chooses it; DataBridge
    never infers it. It is stored with the profile (SQLite migration 2)
    and shown in the profile list and on the Synchronization route sign.
-   A plan records the write target profile's label when it is created,
    and the label is covered by the plan's content hash.
-   When that label is **Production**, approval also requires typing the
    write target's database name exactly (case-sensitive, as the server
    reported it), whatever the plan's risk. All other approval rules
    apply to every label alike: confirm the write target, and approve
    each High, Critical and destructive operation individually.
-   Other labels change only what is shown.

## Consequences

-   Relabelling a profile after planning does not change existing plans;
    a new plan picks up the new label.
-   An unlabelled production database gets no extra protection. The
    label is a guard against slips, not a security control.

## Alternatives considered

-   **Display only.** Cheaper, but the label would not stop a routine
    approval against production.
-   **Deferring labels.** Leaves the most dangerous case with the same
    approval as a scratch database.
-   **Blocking destructive operations on Production entirely.** Too
    strict for real maintenance; destructive operations already need
    opt-in and individual approval.
