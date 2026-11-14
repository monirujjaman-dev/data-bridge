# ADR-0008: No automatic bidirectional synchronization in v1

**Status:** Accepted\
**Date:** 2026-11-14

## Context

A comparison has a source and a target. A user may want changes to flow
both ways. Automatic bidirectional synchronization means deciding,
without the user, which side wins when both sides changed the same
object or row. That needs conflict detection relative to a common
baseline, conflict resolution rules and a way to review them. DataBridge
compares two independent snapshots and keeps no baseline between runs.
Its safety rules require that every change is previewed and approved
before it runs (`CLAUDE.md` rules 3--5).

## Decision

Version 1.0 synchronizes in one direction per plan. Each plan has a
single *write target* (`requirements.md` §1): the Target for Source →
Target, or the Source for Target → Source. The user picks the
direction; DataBridge never merges changes from both sides into one plan
and never resolves conflicts automatically. Automatic bidirectional
conflict resolution is a v1.0 non-goal (`requirements.md` §3) and needs
its own design and ADR before it is built.

## Consequences

-   Every plan has exactly one database it modifies, which the UI shows
    prominently and revalidates immediately before execution.
-   Plans stay predictable: the user approves a known set of operations
    against a known target.
-   Users who need changes in both directions must build and approve two
    plans, one per direction, and resolve overlapping changes
    themselves.

## Alternatives considered

-   **Bidirectional sync with "last writer wins" or "source wins"
    rules.** Silently discards one side's changes, which contradicts the
    product's safety principles. Rejected.
-   **Bidirectional sync with interactive conflict resolution.** Needs a
    stored baseline, conflict model and review UI. Deferred
    (`requirements.md` §9).
