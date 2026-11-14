# ADR-0010: Supported SQL Server targets and authentication in v1

**Status:** Accepted\
**Date:** 2026-11-14

## Context

Version 1.0 supports SQL Server only (`requirements.md` §9), but "SQL
Server" covers several engines: boxed SQL Server versions on Windows
and Linux, Azure SQL Database, Azure SQL Managed Instance, Azure SQL
Edge and Synapse. They differ in catalog views, permissions, collations,
transaction behavior and authentication. Azure SQL usually needs
Microsoft Entra ID authentication, which needs a tenant to test against.
Every later phase (schema reading, data streaming, script generation,
execution) has to be correct and integration-tested on each supported
engine. `requirements.md` §10 required this decision before Phase 2.

## Decision

-   Support SQL Server 2017 (major version 14) and later, all editions
    (Express, Developer, Standard, Enterprise), on Windows or Linux, on
    premises, in a VM or in a container.
-   Defer Azure SQL Database, Azure SQL Managed Instance, Azure SQL Edge
    and Synapse. The provider reads `SERVERPROPERTY('EngineEdition')`
    and `SERVERPROPERTY('ProductMajorVersion')` after connecting and
    rejects unsupported engines and versions with an error that says
    what was found and what is supported, before any other work.
-   Support SQL authentication and integrated authentication (Windows,
    or Kerberos on Linux and macOS). Defer Microsoft Entra ID
    authentication together with Azure SQL.

## Consequences

-   Every supported version has an official Linux container image, so
    integration tests can cover the supported range on CI (ADR-0011).
-   Catalog queries may use features available since SQL Server 2017.
-   Users of Azure SQL get a clear "not supported in this version"
    message instead of partial or wrong results.
-   Adding Azure SQL later needs its own ADR: Entra ID authentication,
    engine-specific metadata differences and a cloud test fixture.
-   Integrated authentication cannot be tested by the container fixture
    (no domain); it is covered by connection-string tests only, and
    Kerberos setup on Linux and macOS is documented, not automated.

## Alternatives considered

-   **Include Azure SQL Database and Managed Instance.** Larger market,
    but needs Entra ID, cloud test databases that CI cannot create as
    containers, and engine-specific handling in every phase. Deferred.
-   **Also support SQL Server 2016.** It has no Linux container image,
    so it could not be integration-tested on CI. Rejected.
