# DataBridge --- Architecture

**Status:** Approved baseline (revision 2, 2026-11-14)\
**Runtime:** .NET 10\
**UI:** Avalonia\
**Architecture:** Clean Architecture, modular monolith

## 1. Overview

DataBridge is a local-first desktop application. It connects
directly to two SQL Server databases. No application server is required.

The system has five logical areas:

1.  Presentation: Avalonia views and MVVM view models.
2.  Application: use cases, orchestration, progress and abstractions.
3.  Core: provider-independent domain models and rules.
4.  Infrastructure: SQL Server provider, readers, generators, executor
    and local persistence.
5.  Shared: minimal generic primitives only.

Terms such as *write target* are defined in `requirements.md` §1.

## 2. Solution structure

``` text
DataBridge/
├── DataBridge.sln
├── global.json
├── Directory.Build.props
├── Directory.Packages.props
├── .editorconfig
├── README.md
├── CLAUDE.md              # agent/contributor instructions (the AGENTS.md role)
├── PRODUCT.md             # product context for design work
├── DESIGN.md              # visual design system
├── .claude/               # path-scoped rules, project agents, skills
├── docs/
│   ├── requirements.md
│   ├── architecture.md
│   ├── domain-model.md
│   ├── development-plan.md
│   └── adr/
├── src/
│   ├── DataBridge.Desktop/
│   │   ├── Views/
│   │   ├── ViewModels/
│   │   ├── Controls/
│   │   ├── Themes/
│   │   ├── Assets/
│   │   └── DependencyInjection/
│   ├── DataBridge.Application/
│   │   ├── Abstractions/
│   │   ├── Connections/
│   │   ├── SchemaComparison/
│   │   ├── DataComparison/
│   │   ├── Synchronization/
│   │   ├── ScriptGeneration/
│   │   └── Reports/
│   ├── DataBridge.Core/
│   │   ├── Connections/
│   │   ├── Schema/
│   │   ├── Data/
│   │   ├── Comparison/
│   │   ├── Synchronization/
│   │   └── ValueObjects/
│   ├── DataBridge.Infrastructure/
│   │   ├── Providers/SqlServer/
│   │   ├── Persistence/
│   │   ├── Security/
│   │   ├── Serialization/
│   │   └── DependencyInjection/
│   └── DataBridge.Shared/
│       ├── Results/
│       ├── Errors/
│       └── Diagnostics/
├── tests/
│   ├── DataBridge.Core.Tests/
│   ├── DataBridge.Application.Tests/
│   ├── DataBridge.Infrastructure.Tests/
│   ├── DataBridge.Desktop.Tests/
│   ├── DataBridge.IntegrationTests/
│   └── DataBridge.PerformanceTests/
├── benchmarks/
│   └── DataBridge.Benchmarks/
├── scripts/
└── deploy/
    ├── windows/
    ├── linux/
    └── macos/
```

`DataBridge.PerformanceTests` holds pass/fail tests with thresholds
(for example, memory stays bounded while comparing a large table).
`DataBridge.Benchmarks` holds BenchmarkDotNet measurements that are run
on demand and produce reports, not pass/fail results.

## 3. Project responsibilities

### Desktop

-   Avalonia application shell, navigation, views, view models and UI
    composition.
-   Registers application and infrastructure services.
-   Never contains SQL or comparison algorithms.
-   Uses async commands and progress/cancellation presentation.

### Application

-   Use cases: test connection, read schema, compare schema, compare
    data, build plan, validate, execute, verify and export.
-   Defines ports/interfaces implemented by Infrastructure.
-   Coordinates long-running work and reports progress.
-   Does not reference concrete SQL Server driver types.

### Core

-   Domain entities, value objects, comparison semantics, plan models
    and domain rules.
-   No UI, database driver, DacFx or SQLite dependencies.
-   Uses typed database values and stable object identities.

### Infrastructure

-   SQL Server connections, catalog queries, data streaming, SQL
    generation, DML/DDL execution, optional DacFx adapter, OS
    credential-store adapters and SQLite persistence.
