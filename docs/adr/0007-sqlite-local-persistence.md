# ADR-0007: SQLite for local application metadata

**Status:** Accepted\
**Date:** 2026-11-14

## Context

DataBridge keeps local state between runs: connection profile metadata,
comparison session summaries, plan metadata and execution manifests,
logs and preferences (`architecture.md` §11). There is no server to
hold it (ADR-0001). Secrets must not be stored with this data, and full
row-level differences are not stored by default.

## Decision

Store local application metadata in an embedded SQLite database behind
repository abstractions defined in Application, with versioned schema
migrations. Secrets go to the OS-native credential store (Windows
Credential Manager, macOS Keychain, Linux Secret Service), never to
SQLite. When no secure store is available, the secret is not saved and
the user is prompted each session; there is no plaintext fallback.

## Consequences

-   A single local file with transactions and queryable history, and
    no separate database process to install.
-   SQLite works the same on all three operating systems.
-   Use cases depend only on repository ports, so the storage engine
    can change without touching Application or Core.
-   Schema migrations must be written and tested from the first release,
    because user files outlive application versions.
-   Data access technology for SQLite (ADO.NET or a lightweight ORM) is
    decided in Phase 1, when persistence is built.

## Alternatives considered

-   **JSON or other flat files.** Simple, but no transactions,
    concurrent-write safety or efficient queries over history. Rejected.
-   **LiteDB or another embedded document store.** Viable, but less
    widely used and offers nothing SQLite lacks here. Rejected.
-   **Storing secrets in SQLite, encrypted or obfuscated.** The key
    would have to live on the same machine, so it adds no real
    protection. Rejected in favor of the OS credential store.
