# DataBridge --- Architecture

**Status:** Approved baseline (revision 6, 2026-11-15)\
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
│   ├── DataBridge.PerformanceTests/
│   └── DataBridge.ArchitectureTests/
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
`DataBridge.ArchitectureTests` enforces the dependency rules in §4 by
inspecting project files and compiled assemblies; it is the only test
project that references several production projects.

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
-   Schema reading (`Providers/SqlServer/Schema`): one read-only batch
    of catalog queries (`SqlServerCatalogQuery`) assembled into the
    normalized model. The catalog is not versioned, so concurrent DDL
    can make result sets disagree; an orphaned child is reported as
    "schema changed while reading" rather than guessed at. Module and
    expression text is compared after `TSqlDefinitionNormalizer`, which
    ignores line endings and indentation but never changes literals,
    quoted identifiers or line-comment boundaries. Object scope:
    ADR-0012; DacFx is not used (ADR-0013).

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
sides, and compare them ordinally in the merge. Non-string keys
(integers, `uniqueidentifier`, temporal, decimal) need type-specific
comparers that follow SQL Server ordering; `uniqueidentifier` ordering
in SQL Server differs from `Guid.CompareTo`.

Decided in ADR-0014: one ordered streaming query per table and side;
the merge verifies that each stream is strictly increasing under the
in-memory comparer and stops the table (`DuplicateKeysFound`,
`KeyOrderMismatch`) instead of producing wrong rows. Keys are matched
exactly: keys that a case- or accent-insensitive database treats as
equal are reported as separate source-only and target-only rows, and
whether the target accepts such a row is validated in Phase 5. Key
columns must not allow NULL.

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

As implemented in Phase 5:

-   Planning (`Core/Synchronization`) is pure: the schema and data
    planners turn selected differences into changes with risk and
    dependencies, and `PlanAssembler` generates each change's SQL
    through `ISqlGenerator`, orders the changes (`DependencyOrdering`)
    and reports cycles and everything left out. Scope and generation
    rules: ADR-0017. Data operations reference recorded change sets:
    ADR-0016.
-   The plan records the write target's identity fingerprint, its
    `SchemaFingerprint` as compared and its profile's environment
    label. Validation re-resolves the identity, re-reads the schema,
    verifies the change sets, and asks `ISynchronizationTargetInspector`
    for missing permissions and for existing rows that would make an
    operation fail. Any change invalidates the plan (FR-PLN-012).
-   Approval needs a validated plan, the write target confirmed, each
    High, Critical and destructive operation approved, and for a
    Production write target the database name typed (ADR-0015). It is
    bound to the content hash; `PlanApprovalPolicy.Authorizes` checks
    it again against the write target resolved before execution.
-   The exported script is for review: it stops (`SET NOEXEC ON`)
    unless the database name, server name and, when known, database
    GUID match the plan's write target, and shows data operations as
    commented-out templates. Text from the databases is written into
    comments with line breaks and `$(` broken up, and the names the
    guard checks are written as hex (`CAST(0x... AS nvarchar)`) so that
    SSMS and sqlcmd cannot split or substitute into them. The plan also records the copied
    side's identity fingerprint; all of these are covered by the
    content hash.

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

The one exception is a synchronization plan's change sets (ADR-0016):
the key values and fingerprints of the rows a plan changes, in files
under the local application data folder
(`Infrastructure/Persistence/ChangeSets`), deleted with their plan or
after seven days. Plans themselves are kept in memory for the session
until history arrives in Phase 7.

### Credential storage

Secrets are stored in OS-native credential storage behind an
Application port:

-   Windows: Credential Manager.
-   macOS: Keychain.
-   Linux: Secret Service (libsecret). Headless or minimal desktops may
    not provide it; in that case the secret is not saved and the user is
    prompted per session (FR-CON-009).

There is no plaintext or obfuscated fallback.