-   SQL Server-specific code stays under `Providers/SqlServer`.

### Shared

-   Only generic result/error/diagnostic primitives used across
    projects. Do not turn this into a dumping ground.

## 4. Dependency rules

-   Shared → base class library only.
-   Core → Shared.
-   Application → Core, Shared.
-   Infrastructure → Application, Core, Shared.
-   Desktop → Application, Infrastructure (composition root only).
-   Tests reference only the project under test and necessary test
    infrastructure.
-   No reverse dependencies.

## 5. Runtime flow

### Comparison

1.  Desktop sends a use-case request to Application.
2.  Application resolves provider through a factory.
3.  Provider opens source and target connections and resolves both
    identities.
4.  Metadata readers create normalized schema models.
5.  Schema comparer emits structured differences.
6.  Data comparer streams ordered key/value batches.
7.  Application publishes progress and stores summary/history.
8.  Desktop incrementally displays results.

### Synchronization

1.  User selects differences and direction; the write target follows
    from the direction.
2.  Planner creates an immutable candidate plan.
3.  Validator checks dependencies, compatibility, write-target identity
    and permissions.
4.  UI displays operations, risk and generated SQL.
5.  User explicitly approves; the approval is bound to the plan's
    content hash.
6.  Executor revalidates write-target state.
7.  Executor applies bounded operations and records outcomes.
8.  Verifier checks write-target state and produces report.

## 6. Provider architecture

Use capability-focused abstractions rather than one large provider
interface:

-   `IDatabaseProvider`: provider identity and connection/database
    information.
-   `ISchemaReader`: normalized schema extraction.
-   `IDataReaderProvider`: typed, ordered batch streaming.
-   `ISqlGenerator`: provider-specific DDL/DML generation.
-   `IDatabaseCommandExecutor`: operation execution.
-   `IBulkDataWriter`: optional bulk-write capability.
-   `IDatabaseProviderFactory`: resolve provider.
-   `IProviderCapabilities`: advertise supported object/value/operation
    capabilities.

The first provider is SQL Server. Future providers must implement the
same application-facing capabilities without changing Core.

## 7. SQL Server implementation

-   Driver: Microsoft.Data.SqlClient. It encrypts connections by
    default; `TrustServerCertificate` must stay off unless the user
    explicitly enables it per profile, and the UI shows that choice.
-   Catalog metadata: `sys.schemas`, `sys.tables`, `sys.columns`,
    `sys.types`, `sys.identity_columns`, `sys.computed_columns`,
    `sys.indexes`, `sys.index_columns`, `sys.key_constraints`,
    `sys.foreign_keys`, `sys.foreign_key_columns`,
    `sys.check_constraints`, `sys.default_constraints`, `sys.triggers`,
    `sys.objects`, `sys.sql_modules`, `sys.sequences`,
    `sys.sql_expression_dependencies` (for the dependency graph) and
    relevant catalog views.
-   Permissions: without `VIEW DEFINITION`, metadata is partial and
    module definitions are NULL. Modules created `WITH ENCRYPTION` also
    have NULL definitions. Both cases are reported as unreadable, never
    as "equal" or "missing".
-   Data reads: `DbDataReader`/`SqlDataReader` streaming, ordered by
    matching key, with `CommandBehavior.SequentialAccess` for LOB
    columns.
-   Bulk insert: `SqlBulkCopy` when applicable and validated.
-   SQL generation: provider-specific identifier quoting and parameter
    binding.
-   Optional DacFx: isolated adapter for supported schema
    comparison/deployment features. Do not expose DacFx types in
    Core/Application contracts.

## 8. Comparison design

### Schema

-   Read source and target metadata.
-   Normalize only semantically safe properties.
-   Match objects by `DatabaseObjectId` (schema, name, object type and,
    for table children, parent table); see `domain-model.md` §5.
-   Compare object-specific properties.
-   Preserve ordered index/key columns and relevant options.
-   Represent unsupported/incompatible cases explicitly.
-   Build dependency graph for synchronization.

