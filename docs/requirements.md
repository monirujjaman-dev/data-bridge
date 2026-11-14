# DataBridge --- Product Requirements

**Version:** 1.0 baseline (revision 2, 2026-11-14)\
**Status:** Approved\
**Product type:** Cross-platform desktop application\
**Initial provider:** Microsoft SQL Server

## 1. Product vision

DataBridge enables developers and database administrators to
compare the schema and data of two SQL Server databases, inspect
differences, create synchronization plans, review generated SQL, apply
approved changes and verify the target.

The application is local-first and does not require a hosted backend.

### Terminology

-   **Source / Target:** the two sides of a comparison, as the user
    selected them.
-   **Write target:** the database a synchronization plan modifies. For
    Source → Target it is the Target; for Target → Source it is the
    Source. Every "revalidate the target" rule in these documents means
    the write target.
-   **Destructive operation:** any operation that can remove objects,
    columns or rows, or lose data (DROP, DELETE, narrowing or lossy
    ALTER).

## 2. Goals

-   Provide accurate schema and data comparison.
-   Remain responsive on large databases through streaming and bounded
    memory.
-   Make synchronization predictable, reviewable and safe.
-   Provide a simple workflow for selecting source and target databases.
-   Run on Windows, Linux and macOS.
-   Keep provider-specific implementation replaceable for future
    database engines.

## 3. Non-goals for version 1.0

-   PostgreSQL, MySQL and SQLite as comparison targets.
-   Automatic bidirectional conflict resolution.
-   Hosted/cloud synchronization service.
-   Real-time change data capture or continuous monitoring.
-   Multi-user collaboration.
-   Automatic scheduled synchronization.
-   Arbitrary cross-database-engine migration.
-   Guaranteed rollback of every DDL operation.

## 4. Personas

-   **Developer:** compares development, test and production database
    structures.
-   **DBA:** investigates differences and controls approved deployments.
-   **Technical lead:** reviews proposed changes and synchronization
    reports.

## 5. Primary user journeys

### 5.1 Connect databases

1.  User creates or selects a connection profile.
2.  User enters server, database and authentication settings.
3.  Application tests connectivity.
4.  User saves the profile; secrets are stored only in OS-native secure
    storage or an approved credential provider.

### 5.2 Compare databases

1.  User selects Source and Target.
2.  Application displays server/database identities and warns if the
    same database is selected.
3.  User selects schema comparison, data comparison or both.
4.  User configures object/table filters and comparison options.
5.  Application reads metadata and data without modifying either
    database.
6.  Application displays summary counts and detailed differences.
7.  User can cancel the operation.

### 5.3 Plan synchronization

1.  User chooses direction: Source → Target or Target → Source. The
    application shows which database is the write target.
2.  User selects differences to include.
3.  Application creates a dependency-aware plan.
4.  Application validates compatibility, permissions, dependencies and
    write-target state.
5.  Application classifies risk and displays generated SQL/operations.
6.  User explicitly approves the plan, confirming the write target.

### 5.4 Execute and verify

1.  Application revalidates write-target identity and plan freshness.
2.  Application executes approved operations with explicit transaction
    boundaries.
3.  Application records operation-level outcomes.
4.  Application verifies expected changes.
5.  Application displays success, partial success or failure and offers
    a report.

## 6. Functional requirements

### 6.1 Connection management

-   **FR-CON-001:** Support SQL Server connections using
    Microsoft.Data.SqlClient.
-   **FR-CON-002:** Support SQL authentication.
-   **FR-CON-003:** Support Windows integrated authentication where the
    runtime/platform supports it.
-   **FR-CON-004:** Support encrypted connections and certificate
    validation.
-   **FR-CON-005:** Test connections and retrieve server/database
    metadata.
-   **FR-CON-006:** Save, edit, rename and delete connection profiles.
-   **FR-CON-007:** Do not persist plaintext passwords in SQLite, JSON
    or logs.
-   **FR-CON-008:** Display the resolved server, database and server
    version identity before comparison and execution.
-   **FR-CON-009:** When OS-native secure storage is unavailable, do not
    save the secret; prompt for it per session and tell the user why. No
    plaintext or obfuscated fallback.

### 6.2 Schema comparison

-   **FR-SCH-001:** Compare schemas and tables.
-   **FR-SCH-002:** Compare columns, SQL types, length, precision,
    scale, nullability, collation, identity, computed definitions and
    ordinal.
-   **FR-SCH-003:** Compare primary keys and ordered key columns.
-   **FR-SCH-004:** Compare foreign keys, referenced columns and
    referential actions.
-   **FR-SCH-005:** Compare indexes, key order, included columns,
    uniqueness, filters and supported options.
