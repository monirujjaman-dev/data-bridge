# DataBridge --- Development Plan

## 1. Delivery strategy

Deliver thin vertical slices. Each phase must build, run and have
automated tests. Do not build all UI screens before the underlying use
cases work.

## 2. Phase 0 --- Repository bootstrap

### Tasks

-   Create solution and project structure.
-   Add .NET 10 SDK pin and central package management.
-   Configure nullable reference types, analyzers and formatting.
-   Configure CI build/test workflow.
-   Add architecture dependency tests.
-   Add README and ADR directory.

### Acceptance

-   Clean clone restores and builds.
-   CI runs build and tests.
-   Project references obey dependency rules.

## 3. Phase 1 --- Desktop foundation and local settings

### Tasks

-   Create Avalonia application shell.
-   Configure Generic Host and dependency injection.
-   Add MVVM toolkit.
-   Add navigation and theme resources.
-   Add SQLite persistence abstraction and migrations.
-   Add connection profile CRUD.
-   Add OS credential-store abstraction; do not implement insecure
    plaintext fallback.
-   Add structured logging and redaction.

### Acceptance

-   App starts on supported development OS.
-   Profiles can be created, edited and deleted.
-   Secrets are not present in SQLite or logs.
-   UI remains responsive.

## 4. Phase 2 --- SQL Server connectivity

### Tasks

-   Implement connection factory and provider factory.
-   Implement SQL authentication.
-   Implement supported integrated authentication.
-   Configure encryption and certificate validation.
-   Test connection and retrieve server/database identity.
-   Add timeout and cancellation behavior.
-   Add integration-test fixture using disposable SQL Server databases.

### Acceptance

-   Connect to two SQL Server databases.
-   Wrong credentials and unreachable servers produce actionable errors.
-   Connection secrets are redacted.
-   Cancellation and timeout are tested.

## 5. Phase 3 --- Schema metadata and comparison

### Tasks

-   Implement schema model and object identity.
-   Implement catalog readers for schemas, tables and columns.
-   Add keys, constraints, indexes and sequences.
-   Add views, procedures, functions and triggers.
-   Implement safe normalization and object-specific comparers.
-   Implement structured difference results.
-   Evaluate optional DacFx adapter on all target operating systems.
-   Build schema comparison UI.

### Acceptance

-   Identical schemas produce no differences for supported objects.
-   Fixtures with controlled changes produce expected object/property
    differences.
-   Unsupported objects are reported.
-   No schema comparison modifies databases.

## 6. Phase 4 --- Data comparison

### Tasks

-   Discover primary/unique keys.
-   Add user-selected matching keys.
-   Implement typed value model and type comparers.
-   Implement streaming reader and keyset pagination.
-   Implement composite-key continuation.
-   Implement ordered merge comparison.
-   Add optional canonical row fingerprints.
-   Add bounded concurrency, progress and cancellation.
-   Build virtualized/incremental data difference UI.

### Acceptance

-   Detect source-only, target-only and modified rows.
-   Correctly compare NULL, decimals, temporal, Unicode and binary test
    cases.
-   Composite keys work.
-   Tables without a unique key are reported as unsupported for
    automatic matching.
-   Memory remains bounded in large-table tests.

## 7. Phase 5 --- Synchronization planning and SQL generation

### Tasks

-   Implement operation and plan models.
-   Implement schema dependency graph.
-   Implement topological ordering and cycle detection.
-   Implement SQL Server DDL/DML generation.
-   Implement parameterized data operations.
-   Implement operation risk classification.
-   Implement validation and target fingerprinting.
-   Implement script preview/export.
-   Implement approval and plan immutability.

### Acceptance

-   Plans can be generated without database mutation.
-   Dependencies are ordered correctly.
-   Cycles and unsupported operations are reported.
-   DROP/DELETE are disabled unless explicitly enabled and approved.
-   Approved plans cannot be edited.

## 8. Phase 6 --- Execution and verification

### Tasks

-   Implement executor and operation state tracking.
-   Implement explicit transaction/batch boundaries.
-   Add SqlBulkCopy strategy where suitable.
-   Add identity handling and foreign-key validation.
-   Revalidate target immediately before execution.
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

### Tasks

-   Add comparison history and retention.
-   Add HTML/CSV report export.
-   Add filters, search and object navigation.
-   Add dark/light themes and keyboard accessibility.
-   Add error diagnostics and support bundle with redaction.
-   Profile UI rendering and memory.

### Acceptance

-   History can be reopened.
-   Reports contain summary and selected details.
-   Large result sets remain virtualized.
-   Secrets and row values are not leaked in reports/logs by default.

## 10. Phase 8 --- Performance, security and release

### Tasks

-   Benchmark 10K, 100K, 1M and 10M row scenarios.
-   Profile allocations, CPU, network and query plans.
-   Test wide rows, binary/LOB columns and skewed keys.
-   Review credential storage and SQL generation.
-   Test Windows, Linux and macOS publishing.
-   Add packaging, signing and release notes.
-   Document supported SQL Server versions and limitations.

### Acceptance

-   Reproducible performance report exists.
-   Memory is bounded by batch/concurrency configuration.
-   All mandatory tests pass.
-   Packages launch on each supported OS.
-   Known limitations are documented.

## 11. Suggested milestone gates

-   M1: desktop shell + secure connection profiles.
-   M2: connect to two SQL Server databases.
-   M3: schema comparison prototype.
-   M4: data comparison prototype.
-   M5: synchronization plan preview.
-   M6: controlled execution and verification.
-   M7: release candidate.

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