### Data

-   Select primary key, unique constraint or user-selected unique key.
-   Reject unsafe automatic matching when no unique key exists.
-   For a user-selected key, detect duplicate keys while merging (two
    consecutive equal keys on one side) and stop matching that table.
-   Match columns by name; report and exclude one-sided columns and
    columns whose source/target types cannot be compared safely.
-   Read both sides in deterministic key order.
-   Merge key streams to detect source-only and target-only rows.
-   Compare typed values for matching keys.
-   Optionally use canonical typed row fingerprints as an optimization.
-   Fetch/compare actual values for candidate changes when required.
-   Use bounded batches, buffers and concurrency.

#### Key ordering and collation

The merge is only correct if the in-memory key comparer orders keys
exactly as each server's `ORDER BY` did. String keys are the risk:
SQL Server sorts them by collation, which may be case- or
accent-insensitive and may differ between source and target.

Baseline strategy: order string key columns with an explicit binary
collation (`COLLATE Latin1_General_100_BIN2` or equivalent) on both
sides, and compare them ordinally in the merge. Because the database
may treat keys that differ only by case or accent as equal, the
comparer reports such near-matches as collation-sensitive differences
instead of silently treating them as separate rows. Non-string keys
(integers, `uniqueidentifier`, temporal, decimal) need type-specific
comparers that follow SQL Server ordering; `uniqueidentifier` ordering
in SQL Server differs from `Guid.CompareTo`. Confirm the strategy in an
ADR during Phase 4.

## 9. Consistency

The application must define read consistency per comparison:

-   Offer snapshot isolation when it is already enabled
    (`ALLOW_SNAPSHOT_ISOLATION ON`) and authorized. Comparison never
    changes database settings to enable it.
-   Warn that long snapshot reads hold row versions in the server's
    tempdb version store.
-   Otherwise use an explicitly documented isolation strategy. Never use
    `READ UNCOMMITTED`/`NOLOCK`: dirty reads can skip or duplicate rows
    and break the ordered merge.
-   Record the isolation achieved on each side in the comparison
    session.
-   Report that results may reflect concurrent changes if a stable
    snapshot is not guaranteed.
-   Do not imply that two independent database snapshots represent the
    same point in time.
-   Revalidate the write target before synchronization; comparison
    output is not an execution lock.

## 10. Synchronization design

-   Plan content is immutable from creation; any change produces a new
    plan. Approval is a separate record bound to the plan's content
    hash, and plan lifecycle status is tracked outside the plan content
    (`domain-model.md` §9).
-   Each operation has ID, object identity, type, dependencies, risk and
    destructive flag. Execution state lives in the execution manifest.
-   Dependency graph is topologically sorted; cycles are reported.
-   Destructive operations are disabled by default.
-   DML uses parameters.
-   Identifiers are quoted with SQL Server-specific rules.
-   Transaction boundaries are explicit; DDL transaction support is
    assessed operation by operation.
-   Data operations exclude computed and `rowversion` columns, scope
    `SET IDENTITY_INSERT` to one table at a time, and order inserts and
    deletes by foreign-key dependencies.
-   Triggers and constraints on the write target are never disabled
    automatically; triggers that will fire are shown as warnings.
-   Execution manifest records completed, failed, skipped and pending
    operations.
-   Verification is separate from execution success.

### Risk classification baseline

| Risk | Examples |
|------|----------|
| Low | Create a new object; insert rows into a table |
| Medium | Add a nullable column; update rows; alter a view, procedure or function |
| High | Alter that can fail on existing data or lose precision (narrowing type, adding NOT NULL, adding a constraint that existing rows may violate); large affected-row counts |
| Critical | Drop an object or column; delete rows; any operation on a table without a verified matching key |

Every High or Critical operation, and every destructive operation,
requires explicit per-plan approval. Thresholds such as "large" are
configurable and recorded with the plan.

## 11. Local persistence

SQLite stores:

