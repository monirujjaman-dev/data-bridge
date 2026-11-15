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
    Tables, added columns and constraints also wait for the functions
    and sequences their computed columns, checks and defaults reference.
    Procedure-to-procedure references are ignored for ordering because
    SQL Server resolves them at run time. A change left out because of
    a cycle leaves out what depends on it, and changes that stand or
    fall together (a constraint's drop and recreate, a required drop and
    the drop that needs it) are left out together.
-   **Required drops are added.** A selected drop that cannot run without
    removing something the copied side also lacks (a dropped column's
    default, a foreign key to a dropped table) adds that drop and says
    why. Anything else it would need is reported instead.
-   **Risk** follows the `architecture.md` §10 baseline. Changing a
    column so that values can be lost or rejected is destructive
    (including a collation change on `char`, `varchar` or `text`,
    whose code page can lose characters, and `datetime` to `datetime2`
    below scale 3); identity, sparse and ROWGUIDCOL columns are not
    altered in place at all, even for changes SQL Server allows (an
    identity `int` to `bigint`); a
    constraint, key or index on a table the plan creates is Low.

## Consequences

-   Generated scripts are readable and reviewable, and integration tests
    apply them to real databases and compare equal afterwards.
-   Some real-world changes need manual work; the plan says which and
    why rather than attempting them.
-   A module renamed with `sp_rename` keeps its old name in its stored
    text. The generator checks that a definition's header names the
    planned object (and a trigger's table), qualifies a one-part name
    with the planned schema, and refuses any other name, so such a
    module needs manual work. Header names are matched under the copied
    database's case rules, so in a case-insensitive database a header
    differing only in case is accepted and SQL Server names the new
    object as the header spells it.
-   The exported script is read by SSMS or sqlcmd before SQL Server
    sees it, and those tools act on `GO` lines, sqlcmd commands (`:`,
    `!!`) and `$(` variables even inside strings and comments, and may
    track comments differently from SQL Server (nested `/* */`). So each
    operation's SQL is shown as a comment and run as
    `EXEC (@Batch)` over its exact text in hex; only generated text, the
    session-option batches and the identity guard run as plain SQL, and
    the generator refuses to return a script if any runnable line would
    be acted on. The script sets the session options SQL Server needs
    for computed columns, filtered indexes and indexed views (sqlcmd
    starts with `QUOTED_IDENTIFIER OFF`) and restores them after each
    module, which runs under its own stored `ANSI_NULLS` and
    `QUOTED_IDENTIFIER`. Integration tests run exported scripts with the
    container's `sqlcmd` and as SSMS splits them.
-   A module waits for the tables it references only when SQL Server
    checks them as it creates the module (views, table-valued functions
    and schema-bound modules), and is dropped before them only when it
    is schema-bound; procedures, triggers and other scalar functions
    resolve tables when they run. So a table whose check calls a function
    that reads the table is created and dropped without a cycle; with a
    schema-bound function it is a real cycle and is reported.
-   SQL Server refuses to alter or drop a function while a check,
    default or computed column uses it (error 3729). The plan alters or
    drops it after the drops of those uses it makes, recreates them
    after, and warns about uses it keeps.
-   Expression references are recorded per table, not per column or
    constraint, so all of a table's creates and drops are ordered around
    every function and sequence any of its computed columns, checks or
    defaults use. This over-orders but no longer causes the cycles
    above; table-valued functions are treated as checking their tables
    at creation even when multi-statement, because the model does not
    tell inline and multi-statement ones apart.

## Alternatives considered

-   **DacFx deployment.** Rejected for the same reasons as ADR-0013, and
    because its plans are harder to bind to an approval hash.
-   **Table rebuilds** (create new, copy, swap) for rebuild-only changes.
    Deferred: they are destructive multi-step operations that need
    execution support from Phase 6.
