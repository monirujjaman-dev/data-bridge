# ADR-0011: Disposable SQL Server containers for integration tests

**Status:** Accepted\
**Date:** 2026-11-14

## Context

Database behavior must be covered by integration tests against real SQL
Server (`CLAUDE.md`, definition of done), and tests must never point at
a database anyone cares about. `architecture.md` §16 left the fixture
open for Phase 2. SQL Server container images exist for Linux on x64
only. GitHub-hosted runners differ: Linux runners have Docker with Linux
containers; Windows runners cannot run Linux containers; macOS runners
have no Docker.

## Decision

-   `DataBridge.IntegrationTests` starts a throwaway SQL Server 2022
    container with Testcontainers (`Testcontainers.MsSql`), once per
    test run, and each test creates its own uniquely named databases.
    The container and its data are discarded at the end.
-   Tests that need SQL Server are skipped when Docker is unavailable,
    unless `DATABRIDGE_REQUIRE_SQL_SERVER=true`, in which case they
    fail. CI sets it on the Linux job, so SQL Server behavior is always
    tested there; Windows and macOS jobs run the tests that need no
    server (for example unreachable-server and cancellation tests).
-   `DATABRIDGE_SQL_SERVER_IMAGE` overrides the image, for example to
    test another supported version (2017, 2019).
-   ARM machines run the x64 image under emulation (Docker Desktop with
    Rosetta on Apple silicon) or run the tests on CI.

## Consequences

-   No shared or long-lived test server; nothing outside the container
    can be modified by a test.
-   A local run needs Docker with Linux containers; the first run pulls
    an image of about 1.5 GB.
-   SQL Server behavior is tested on Linux only. Client-side behavior
    that differs by OS (TLS stack, integrated authentication) is not
    covered by the fixture.
-   Container start-up adds roughly 10 to 20 seconds per test run.

## Alternatives considered

-   **A connection string to a server the developer manages.** No
    Docker needed, but easy to point at a real database and not
    reproducible on CI without a service container anyway. Rejected as
    the default.
-   **GitHub Actions service containers.** Works on Linux CI only and
    differs from local runs. Testcontainers gives the same setup in
    both places. Rejected.
-   **LocalDB.** Windows only and not the same engine configuration as
    servers users connect to. Rejected.
