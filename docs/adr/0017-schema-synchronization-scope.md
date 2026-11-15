# ADR-0017: Schema synchronization scope and SQL generation

**Status:** Accepted\
**Date:** 2026-11-15

## Context

Phase 5 turns schema differences into SQL Server DDL. Some differences
have a safe, in-place statement; others need a table rebuild, a change
of enabled state that the product must never make automatically
(FR-PLN-014), or knowledge DataBridge does not have. The plan must order
the statements correctly and report everything it leaves out.

## Decision

-   **Generated in-house, from the normalized model.** DDL is built from
    the schema model (ADR-0005) without DacFx (ADR-0013). Identifiers are
    always bracket-quoted; type, collation and option names are emitted
    only when they match a strict pattern. Stored definitions (module
    text, check, default, computed and filter expressions) are copied as
    the engine stores them.
-   **In scope:** create and drop schemas, tables, sequences, views,
    procedures, functions and DML triggers; add, drop and alter columns
    (type, length, precision, scale, collation, nullability); add and
    drop primary keys, unique, foreign key, check and default
    constraints, and clustered and nonclustered indexes on tables and
    schema-bound views; alter modules and sequences in place.
-   **Changes are drop and create.** A changed constraint or index is
    planned as two operations, so column changes can run between them;
    the drop half is High risk, not destructive, and warns that the
    object is missing until it is recreated.
-   **Modules are altered with `CREATE OR ALTER`,** which keeps
    permissions; each runs after a batch setting the module's stored
    `ANSI_NULLS` and `QUOTED_IDENTIFIER`.
-   **System-generated constraint names are never copied;** the engine
    names the new constraint, as it did on the copied side.
-   **Not synchronized, and reported:** changes needing a rebuild
    (identity, computed, sparse, ROWGUIDCOL, FILESTREAM, column sets,
    typed XML, masking, Always Encrypted, generated-always, hidden
    columns, table compression), column order, enabling or disabling
    constraints, indexes or triggers, disabled or untrusted state on new
    objects except untrusted foreign keys and checks (added `WITH
    NOCHECK`), altering an indexed view, columnstore and other index
    types, sequence type and start value, alias types the write target
    lacks, hidden or encrypted definitions, and keys referenced by
    foreign keys the plan would have to drop and recreate.
-   **Ordering** uses the dependency rules of `SchemaSynchronizationPlanner`
    (schemas, tables, columns, keys before foreign keys, referenced
    objects before modules via `sys.sql_expression_dependencies`, and the
    reverse for drops), sorted topologically with cycle detection.
    Procedure-to-procedure references are ignored for ordering because
    SQL Server resolves them at run time.
-   **Required drops are added.** A selected drop that cannot run without
    removing something the copied side also lacks (a dropped column's
    default, a foreign key to a dropped table) adds that drop and says
    why. Anything else it would need is reported instead.
-   **Risk** follows the `architecture.md` §10 baseline. Changing a
    column so that values can be lost or rejected is destructive; a
    constraint, key or index on a table the plan creates is Low.

## Consequences

-   Generated scripts are readable and reviewable, and integration tests
    apply them to real databases and compare equal afterwards.
-   Some real-world changes need manual work; the plan says which and
    why rather than attempting them.
-   A module renamed with `sp_rename` keeps its old name in its stored
    text, so recreating it from that text creates the old name. This is
    not detected yet.

## Alternatives considered

-   **DacFx deployment.** Rejected for the same reasons as ADR-0013, and
    because its plans are harder to bind to an approval hash.
-   **Table rebuilds** (create new, copy, swap) for rebuild-only changes.
    Deferred: they are destructive multi-step operations that need
    execution support from Phase 6.
