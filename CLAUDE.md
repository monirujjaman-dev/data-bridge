# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Current repository state

The repository is pre-Phase 0: there is no solution or project file
yet. Besides the design docs it holds the visual design system, drafted
ahead of Phase 1: `PRODUCT.md`, `DESIGN.md`, theme resources in
`src/DataBridge.Desktop/Themes/` and brand/font assets in
`src/DataBridge.Desktop/Assets/`. Phase 0 must place these inside the
new Desktop project rather than recreate them. Read these before
implementing; they are the source of truth:

-   `docs/requirements.md`: product scope, user journeys, acceptance
    criteria, v1.0 vs deferred features.
-   `docs/architecture.md`: solution layout, project responsibilities,
    provider abstractions, runtime flows, consistency model.
-   `docs/domain-model.md`: bounded contexts, schema/object identity,
    typed values, comparison and plan models, domain invariants.
-   `docs/development-plan.md`: phased delivery (Phase 0 bootstrap →
    Phase 8 release) with per-phase acceptance criteria. Work the
    current phase; don't build UI ahead of the use cases behind it.

This file fills the `AGENTS.md` role (agent/contributor instructions).
Terms such as *write target* are defined in `docs/requirements.md` §1.

## Commands

The planned layout (`DataBridge.sln`, `src/`, `tests/`, `benchmarks/`)
does not exist until Phase 0 lands. Once it does:

``` text
dotnet restore
dotnet build DataBridge.sln
dotnet format --verify-no-changes
dotnet test DataBridge.sln
dotnet test tests/DataBridge.Core.Tests --filter "FullyQualifiedName~<TestClassOrMethod>"
dotnet run --project src/DataBridge.Desktop
dotnet run -c Release --project benchmarks/DataBridge.Benchmarks
dotnet publish src/DataBridge.Desktop -c Release -r win-x64 --self-contained
```

`DataBridge.IntegrationTests` needs a disposable SQL Server instance
(the fixture is a Phase 2 task). Replace this section with the real
commands and SQL Server fixture setup once they exist.

## Big-picture architecture

Planned projects: `DataBridge.Desktop` (Avalonia, MVVM, composition
root), `DataBridge.Application` (use cases and ports),
`DataBridge.Core` (provider-independent domain),
`DataBridge.Infrastructure` (SQL Server provider, SQLite, credential
store) and `DataBridge.Shared` (generic result/error/diagnostic
primitives only).

Provider access is split into capability-focused ports defined in
Application, not one large interface: `IDatabaseProvider`,
`ISchemaReader`, `IDataReaderProvider`, `ISqlGenerator`,
`IDatabaseCommandExecutor`, `IBulkDataWriter` (optional),
`IDatabaseProviderFactory`, `IProviderCapabilities`. SQL Server is the
only v1 implementation; future providers must fit these without
changing Core.

Comparison flow: Desktop → Application use case → provider factory →
metadata readers build a normalized schema model → schema comparer
emits structured differences → data comparer streams both sides in
matching-key order and merges the key streams (source-only /
target-only / modified) → progress and summaries flow back and the UI
loads results incrementally.

Synchronization flow: select differences → planner builds an immutable
candidate plan → validator (dependencies, compatibility, target
identity, permissions) → user reviews operations, risk and generated
SQL → approve → executor revalidates the target, runs bounded
operations and records an execution manifest → verifier checks target
state independently of execution success.

Local persistence (SQLite) stores profile metadata, session summaries,
plan metadata/manifests, logs and preferences. Secrets go to the
OS-native credential store; full row-level differences are not stored
by default.

## Mission

Build DataBridge: a lightweight, cross-platform desktop
application for comparing and safely synchronizing two Microsoft SQL
Server databases. Version 1.0 supports SQL Server only and includes both
schema and data comparison.

## Approved architecture

-   Runtime/language: .NET 10 / C#.
-   Desktop: Avalonia UI, MVVM, CommunityToolkit.Mvvm.
-   Architecture: Clean Architecture, modular monolith.
-   Core/domain: provider-independent.
-   Application: use cases, orchestration, abstractions.
-   Infrastructure: SQL Server provider, metadata readers, data readers,
    SQL generation, execution, local persistence.
-   Database access: Microsoft.Data.SqlClient and ADO.NET; Dapper is
    permitted for suitable metadata queries.
-   Optional SQL Server tooling: Microsoft.SqlServer.DacFx behind an
    adapter. DacFx-specific types must not leak into Core or Application
    contracts.