The adapters (`Infrastructure/Security`) call the OS APIs directly,
with no third-party packages: `CredWriteW`/`CredReadW` (generic
credentials, per user), the `SecItem` API, and libsecret's
non-variadic `secret_password_*v_sync` functions. macOS and Linux
libraries are loaded at run time, so a missing library makes the store
report itself unavailable instead of crashing. Store keys
(`DataBridge/connection-profile/<profile id>/<random suffix>`) contain
no user data. A new password is always written to a new key, and the
old key is deleted only after the profile row points at the new one,
so a failed save never loses the previous password.

## 12. UI architecture

-   Avalonia + MVVM + CommunityToolkit.Mvvm.
-   Views are presentation-only.
-   View models call Application use cases.
-   Use virtualized grids and incremental paging.
-   Use background tasks, cancellation and progress.
-   Never marshal huge result sets to the UI thread.
-   View models run every Application call on the thread pool and resume
    on the UI thread: some ports (SQLite) complete synchronously behind
    async signatures, so awaiting them directly would block the UI.
-   The composition root (`Desktop/DependencyInjection`) builds a
    Generic Host with DI validation on build; pages are singletons so
    their state survives navigation. The rail lists only screens whose
    use cases exist.
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

Implementation (`Infrastructure/Logging`): code logs through
`ILogger<T>`, using source-generated `[LoggerMessage]` methods in
production code. Serilog writes compact JSON, one event per line, to a
daily rolling file (`databridge-YYYYMMDD.log`, 10 MB per file, 14
files kept) in `DataBridge/logs` under the local application data
folder. Logging scopes become event properties, so correlation ids
(`SessionId`, `PlanId`, `OperationId`) are added with
`ILogger.BeginScope`. Redaction has three layers:

1.  `Secret` values always render as `[redacted]`.
2.  Properties whose names denote a secret (`Password`, `Pwd`,
    `Secret`, `Token`, `ApiKey`, …), including nested object,
    dictionary and scope values, are replaced; secrets inside string
    values such as connection strings are removed.
3.  Every formatted line is scrubbed for `key=value` and JSON secrets,
    which also covers exception text and literal template text.

Redaction is a safety net: log events carry ids and outcomes, never
profile names, servers, user names or row values.

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

Each of these is recorded as an ADR (0001--0008) in `docs/adr/`; see
`docs/adr/README.md` for the index and how to add or supersede one.

## 16. Open design decisions

| Decision | Needed by | Notes |
|----------|-----------|-------|
| ~~Data-operation materialization~~ | Phase 5 | Decided in ADR-0016: plans reference the keys and fingerprints recorded during comparison; execution re-checks each row before writing it. |
| ~~Key ordering strategy~~ | Phase 4 | Decided in ADR-0014: binary-collation ordering with order verification in the merge, exact key matching. |
| Transaction granularity | Phase 6 | Per operation, per batch or per plan; user-configurable within the limits SQL Server can guarantee. |
| ~~DacFx adoption~~ | Phase 3 | Decided in ADR-0013: not used for schema reading or comparison in v1; script generation would need its own evaluation. |
| ~~SQL Server test fixture~~ | Phase 2 | Decided in ADR-0011: disposable SQL Server 2022 containers (Testcontainers); x64 only, so ARM machines use emulation or a remote instance. |

## 17. Revision history

-   **Revision 6 (2026-11-15):** synchronization planning as
    implemented (§10); change sets in local persistence (§11); closed
    data-operation materialization (ADR-0016); environment labels
    (ADR-0015) and schema synchronization scope (ADR-0017).
-   **Revision 5 (2026-11-15):** key ordering, row streaming and
    consistency decided (ADR-0014).
-   **Revision 4 (2026-11-15):** schema reading in §7; closed the DacFx
    decision (ADR-0013).
-   **Revision 3 (2026-11-14):** added `DataBridge.ArchitectureTests`
    to the solution structure; recorded §15 decisions as ADRs
    0001--0008.
-   **Revision 2 (2026-11-14):** fixed list formatting; aligned
    solution tree with the repository (`CLAUDE.md`, `PRODUCT.md`,
    `DESIGN.md`, `.claude/`); clarified PerformanceTests vs Benchmarks;
    added catalog views, permission/encryption handling, key ordering
    and collation, isolation constraints, data-operation rules, risk
    baseline, credential storage per OS and open design decisions.
-   **Revision 1:** approved baseline.
