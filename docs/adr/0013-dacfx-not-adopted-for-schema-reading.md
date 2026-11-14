# ADR-0013: DacFx is not adopted for schema reading and comparison

**Status:** Accepted\
**Date:** 2026-11-15

## Context

ADR-0006 allowed DacFx behind an adapter and asked Phase 3 to decide,
after a cross-platform evaluation, whether to adopt it
(`architecture.md` §16). Phase 3 needed a schema reader and comparer for
the v1 object list (ADR-0012).

The evaluation was a design review, not a hands-on spike on every
operating system:

-   DacFx extracts a whole model (`TSqlModel`) per database. Mapping it
    into DataBridge's model (ADR-0005) would still be required, so it
    would save the catalog queries but not the comparer.
-   Its comparison output is its own object graph and deployment plan;
    using it would bypass DataBridge's identity rules (system-named
    constraint matching, partial-visibility handling, unreadable modules
    as Unknown) that the requirements depend on.
-   It is a large dependency whose behavior on Linux and macOS desktops
    has not been validated for this product.

A native reader was built instead: one read-only catalog batch over
`sys.*` views with Microsoft.Data.SqlClient, which already runs on all
three operating systems. Integration tests against SQL Server 2022
cover every supported object kind.

## Decision

Do not use DacFx for schema reading or comparison in v1. The SQL Server
schema reader and comparer are DataBridge's own (`Providers/SqlServer/Schema`,
`Core/Comparison`). ADR-0006 stays in force: if script generation in
Phase 5 wants DacFx, that needs its own hands-on evaluation on Windows,
Linux and macOS and a new ADR.

## Consequences

-   No DacFx package is referenced; publish size and platform risk stay
    low.
-   DataBridge owns the correctness of every supported object's metadata
    and must keep its integration tests in step with new object types.
-   Objects DacFx would have covered beyond the v1 list are reported as
    not compared (ADR-0012) rather than compared.

## Alternatives considered

-   **DacFx for extraction, own comparer.** Saves catalog queries but
    adds the dependency and a model mapping as large as the queries it
    replaces. Rejected for v1.
-   **DacFx schema compare end to end.** Fastest coverage, but its
    results and rules would replace the product's own. Rejected.
