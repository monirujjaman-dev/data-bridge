# DataBridge --- Development Plan

**Status:** Approved baseline (revision 2, 2026-11-14)

## 1. Delivery strategy

Deliver thin vertical slices. Each phase must build, run and have
automated tests. Do not build all UI screens before the underlying use
cases work.

Each phase lists the requirements it covers (`requirements.md`) and any
open decision it must close (`requirements.md` §10,
`architecture.md` §16) before its acceptance is claimed.

### Current status (2026-11-14)

Phase 1 is in progress. Done: connection profile domain model
(`Core/Connections`), profile use cases and the repository and
credential-store ports (`Application/Connections`,
`Application/Abstractions`), and SQLite persistence with versioned
migrations (`Infrastructure/Persistence/Sqlite`, ADR-0009), and OS
credential-store adapters for Windows, macOS and Linux
(`Infrastructure/Security`). Remaining: structured logging and
redaction, and the Desktop shell (host, DI, MVVM, navigation, theme,
profile screens).

Phase 0 is complete. Done: solution and project
structure, SDK pin, central package management,
nullable/analyzer/formatting settings, architecture dependency tests,
the CI workflow (`.github/workflows/ci.yml`: build and test on Windows,
Linux and macOS), README and ADRs 0001--0008 in `docs/adr/`. Acceptance
was confirmed by the first green CI run on all three operating systems
(pull request #1). The Desktop project wraps the visual design system
drafted ahead of Phase 1 (`DESIGN.md`, `PRODUCT.md`,
`src/DataBridge.Desktop/Themes/`, `src/DataBridge.Desktop/Assets/`) in a
placeholder window.

## 2. Phase 0 --- Repository bootstrap

### Tasks

-   Create solution and project structure.
-   Add .NET 10 SDK pin and central package management.
-   Configure nullable reference types, analyzers and formatting.
-   Configure CI build/test workflow on Windows, Linux and macOS.
-   Add architecture dependency tests.
-   Add README and ADR directory; record the decisions in
    `architecture.md` §15 as ADRs.
-   Replace the Commands section of `CLAUDE.md` with the real commands.

### Acceptance

-   Clean clone restores and builds on all three operating systems.
-   CI runs build and tests.
-   Project references obey dependency rules.

## 3. Phase 1 --- Desktop foundation and local settings

Covers: FR-CON-006, FR-CON-007, FR-CON-009, FR-UI-005, NFR-SEC-001,
NFR-SEC-003.

### Tasks

-   Create Avalonia application shell.
-   Configure Generic Host and dependency injection.
-   Add MVVM toolkit.
-   Add navigation and wire the existing theme resources (light and
    dark, following the OS setting) per `DESIGN.md`.
-   Add SQLite persistence abstraction and migrations.
-   Add connection profile CRUD.
-   Add OS credential-store abstraction with Windows, macOS and Linux
    adapters; do not implement an insecure plaintext fallback. When no
    secure store is available, prompt per session.
-   Add structured logging and redaction.

### Acceptance

-   App starts on supported development OS.
-   Profiles can be created, edited and deleted.
-   Secrets are not present in SQLite or logs.
-   Behavior without a secure store is tested.
-   UI remains responsive.

## 4. Phase 2 --- SQL Server connectivity

Covers: FR-CON-001 to FR-CON-005, FR-CON-008, AC-01. Closes: supported
SQL Server versions/editions, Entra ID authentication, SQL Server test
fixture.

### Tasks

-   Implement connection factory and provider factory.
-   Implement SQL authentication.
-   Implement supported integrated authentication.
-   Configure encryption and certificate validation.
-   Test connection and resolve server/database identity (including
    server version and collation).
-   Add timeout and cancellation behavior.
-   Add integration-test fixture using disposable SQL Server databases.
    SQL Server container images are x64 only; document the setup for
    ARM machines.

### Acceptance

-   Connect to two SQL Server databases.
-   Wrong credentials and unreachable servers produce actionable errors.
-   Connection secrets are redacted.
-   Cancellation and timeout are tested.

## 5. Phase 3 --- Schema metadata and comparison

Covers: FR-SCH-001 to FR-SCH-012, AC-03, AC-04. Closes: object types
beyond the v1 list, DacFx adoption.

### Tasks

-   Implement schema model and object identity, including table-child
    identity.
-   Implement catalog readers for schemas, tables and columns.
-   Add keys, constraints, indexes and sequences.
-   Add views, procedures, functions and triggers.
-   Report objects that are unreadable (no `VIEW DEFINITION`, `WITH
    ENCRYPTION`) or unsupported.
-   Implement safe normalization and object-specific comparers.
-   Implement structured difference results.
-   Evaluate optional DacFx adapter on all target operating systems.
-   Build schema comparison UI.

### Acceptance

-   Identical schemas produce no differences for supported objects.
-   Fixtures with controlled changes produce expected object/property
    differences.
-   Unsupported and unreadable objects are reported.
-   No schema comparison modifies databases.

## 6. Phase 4 --- Data comparison

Covers: FR-DAT-001 to FR-DAT-016, AC-05 to AC-07, AC-16. Closes: key
ordering strategy.

### Tasks

-   Discover primary/unique keys.
-   Add user-selected matching keys with duplicate-key detection.
-   Implement typed value model and type comparers.
-   Implement collation-safe key ordering and comparison
    (`architecture.md` §8).
-   Implement streaming reader and keyset pagination.
-   Implement composite-key continuation.
-   Implement ordered merge comparison.
-   Implement read-consistency selection and record the isolation
    achieved per side.
-   Add optional canonical row fingerprints.
-   Add bounded concurrency, progress and cancellation.
-   Build virtualized/incremental data difference UI.

### Acceptance

-   Detect source-only, target-only and modified rows.
-   Correctly compare NULL, decimals, temporal, Unicode and binary test
    cases.
-   Composite keys work.
-   String keys compare correctly under case-insensitive and differing
    source/target collations.
-   Tables without a unique key, or with duplicate user-selected keys,
    are reported as unsupported for automatic matching.
-   Memory remains bounded in large-table tests
    (`DataBridge.PerformanceTests`).

## 7. Phase 5 --- Synchronization planning and SQL generation

Covers: FR-PLN-001 to FR-PLN-014, AC-08, AC-09. Closes: data-operation
materialization, environment labels.

### Tasks

-   Implement operation and plan models.
-   Implement schema dependency graph.
-   Implement topological ordering and cycle detection.
-   Implement SQL Server DDL/DML generation.
-   Implement parameterized data operations.
-   Implement operation risk classification (`architecture.md` §10
    baseline).
-   Implement validation and write-target fingerprinting.
-   Implement script preview/export.
-   Implement approval bound to the plan content hash, and plan
    immutability.

### Acceptance

-   Plans can be generated without database mutation.
-   Dependencies are ordered correctly.
-   Cycles and unsupported operations are reported.
-   DROP/DELETE are disabled unless explicitly enabled and approved.
-   Approved plans cannot be edited; an approval does not apply to a
    different plan.

## 8. Phase 6 --- Execution and verification

Covers: FR-EXE-001 to FR-EXE-010, AC-10 to AC-12, AC-15. Closes:
transaction granularity.

### Tasks

-   Implement executor and operation state tracking in the manifest.
-   Implement explicit transaction/batch boundaries.
-   Add SqlBulkCopy strategy where suitable.
-   Add identity handling and foreign-key validation.
-   Warn about triggers that will fire; never disable triggers or
    constraints automatically.
-   Revalidate write target immediately before execution.
-   Add cancellation and partial execution manifest.
-   Implement verification and post-execution comparison.
-   Build execution monitor UI.

### Acceptance

-   Approved operations execute against disposable test databases.
-   Unapproved operations cannot execute.
-   Stale plans are rejected.
-   Partial failures are recorded accurately.
-   Verification detects incomplete changes.
-   No false rollback guarantee is presented.

## 9. Phase 7 --- History, reports and UX hardening

Covers: FR-HIS-001 to FR-HIS-005, FR-UI-001 to FR-UI-007, AC-14.

### Tasks

-   Add comparison history and retention.
-   Add HTML/CSV report export.
-   Add filters, search and object navigation.
-   Audit both themes for WCAG 2.2 AA contrast, focus visibility and
    keyboard operation.
-   Add error diagnostics and support bundle with redaction.
-   Profile UI rendering and memory.

### Acceptance

-   History can be reopened.
-   Reports contain summary and selected details.
-   Large result sets remain virtualized.
-   Secrets and row values are not leaked in reports/logs by default.
-   Accessibility audit passes in light and dark themes.

## 10. Phase 8 --- Performance, security and release

Covers: NFR-PER-001, NFR-PORT-001, NFR-TEST-002, AC-13.

### Tasks

-   Benchmark 10K, 100K, 1M and 10M row scenarios.
-   Profile allocations, CPU, network and query plans.
-   Test wide rows, binary/LOB columns and skewed keys.
-   Review credential storage and SQL generation.
-   Test Windows, Linux and macOS publishing, including integrated
    authentication (Kerberos on Linux/macOS).
-   Add packaging, signing and release notes.
-   Document supported SQL Server versions and limitations.

### Acceptance

-   Reproducible performance report exists.
-   Memory is bounded by batch/concurrency configuration.
-   All mandatory tests pass.
-   Packages launch on each supported OS.
-   Known limitations are documented.

## 11. Suggested milestone gates

-   M1 (end of Phase 1): desktop shell + secure connection profiles.
-   M2 (end of Phase 2): connect to two SQL Server databases.
-   M3 (end of Phase 3): schema comparison prototype.
-   M4 (end of Phase 4): data comparison prototype.
-   M5 (end of Phase 5): synchronization plan preview.
-   M6 (end of Phase 6): controlled execution and verification.
-   M7 (end of Phase 8): release candidate.

## 12. Definition of done

-   Build and tests pass.
-   Unit tests cover domain algorithms.
-   Integration tests cover SQL Server behavior.
-   Cancellation and failure paths are covered.
-   Dependency rules remain valid.
-   No secrets or sensitive row values are logged.
-   Documentation and user-facing limitations are current.
-   Performance-sensitive changes include benchmarks or profiling
    evidence.

## 13. Revision history

-   **Revision 2 (2026-11-14):** added current status, requirement
    coverage and decisions to close per phase, three-OS CI, credential
    adapters per OS, collation-safe keys, duplicate-key detection,
    consistency recording, approval bound to content hash, trigger
    warnings; replaced the Phase 7 theme task with an accessibility
    audit (themes are wired in Phase 1); mapped milestones to phases.
-   **Revision 1:** approved baseline.