-   Connection profile metadata (not plaintext secrets).
-   Comparison sessions and summaries.
-   Plan metadata and operation manifests.
-   Execution logs and user preferences.

Use repository abstractions and schema migrations. Do not store full
row-level differences by default.

### Credential storage

Secrets are stored in OS-native credential storage behind an
Application port:

-   Windows: Credential Manager.
-   macOS: Keychain.
-   Linux: Secret Service (libsecret). Headless or minimal desktops may
    not provide it; in that case the secret is not saved and the user is
    prompted per session (FR-CON-009).

There is no plaintext or obfuscated fallback.

## 12. UI architecture

-   Avalonia + MVVM + CommunityToolkit.Mvvm.
-   Views are presentation-only.
-   View models call Application use cases.
-   Use virtualized grids and incremental paging.
-   Use background tasks, cancellation and progress.
-   Never marshal huge result sets to the UI thread.
-   Display source, target and write-target identity prominently.
-   Visual design (tokens, route sign, components) follows `DESIGN.md`;
    theme resources live under `src/DataBridge.Desktop/Themes/`.

## 13. Observability and diagnostics

-   Microsoft.Extensions.Logging abstractions with Serilog
    configuration.
-   Structured logs with correlation IDs for session, plan and
    operation.
-   Redact credentials and sensitive values.
-   Capture duration, rows scanned, batches, differences and throughput.
-   Preserve inner exception details in diagnostic logs, with
    secret/data redaction.
-   User-facing errors should be actionable without exposing secrets.

## 14. Deployment

-   Self-contained publish for each supported RID.
-   Windows: `win-x64` initially; add `win-arm64` if validated.
-   Linux: `linux-x64`; validate distro/runtime dependencies and
    packaging.
-   macOS: `osx-arm64` and `osx-x64` as required.
-   Test Avalonia native dependencies and SQL authentication modes on
    every target OS. Integrated authentication on Linux and macOS needs
    Kerberos configuration on the client machine; document it.
-   Code-sign/notarize where release infrastructure permits.

## 15. Architecture decisions

-   Desktop rather than web: local database access and no mandatory
    server.
-   Avalonia rather than WPF: cross-platform UI.
-   Modular monolith rather than microservices: local cohesive workload.
-   ADO.NET rather than EF Core in comparison engine: streaming and
    low-level control.
-   Custom normalized schema model: provider-independent comparison.
-   Optional DacFx: reuse SQL Server tooling without coupling the
    product.
-   SQLite for local history: small embedded persistence.
-   No automatic bidirectional sync in v1: conflict semantics require a
    separate design.

Record each of these as an ADR in `docs/adr/` during Phase 0.

## 16. Open design decisions

| Decision | Needed by | Notes |
|----------|-----------|-------|
| Data-operation materialization | Phase 5 | A plan cannot hold every row (product rule 7). Options: re-stream source rows by key at execution and apply only rows whose comparison fingerprint still matches; or stage changed keys in a local or target-side staging table. Whatever is chosen, the reviewed plan must describe exactly what executes, and drift since comparison must be detected, not applied silently. |
| Key ordering strategy | Phase 4 | Baseline in §8; confirm with integration tests under CI/AS collations. |
| Transaction granularity | Phase 6 | Per operation, per batch or per plan; user-configurable within the limits SQL Server can guarantee. |
| DacFx adoption | Phase 3 | Depends on cross-platform evaluation. |
| SQL Server test fixture | Phase 2 | SQL Server container images are x64 only; ARM developer machines need emulation or a remote instance. |

## 17. Revision history

-   **Revision 2 (2026-11-14):** fixed list formatting; aligned
    solution tree with the repository (`CLAUDE.md`, `PRODUCT.md`,
    `DESIGN.md`, `.claude/`); clarified PerformanceTests vs Benchmarks;
    added catalog views, permission/encryption handling, key ordering
    and collation, isolation constraints, data-operation rules, risk
    baseline, credential storage per OS and open design decisions.
-   **Revision 1:** approved baseline.
