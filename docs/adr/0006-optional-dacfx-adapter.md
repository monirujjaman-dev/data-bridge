# ADR-0006: DacFx is optional and isolated behind an adapter

**Status:** Accepted\
**Date:** 2026-11-14

## Context

Microsoft.SqlServer.DacFx implements extensive SQL Server schema
comparison and deployment logic. Reusing parts of it could save work.
But DataBridge's domain must stay provider-independent (ADR-0005), Core
must not reference DacFx (`CLAUDE.md` rule 14), and DacFx's behavior on
Linux and macOS has not yet been evaluated for this product.

## Decision

DacFx may be used only inside Infrastructure, under
`Providers/SqlServer`, behind an adapter that implements DataBridge's
own ports. DacFx types must not appear in Core or Application contracts.
DataBridge must work without it.

Whether DacFx is adopted at all, and for which features, is an open
decision to be closed in Phase 3 after a cross-platform evaluation
(`architecture.md` §16). That outcome will be recorded in a new ADR.

## Consequences

-   The option to reuse DacFx stays open without coupling the product
    to it.
-   Removing or replacing DacFx later affects only the adapter.
-   Features delivered through DacFx still have to be mapped into the
    DataBridge model, so the saving is partial.
-   The architecture tests treat `Microsoft.SqlServer.*` packages and
    assemblies as forbidden in Shared, Core and Application.

## Alternatives considered

-   **Build on DacFx throughout.** Fast coverage of SQL Server objects,
    but it makes the product depend on one SQL Server library everywhere
    and on its platform support. Rejected.
-   **Never use DacFx.** Safe, but it gives up useful tooling before it
    has been evaluated. Deferred to the Phase 3 decision instead.