-   **FR-SCH-006:** Compare unique, check and default constraints.
-   **FR-SCH-007:** Compare views, stored procedures, functions and
    triggers.
-   **FR-SCH-008:** Compare sequences. Further object types are
    supported only once listed here (see §10).
-   **FR-SCH-009:** Detect source-only, target-only, modified, equal and
    incompatible objects.
-   **FR-SCH-010:** Report unsupported or unreadable objects explicitly,
    including module definitions hidden by missing `VIEW DEFINITION`
    permission or `WITH ENCRYPTION`.
-   **FR-SCH-011:** Normalize metadata without corrupting SQL literals,
    comments or semantics.
-   **FR-SCH-012:** Provide object/property-level difference details.

### 6.3 Data comparison

-   **FR-DAT-001:** Compare selected tables.
-   **FR-DAT-002:** Discover primary keys and unique constraints.
-   **FR-DAT-003:** Allow user-selected unique matching keys when no
    suitable key is discovered. Uniqueness of a user-selected key is
    verified during comparison; duplicates stop row matching for that
    table and are reported.
-   **FR-DAT-004:** Support composite keys.
-   **FR-DAT-005:** Detect source-only, target-only and modified rows.
-   **FR-DAT-006:** Show changed columns for modified rows.
-   **FR-DAT-007:** Compare typed values, including NULL, decimal,
    temporal, Unicode and binary values.
-   **FR-DAT-008:** Stream rows and process bounded batches.
-   **FR-DAT-009:** Use keyset pagination or ordered streaming; do not
    use deep OFFSET pagination as the default.
-   **FR-DAT-010:** Support row fingerprints as an optional optimization
    with collision-safe verification.
-   **FR-DAT-011:** Support configurable batch size and bounded
    parallelism.
-   **FR-DAT-012:** Report tables that cannot be safely matched
    automatically.
-   **FR-DAT-013:** Provide filters and incremental/virtualized result
    presentation.
-   **FR-DAT-014:** Define and communicate consistency/isolation
    behavior when databases are concurrently modified.
-   **FR-DAT-015:** Match columns by name. Columns present on one side
    only, or with types that cannot be compared safely, are reported and
    excluded from row comparison rather than coerced.
-   **FR-DAT-016:** The key order used to merge the two row streams must
    be identical to the order in which each server returned the rows,
    including for collation-sensitive string keys and when source and
    target collations differ.

### 6.4 Synchronization planning

-   **FR-PLN-001:** Support Source → Target and Target → Source.
-   **FR-PLN-002:** Generate a plan without executing it.
-   **FR-PLN-003:** Support create, alter and optional drop schema
    operations.
-   **FR-PLN-004:** Support insert, update and optional delete data
    operations.
-   **FR-PLN-005:** Build dependency-aware execution order.
-   **FR-PLN-006:** Detect cycles and unsupported dependency cases.
-   **FR-PLN-007:** Classify operation risk.
-   **FR-PLN-008:** Validate write-target identity, schema fingerprint,
    permissions and constraints.
-   **FR-PLN-009:** Display generated SQL and operation details.
-   **FR-PLN-010:** Require explicit approval for destructive
    operations.
-   **FR-PLN-011:** Make approved plans immutable.
-   **FR-PLN-012:** Invalidate/rebuild plans when relevant write-target
    state changes.
-   **FR-PLN-013:** Export the generated SQL script for review.
-   **FR-PLN-014:** Warn about side effects the plan cannot control,
    such as triggers on the write target that will fire during data
    operations. Never disable constraints or triggers automatically.

### 6.5 Synchronization execution

-   **FR-EXE-001:** Execute only approved plans.
-   **FR-EXE-002:** Revalidate write-target identity and plan freshness
    immediately before execution.
-   **FR-EXE-003:** Use parameterized DML and safely quoted identifiers.
-   **FR-EXE-004:** Use explicit, documented transaction boundaries.
-   **FR-EXE-005:** Support bounded batches and suitable bulk insert
    strategies.
-   **FR-EXE-006:** Record operation-level status and affected-row
    counts.
-   **FR-EXE-007:** Support cancellation at safe boundaries.
-   **FR-EXE-008:** Report partial execution accurately.
-   **FR-EXE-009:** Verify results after execution.
-   **FR-EXE-010:** Never promise rollback where it cannot be
    guaranteed.

### 6.6 History and reports

-   **FR-HIS-001:** Persist connection metadata, comparison summaries
    and execution manifests locally.
-   **FR-HIS-002:** Allow users to reopen summary/history records.
-   **FR-HIS-003:** Export reports to CSV and HTML.
-   **FR-HIS-004:** Avoid persisting detailed row values by default.
-   **FR-HIS-005:** Provide configurable history retention.

