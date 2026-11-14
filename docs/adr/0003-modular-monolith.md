# ADR-0003: Modular monolith with Clean Architecture layers

**Status:** Accepted\
**Date:** 2026-11-14

## Context

DataBridge is a single-user desktop application (ADR-0001). Its
workload is local and cohesive: read metadata, stream and merge rows,
plan, execute and verify. It must keep the domain independent of SQL
Server so that future providers fit without changing Core
(`requirements.md` §2), and it must stay testable without a database
for most logic.

## Decision

Build one process, structured as a modular monolith with Clean
Architecture layers:

-   `DataBridge.Shared`: generic result, error and diagnostic
    primitives only.
-   `DataBridge.Core`: provider-independent domain.
-   `DataBridge.Application`: use cases and the ports Infrastructure
    implements.
-   `DataBridge.Infrastructure`: SQL Server provider, SQLite
    persistence, credential stores.
-   `DataBridge.Desktop`: Avalonia UI and composition root.

Dependencies point inward only, as listed in `architecture.md` §4.
Provider access is split into small capability-focused ports
(`architecture.md` §6) rather than one large provider interface. Use
cases are called directly; no mediator framework, message broker or
service boundary is introduced (`CLAUDE.md` rule 15).

## Consequences

-   Domain and use-case logic can be unit tested without a database or
    UI.
-   A new provider implements the Application ports; Core does not
    change.
-   Dependency direction is enforced by `DataBridge.ArchitectureTests`
    in every build, rather than by review alone.
-   In-process calls keep comparison and execution fast and simple to
    debug.
-   The layering adds some ceremony (ports, mapping between layers) to
    small features. That is accepted for the isolation it buys.

## Alternatives considered

-   **Microservices or a local service plus UI client.** Adds process
    boundaries, serialization and deployment complexity with no benefit
    for a single-user local workload. Rejected.
-   **Single project with folders.** Simpler to start, but nothing would
    stop SQL Server or UI types from leaking into the domain. Rejected.
-   **Mediator framework for use cases.** Indirection without a
    concrete need. Rejected; it can be reconsidered by a new ADR.
