# ADR-0009: Plain ADO.NET over Microsoft.Data.Sqlite for local persistence

**Status:** Accepted\
**Date:** 2026-11-14

## Context

ADR-0007 chose SQLite for local application metadata and left the data
access technology to Phase 1. The local schema is small and owned by
DataBridge: connection profiles now; session summaries, plan metadata,
manifests and preferences later. User database files outlive
application versions, so schema changes must be explicit, versioned
and tested. Secrets never reach this database.

## Decision

-   Use `Microsoft.Data.Sqlite` with hand-written, parameterized SQL in
    repositories under `Infrastructure/Persistence/Sqlite`. No ORM.
-   Keep the schema version in `PRAGMA user_version`. Migrations are an
    ordered list of numbered SQL scripts in code; each runs in one
    transaction together with its version bump. Applied migrations are
    never edited. A database with a newer version than the build knows
    is refused rather than opened.
-   Tables are `STRICT`. Enums are stored by member name, `Guid`s as
    `D`-format text and timestamps as round-trip (`O`) UTC text.
-   Rows are rebuilt through the domain factory (for example
    `DatabaseConnection.Create`), so invalid stored data is reported as
    corrupt instead of entering the application.
-   Updates are optimistic: a row is replaced only while its stored
    `updated_at_utc` still equals the value the caller read, so a stale
    copy cannot overwrite a newer one. A unique-index violation is
    turned into a typed exception rather than leaking `SqliteException`.
-   Connections are not pooled, so the file is released after each
    operation and can be backed up, moved or deleted while the app runs.

## Consequences

-   One small dependency, no mapping layer and full control of the SQL,
    in line with the ADO.NET choice for the comparison engine
    (ADR-0004).
-   Mapping code is written by hand and must be covered by round-trip
    tests for every column.
-   Name uniqueness uses `COLLATE NOCASE`, which folds ASCII letters
    only.
-   Opening a connection per operation costs little for a local file at
    desktop request rates; revisit if history queries become hot.

## Alternatives considered

-   **EF Core with migrations.** Generates migrations and mapping, but
    adds a large dependency and a second data access style for a
    handful of tables. Rejected.
-   **Dapper.** Permitted by the approved architecture and would remove
    some mapping code, but the mapping must go through domain factories
    anyway, so it saves little. Can be adopted later without changing
    the repository ports.