### 6.7 Desktop experience

-   **FR-UI-001:** Provide Dashboard, Connections, New Comparison,
    Schema Comparison, Data Comparison, Synchronization Preview, SQL
    Preview, Execution Monitor, History and Settings.
-   **FR-UI-002:** Keep UI responsive during all database operations.
-   **FR-UI-003:** Display progress and cancellation.
-   **FR-UI-004:** Use virtualized grids and incremental loading.
-   **FR-UI-005:** Support light and dark themes, following the OS
    setting by default.
-   **FR-UI-006:** Display source, target and write-target identity
    prominently.
-   **FR-UI-007:** Meet WCAG 2.2 AA contrast, visible focus and
    keyboard operation in both themes; risk and difference states must
    not rely on color alone.

Visual design rules live in `DESIGN.md`; product context in
`PRODUCT.md`.

## 7. Non-functional requirements

-   **NFR-PER-001:** Memory use must be bounded by configured
    batch/concurrency settings, not total table row count.
-   **NFR-PER-002:** Large result sets must not be materialized in UI
    collections.
-   **NFR-PER-003:** Long-running operations must support cancellation.
-   **NFR-PER-004:** Comparison and execution must expose progress.
-   **NFR-SEC-001:** Credentials must be stored using OS-native secure
    storage.
-   **NFR-SEC-002:** SQL values must be parameterized; identifiers must
    be safely quoted.
-   **NFR-SEC-003:** Secrets and row contents must not be logged by
    default.
-   **NFR-REL-001:** Errors must retain diagnostic context and inner
    exceptions.
-   **NFR-REL-002:** Interrupted execution must leave an inspectable
    manifest.
-   **NFR-PORT-001:** Build and package for Windows, Linux and macOS.
-   **NFR-MNT-001:** Core and Application must remain
    provider-independent.
-   **NFR-TEST-001:** Algorithms require unit tests; SQL behavior
    requires integration tests.
-   **NFR-TEST-002:** Performance-sensitive paths require reproducible
    benchmarks.

## 8. Acceptance criteria

-   AC-01: Connect to two SQL Server databases.
-   AC-02: Persist connection profiles without plaintext credentials.
-   AC-03: Detect supported table, column, key, index and constraint
    differences.
-   AC-04: Detect supported programmable-object differences.
-   AC-05: Detect missing and modified rows using primary/composite
    keys.
-   AC-06: Correctly compare NULL, decimal, temporal, Unicode and binary
    values.
-   AC-07: Compare large tables through streaming and bounded batches.
-   AC-08: Generate schema and data synchronization plans without
    executing them.
-   AC-09: Require approval before execution and explicit opt-in for
    DROP/DELETE.
-   AC-10: Revalidate write-target state before execution.
-   AC-11: Record partial outcomes and verify completed operations.
-   AC-12: Cancel long-running comparisons and executions safely.
-   AC-13: Run on Windows, Linux and macOS.
-   AC-14: UI remains responsive and uses virtualized/incremental
    results.
-   AC-15: Integration tests cover constraints, identity columns, errors
    and interrupted execution.
-   AC-16: Data comparison gives correct results for string keys under
    case-insensitive and differing collations.

## 9. Release scope

### Version 1.0

SQL Server-to-SQL Server schema and data comparison, reviewable
synchronization plans, controlled execution, verification, history and
cross-platform desktop deployment.

### Deferred

Other database engines, automatic bidirectional conflict resolution,
cloud services, scheduled synchronization and collaboration.

## 10. Open product decisions

These must be decided (and recorded here or in an ADR) before the phase
noted; until then they are out of scope.

| Decision | Needed by |
|----------|-----------|
| Supported SQL Server versions and editions, and whether Azure SQL Database / Managed Instance are supported | Phase 2 |
| Microsoft Entra ID authentication (needed for Azure SQL) | Phase 2 |
| Object types beyond §6.2: synonyms, user-defined types and table types, partition schemes/functions, temporal and memory-optimized tables, full-text objects, users/roles/permissions | Phase 3 |
| Environment labels on profiles (e.g. Production) and whether they change approval requirements | Phase 5 |
| How data operations are materialized in a plan without holding all rows (see architecture §16) | Phase 5 |

## 11. Revision history

-   **Revision 2 (2026-11-14):** added terminology (write target,
    destructive operation); FR-CON-009, FR-DAT-015/016, FR-PLN-013/014,
    FR-UI-007, AC-16; clarified FR-CON-008, FR-SCH-002/008/010,
    FR-DAT-003; added open decisions.
-   **Revision 1:** approved baseline.