-   Local application metadata: SQLite behind repository abstractions.
-   Logging: Microsoft.Extensions.Logging with Serilog provider/sink
    integration.
-   Tests: xUnit; BenchmarkDotNet for isolated performance benchmarks.
-   Deployment: self-contained builds for Windows, Linux and macOS.

## Non-negotiable product rules

1.  Compare schema and data as separate capabilities.
2.  Comparison must never modify either database.
3.  Synchronization is a separate, explicit workflow: compare → plan →
    validate → review/approve → execute → verify.
4.  Never execute destructive operations by default. DROP and DELETE
    require explicit opt-in and per-plan approval.
5.  A synchronization plan is immutable after approval. Any change
    requires a new plan and approval.
6.  Revalidate target identity and relevant target state immediately
    before execution.
7.  Never load an entire table into memory. Use streaming, bounded
    batches and bounded concurrency.
8.  Require a primary key, unique constraint or user-selected unique key
    for automatic row matching. Do not silently match rows by ordinal
    position.
9.  Preserve SQL Server data types and null semantics. Never compare
    arbitrary values by `ToString()`.
10. Parameterize values and safely quote identifiers using
    provider-specific rules.
11. Never log passwords, connection strings containing secrets, or row
    values by default.
12. Do not claim transactionality or rollback where SQL Server cannot
    guarantee it.
13. Do not block the UI thread. Long-running work must support progress
    and cancellation.
14. Keep the Core project free of Avalonia, SqlClient, DacFx, SQLite and
    other infrastructure dependencies.
15. Do not introduce microservices, a mandatory backend server, message
    brokers, or unnecessary mediator frameworks.

## Repository conventions

-   Use nullable reference types and implicit usings.
-   Use async APIs and pass `CancellationToken` through all I/O and
    long-running operations.
-   Use `DateTimeOffset` and UTC-suffixed property names
    (`CreatedAtUtc`, `StartedAtUtc`, etc.).
-   Prefer immutable records/value objects for comparison results and
    plans; make state transitions explicit.
-   Use `Guid` identifiers for sessions, plans and operations.
-   Use descriptive names; avoid abbreviations except established
    technical terms.
-   Keep methods cohesive and small. Avoid giant provider interfaces and
    god services.
-   Keep SQL Server-specific code under
    `Infrastructure/Providers/SqlServer`.
-   Centralize package versions in `Directory.Packages.props`; pin the
    SDK in `global.json` to an available .NET 10 SDK.
-   Treat warnings as errors in CI after the baseline is clean.
-   Add XML documentation to public abstractions and non-obvious
    algorithms.
-   Add tests with every behavior change.

## Dependency direction

-   Core → Shared only, where Shared contains truly generic primitives.
-   Application → Core and Shared.
-   Infrastructure → Application abstractions, Core and Shared.
-   Desktop → Application and Infrastructure composition root.
-   Core must not reference Application, Infrastructure or Desktop.
-   Application must not reference Infrastructure or Desktop.
-   Infrastructure must not reference Desktop.

## Path-scoped rules and project tooling

Detailed rules live in `.claude/rules/` and load when matching files are
touched:

-   `database-correctness.md`: Core, comparison use cases, providers,
    integration tests.
-   `synchronization-safety.md`: planning, script generation, SQL Server
    provider.
-   `desktop-ui.md`: Desktop project and its tests.

Project agents: `dependency-rule-checker` (after reference/namespace
changes) and `sql-safety-reviewer` (after SQL, provider, comparison or
sync changes). The `verify` skill runs format/build/test and produces
the change report the workflow below requires.

## Required engineering workflow

1.  Read the relevant docs before implementation.
2.  Identify affected projects and dependency rules.
3.  Implement the smallest coherent vertical slice.
4.  Add/update unit and integration tests.
5.  Run formatting, build and tests.
6.  For SQL changes, test against disposable SQL Server databases.
7.  For performance-sensitive changes, add a reproducible benchmark or
    explain why not.
8.  Update docs and ADRs when changing approved architecture.
9.  Report changed files, commands run, test results and known
    limitations.

## Definition of done

-   Builds on .NET 10.
-   Tests pass.
-   No dependency direction violations.
-   Cancellation and error paths are tested for I/O operations.
-   No secrets or sensitive row values are logged.
-   Database behavior is covered by integration tests.
-   Documentation reflects actual behavior.
-   Destructive actions are protected by explicit approval.
