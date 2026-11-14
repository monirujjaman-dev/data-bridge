# DataBridge --- Architecture

**Status:** Approved baseline\
**Runtime:** .NET 10\
**UI:** Avalonia\
**Architecture:** Clean Architecture, modular monolith

## 1. Overview

DataBridge is a local-first desktop application. It connects
directly to two SQL Server databases. No application server is required.

The system has five logical areas: 1. Presentation: Avalonia views and
MVVM view models. 2. Application: use cases, orchestration, progress and
abstractions. 3. Core: provider-independent domain models and rules. 4.
Infrastructure: SQL Server provider, readers, generators, executor and
local persistence. 5. Shared: minimal generic primitives only.

## 2. Solution structure

``` text
DataBridge/
├── DataBridge.sln
├── global.json
├── Directory.Build.props
├── Directory.Packages.props
├── .editorconfig
├── README.md
├── AGENTS.md
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

-   Core → Shared.
-   Application → Core, Shared.
-   Infrastructure → Application, Core, Shared.
-   Desktop → Application, Infrastructure.
-   Tests reference only the project under test and necessary test
    infrastructure.
-   No reverse dependencies.

## 5. Runtime flow

### Comparison

1.  Desktop sends a use-case request to Application.
2.  Application resolves provider through a factory.
3.  Provider opens source and target connections.
4.  Metadata readers create normalized schema models.
5.  Schema comparer emits structured differences.
6.  Data comparer streams ordered key/value batches.
7.  Application publishes progress and stores summary/history.
8.  Desktop incrementally displays results.

### Synchronization

1.  User selects differences and direction.
2.  Planner creates immutable candidate plan.
3.  Validator checks dependencies, compatibility, target identity and
    permissions.
4.  UI displays operations, risk and generated SQL.
5.  User explicitly approves.
6.  Executor revalidates target state.
7.  Executor applies bounded operations and records outcomes.
8.  Verifier checks target state and produces report.

## 6. Provider architecture

Use capability-focused abstractions rather than one large provider
interface: - `IDatabaseProvider`: provider identity and
connection/database information. - `ISchemaReader`: normalized schema
extraction. - `IDataReaderProvider`: typed, ordered batch streaming. -
`ISqlGenerator`: provider-specific DDL/DML generation. -
`IDatabaseCommandExecutor`: operation execution. - `IBulkDataWriter`:
optional bulk-write capability. - `IDatabaseProviderFactory`: resolve
provider. - `IProviderCapabilities`: advertise supported
object/value/operation capabilities.

The first provider is SQL Server. Future providers must implement the
same application-facing capabilities without changing Core.

## 7. SQL Server implementation

-   Driver: Microsoft.Data.SqlClient.
-   Catalog metadata: `sys.schemas`, `sys.tables`, `sys.columns`,
    `sys.types`, `sys.indexes`, `sys.index_columns`,
    `sys.key_constraints`, `sys.foreign_keys`,
    `sys.foreign_key_columns`, `sys.check_constraints`,
    `sys.default_constraints`, `sys.objects`, `sys.sql_modules`,
    `sys.sequences` and relevant catalog views.
-   Data reads: `DbDataReader`/`SqlDataReader` streaming, ordered by
    matching key.
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
-   Match objects using schema, name and object type.
-   Compare object-specific properties.
-   Preserve ordered index/key columns and relevant options.
-   Represent unsupported/incompatible cases explicitly.
-   Build dependency graph for synchronization.

### Data

-   Select primary key, unique constraint or user-selected unique key.
-   Reject unsafe automatic matching when no unique key exists.
-   Read both sides in deterministic key order.
-   Merge key streams to detect source-only and target-only rows.
-   Compare typed values for matching keys.
-   Optionally use canonical typed row fingerprints as an optimization.
-   Fetch/compare actual values for candidate changes when required.
-   Use bounded batches, buffers and concurrency.

## 9. Consistency

The application must define read consistency per comparison: - Offer
snapshot isolation when supported, enabled and authorized. - Otherwise
use an explicitly documented isolation strategy. - Report that results
may reflect concurrent changes if a stable snapshot is not guaranteed. -
Do not imply that two independent database snapshots represent the same
point in time. - Revalidate target before synchronization; comparison
output is not an execution lock.

## 10. Synchronization design

-   Plan is immutable after approval.
-   Each operation has ID, object identity, type, dependencies, risk,
    validation and execution state.
-   Dependency graph is topologically sorted; cycles are reported.
-   Destructive operations are disabled by default.
-   DML uses parameters.
-   Identifiers are quoted with SQL Server-specific rules.
-   Transaction boundaries are explicit; DDL transaction support is
    assessed operation by operation.
-   Execution manifest records completed, failed, skipped and pending
    operations.
-   Verification is separate from execution success.

## 11. Local persistence

SQLite stores: - Connection profile metadata (not plaintext secrets). -
Comparison sessions and summaries. - Plan metadata and operation
manifests. - Execution logs and user preferences.

Use repository abstractions and schema migrations. Do not store full
row-level differences by default. Store secrets in OS-native credential
storage.

## 12. UI architecture

-   Avalonia + MVVM + CommunityToolkit.Mvvm.
-   Views are presentation-only.
-   View models call Application use cases.
-   Use virtualized grids and incremental paging.
-   Use background tasks, cancellation and progress.
-   Never marshal huge result sets to the UI thread.
-   Display source/target identity prominently.

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
    every target OS.
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
