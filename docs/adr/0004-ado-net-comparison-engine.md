# ADR-0004: ADO.NET, not EF Core, in the comparison engine

**Status:** Accepted\
**Date:** 2026-11-14

## Context

Data comparison streams both sides of a table in matching-key order and
merges the two streams (`architecture.md` §8). Tables can hold millions
of rows, and the product rules forbid loading an entire table into
memory (`CLAUDE.md` rule 7). Values must keep their SQL Server types and
null semantics (rule 9). Schema reading queries catalog views such as
`sys.columns` and `sys.indexes` whose shape is known only at run time
for user databases. DataBridge does not own the schema of the
databases it compares.

## Decision

Use Microsoft.Data.SqlClient and ADO.NET directly for SQL Server access.
Data is read with `SqlDataReader` streaming, ordered by the matching
key, with `CommandBehavior.SequentialAccess` for LOB columns. Values are
read through typed accessors, not `ToString()`. Dapper is permitted for
suitable catalog metadata queries. SQL generation uses parameters for
values and SQL Server quoting rules for identifiers (`CLAUDE.md`
rule 10).

## Consequences

-   Full control over streaming, batch size, command timeouts,
    cancellation and isolation level for each read.
-   Exact SQL Server type and null handling, which an ORM's mapping
    layer would obscure.
-   No change tracking or object materialization overhead per row.
-   More hand-written code: queries, readers and type mapping must be
    written and covered by integration tests against disposable SQL
    Server databases.
-   Query and identifier safety is DataBridge's responsibility. It is
    reviewed by the `sql-safety-reviewer` agent and the rules in
    `.claude/rules/`.

## Alternatives considered

-   **Entity Framework Core.** Built for an application that owns its
    model. Comparing arbitrary user schemas would need dynamic models,
    and its change tracking and materialization work against bounded,
    ordered streaming. Rejected for the comparison engine. Local SQLite
    persistence makes its own choice (ADR-0007).
-   **Dapper for everything.** Convenient for metadata queries, but its
    buffered mapping to objects does not suit typed, ordered row
    streaming. Kept for metadata only.
