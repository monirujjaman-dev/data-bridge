# ADR-0012: Schema comparison object scope in v1

**Status:** Accepted\
**Date:** 2026-11-15

## Context

`requirements.md` §6.2 lists the objects schema comparison supports:
schemas, tables, columns, primary keys, foreign keys, indexes, unique,
check and default constraints, views, stored procedures, functions,
triggers and sequences. §10 left open, for Phase 3, whether to add
synonyms, user-defined types and table types, partition schemes and
functions, temporal and memory-optimized tables, full-text objects, and
users, roles and permissions. FR-SCH-010 requires anything not compared
to be reported, never skipped silently.

## Decision

v1 compares only the §6.2 list. Everything else the catalog shows is
reported as not compared, with a reason, on the side where it exists:

-   **Reported as objects:** synonyms, CLR procedures, functions,
    aggregates and triggers, rules, old-style defaults, service queues,
    external tables and any other `sys.objects` type outside the list;
    alias, CLR and table types; partition functions and schemes; XML
    schema collections; full-text catalogs and indexes; user assemblies;
    database DDL triggers; database users and roles; XML and spatial
    indexes.
-   **Tables excluded whole and reported:** system-versioned temporal
    tables and their history tables, memory-optimized tables, graph
    (node and edge) tables and FileTables. Their columns and indexes
    have engine-managed semantics the model does not represent, so a
    partial comparison would mislead.
-   **Partitioned tables** are compared, and their partitioning is
    reported as not compared.
-   **Columns of a user-defined type** are compared by the type's
    schema-qualified name.
-   **Unreadable modules** (encrypted, or hidden by a missing
    `VIEW DEFINITION`) are reported and compared as Unknown, never as
    equal or missing.
-   **Not reported:** permissions (grants and denies), statistics,
    extended properties, database options and filegroups. They are
    outside the schema model entirely; the user documentation says so.

## Consequences

-   Every supported object has a tested normalized model and comparer;
    nothing outside it gets a half-correct comparison.
-   Users see exactly what was not compared and why, per side.
-   Adding a type later means listing it in §6.2, extending the model
    and comparer, and moving it out of the reported list, with
    integration tests. Each addition needs no new ADR unless it changes
    the model's shape.

## Alternatives considered

-   **Compare temporal and memory-optimized tables as ordinary tables.**
    Their period columns, history tables and hash indexes would show as
    plain differences that cannot be synchronized safely. Rejected.
-   **Compare users, roles and permissions.** Security principals differ
    by design between environments, and synchronizing them is a security
    decision. Deferred beyond v1.
-   **Ignore unsupported objects.** Violates FR-SCH-010. Rejected.
