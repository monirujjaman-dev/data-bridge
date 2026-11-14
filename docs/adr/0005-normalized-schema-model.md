# ADR-0005: Custom provider-independent schema model

**Status:** Accepted\
**Date:** 2026-11-14

## Context

Schema comparison must match objects across two databases, compare
their properties and build a dependency graph for synchronization
(`architecture.md` §8). Some cases must be reported explicitly rather
than hidden: objects that are unreadable without `VIEW DEFINITION`,
modules created `WITH ENCRYPTION`, and unsupported object types
(`architecture.md` §7). Future providers must fit without changing Core
(`requirements.md` §2).

## Decision

Define DataBridge's own normalized schema model in Core
(`domain-model.md`). Objects are identified by `DatabaseObjectId`:
schema, name, object type and, for table children, the parent table.
Provider readers in Infrastructure translate catalog metadata into this
model. Normalization is limited to semantically safe properties, and
unreadable or unsupported cases are explicit states in the model. The
schema comparer works only on the model and emits structured
differences.

## Consequences

-   Comparison, planning and risk classification are written once,
    against domain types, and unit tested without a database.
-   Unreadable, encrypted and unsupported objects can be reported as
    such, never as "equal" or "missing".
-   Adding a provider means writing a reader into the same model.
-   DataBridge must maintain the model and the SQL Server metadata
    mapping itself, and grow them as object types are supported.
-   The model must not drift toward SQL Server-only concepts. Provider
    specifics belong in provider extensions, not in shared types.

## Alternatives considered

-   **Use DacFx's model as the domain model.** Rich SQL Server coverage,
    but it ties Core to a SQL Server-specific library and to its
    cross-platform support. It is kept as an optional adapter
    (ADR-0006).
-   **Compare generated DDL scripts as text.** Simple, but formatting
    and option order produce false differences, and it gives no
    structured differences or dependency graph. Rejected.
