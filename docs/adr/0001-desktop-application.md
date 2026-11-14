# ADR-0001: Local desktop application, no mandatory server

**Status:** Accepted\
**Date:** 2026-11-14

## Context

DataBridge connects directly to two SQL Server databases, reads their
metadata and data, and can apply approved changes to one of them. Those
databases are usually on a developer workstation, a private network or
behind a VPN. They are rarely reachable from a hosted service.

Credentials and row values are sensitive. The product rules forbid
logging secrets or row values by default (`CLAUDE.md` rule 11), and
hosted synchronization is a non-goal for v1.0 (`requirements.md` §3).

## Decision

DataBridge is a local-first desktop application. It talks to the
databases directly from the user's machine. It has no mandatory backend
server, account service or cloud component. Local state is kept on the
machine: application metadata in SQLite (ADR-0007), secrets in the OS
credential store.

## Consequences

-   Any database the user's machine can reach can be compared, with the
    user's own network access and authentication.
-   Credentials and row values never leave the machine for a
    DataBridge-operated service.
-   There is nothing to host, operate or secure on a server.
-   Collaboration, shared history and scheduled synchronization are not
    available. They are deferred features (`requirements.md` §9).
-   DataBridge needs native builds, packaging and signing for each
    supported OS (`architecture.md` §14).
-   Machine resources bound comparison work, so streaming and bounded
    memory are mandatory (`CLAUDE.md` rule 7).

## Alternatives considered

-   **Web application with a backend.** The backend would need network
    access to every database the user compares and would hold
    credentials centrally. Rejected for v1.0: it adds hosting and a
    larger attack surface, and it cannot reach most private databases.
-   **Command-line tool only.** Good for automation, but reviewing large
    differences, risk and generated SQL before approval needs an
    interactive UI. A CLI can be added later over the same Application
    layer.
