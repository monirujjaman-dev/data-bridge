# DataBridge --- Domain Model

**Status:** Approved baseline (revision 5, 2026-11-15)

The C# shapes below are illustrative contracts, not final code. Names,
fields and invariants are binding; exact signatures may be refined in
implementation with tests.

## 1. Design principles

-   Domain types describe database concepts, not SqlClient or DacFx
    implementation details.
-   Use `Guid` identifiers for sessions, plans and operations.
-   Use `DateTimeOffset` with UTC-suffixed property names.
-   Preserve SQL Server type information.
-   Prefer immutable records for comparison results and plans.
-   Model unsupported or ambiguous states explicitly.
-   Use the terminology in `requirements.md` §1 (source, target, write
    target, destructive operation).

## 2. Bounded contexts

1.  Connections: profiles and connection identity.
2.  Schema: database objects and definitions.
3.  Comparison: sessions, options and differences.
4.  Synchronization: plans, operations, validation and execution.
5.  Reporting/history: summaries and manifests.

## 3. Connections

``` csharp
public enum DatabaseProviderType
{
    SqlServer
}

public enum AuthenticationType
{
    SqlAuthentication,
    Integrated
}

public sealed record DatabaseConnection(
    Guid Id,
    string Name,
    DatabaseProviderType Provider,
    string Server,
    string DatabaseName,
    AuthenticationType AuthenticationType,
    string? UserName,                 // SQL authentication only; not a secret
    string? CredentialReference,      // key into the OS credential store; never the secret
    ConnectionSettings Settings,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ConnectionSettings(
    bool Encrypt,                     // default true
    bool TrustServerCertificate,      // default false; explicit per-profile opt-in
    int ConnectTimeoutSeconds,
    string? ApplicationName);

public sealed record ResolvedDatabaseIdentity(
    DatabaseProviderType Provider,
    string ServerName,                // as reported by the server, e.g. @@SERVERNAME
    string DatabaseName,
    int? DatabaseId,
    Guid? DatabaseGuid,               // e.g. SQL Server service_broker_guid
    DateTime? DatabaseCreateDate,     // as the server reports it (server local time); identifying, not UTC
    string ServerVersion,
    string? DatabaseCollation,
    string IdentityFingerprint);      // stable hash of the identifying fields; a match is not proof (§11)
```

Secrets are referenced through `CredentialReference`, never held in
serializable profile objects or logs.

`ResolvedDatabaseIdentity` is what the server reports after connecting,
not what the profile says. It drives the same-database warning, the
route sign in the UI and write-target revalidation.

`DatabaseConnection` also has an `EnvironmentLabel Environment`
(`None`, `Development`, `Test`, `Staging`, `Production`), chosen by the
user. A plan records its write target's label; a Production write
target requires typing the database name to approve (ADR-0015).

## 4. Schema model

``` csharp
public sealed record DatabaseSchema(
    string DatabaseName,
    IReadOnlyList<SchemaDefinition> Schemas,
    IReadOnlyList<UnsupportedObject> UnsupportedObjects,
    bool IdentifiersAreCaseSensitive, // from the database collation
    bool HasCompleteMetadata,         // false without VIEW DEFINITION: objects may be hidden
    DateTimeOffset CapturedAtUtc);

public sealed record SchemaDefinition(
    string Name,
    IReadOnlyList<TableDefinition> Tables,
    IReadOnlyList<ViewDefinition> Views,
    IReadOnlyList<ProgrammableObjectDefinition> ProgrammableObjects,
    IReadOnlyList<SequenceDefinition> Sequences);

public sealed record TableDefinition(
    DatabaseObjectId Id,
    IReadOnlyList<ColumnDefinition> Columns,
    PrimaryKeyDefinition? PrimaryKey,
    IReadOnlyList<UniqueConstraintDefinition> UniqueConstraints,
    IReadOnlyList<ForeignKeyDefinition> ForeignKeys,
    IReadOnlyList<CheckConstraintDefinition> CheckConstraints,
    IReadOnlyList<DefaultConstraintDefinition> DefaultConstraints,
    IReadOnlyList<IndexDefinition> Indexes);   // excludes indexes backing PK/unique constraints

public sealed record ColumnDefinition(
    string Name,
    string SqlTypeName,      // alias types schema-qualified
    int? MaxLength,          // in characters for (n)char/(n)varchar, bytes for binary; null = MAX or n/a
    bool IsMaxLength,
    byte? Precision,         // exact numerics only
    byte? Scale,             // exact numerics and fractional-second temporal types only
    bool IsNullable,
    IdentitySpecification? Identity,   // BigInteger seed and increment (up to 38 digits)
    ComputedSpecification? Computed,   // expression and persisted flag
    string? CollationName,
    int OrdinalPosition,     // 1-based, gaps from dropped columns removed
    ColumnOptions? Options); // sparse, column set, ROWGUIDCOL, FILESTREAM, XML schema, masking,
                             // Always Encrypted, generated-always, hidden

public sealed record UnsupportedObject(
    string SchemaName,
    string ObjectName,
    string ObjectTypeDescription,
    string Reason);          // e.g. unsupported type, encrypted, no VIEW DEFINITION
```

Additional types:

-   `ViewDefinition` (module and indexes) and
    `ProgrammableObjectDefinition` (procedures, functions, DML triggers
    with `TriggerOptions`), both holding a `ModuleDefinition`: text
    (null when encrypted or hidden), encryption, schema binding,
    `ANSI_NULLS` and `QUOTED_IDENTIFIER`.
-   `SequenceDefinition` (BigInteger values).
-   `PrimaryKeyDefinition`, `UniqueConstraintDefinition`,
    `ForeignKeyDefinition` (with `ForeignKeyColumnMapping` and
    `ReferentialAction`), `CheckConstraintDefinition`,
    `DefaultConstraintDefinition`; each carries `IsSystemNamed`. PK and
    unique constraints carry their index options; checks and foreign keys
    carry `IsNotTrusted` (`WITH NOCHECK`). Definitions and foreign-key
    references the login cannot see are null and compare as `Unknown`.
-   `IndexDefinition` with `IndexColumnDefinition` and `IndexOptions`.

Column defaults are modeled only as `DefaultConstraintDefinition`
(SQL Server default constraints are named objects), not duplicated on
`ColumnDefinition`.

`IndexColumnDefinition` must preserve key ordinal, sort direction and
included-column status. Foreign keys must preserve ordered column
mappings and referential actions.

Engine-generated constraint names differ in every database. The
comparer pairs two system-named constraints with the same definition
when no constraint of the same name exists, and does not compare their
names. SQL text is compared after a provider `ISqlDefinitionNormalizer`
(FR-SCH-011).

## 5. Object identity

``` csharp
public enum DatabaseObjectType
{
    // Database-scoped
    Schema,

    // Schema-scoped objects
    Table,
    View,
    StoredProcedure,
    ScalarFunction,
    TableValuedFunction,
    Trigger,
    Sequence,

    // Table children
    Column,
    PrimaryKey,
    UniqueConstraint,
    ForeignKey,
    CheckConstraint,
    DefaultConstraint,
    Index
}

public readonly record struct DatabaseObjectId(
    string SchemaName,
    string ObjectName,
    DatabaseObjectType ObjectType,
    string? ParentObjectName = null);
```

`ParentObjectName` is the owning table or view and is required for
table children and DML triggers. Index names are unique only per table,
so an index cannot be identified without it. Synchronization operations
on columns, keys, constraints and indexes target the child identity,
not the table.

Comparison policy for case sensitivity must be
provider/database-collation aware. Do not globally lowercase names.
Names match case-insensitively only when both databases compare
identifiers case-insensitively; otherwise ordinally. The same name with
different schema-scoped object kinds (a view and a table) is
`Incompatible`. An object missing from a side without complete metadata
is `Unknown`, not source- or target-only.

## 6. Typed data values

Do not use `object.ToString()` as a comparison or serialization format.

Define a typed value representation that preserves:

-   SQL type identity.
-   Null state.
-   Exact decimal value/scale semantics.
-   Date/time kind and precision.
-   Unicode text.
-   Binary bytes.
-   GUID.
-   XML and other supported specialized types.

Implemented shape (`Core/Data`):

``` csharp
public enum ValueKind      // the comparison family; columns compare only within one kind
{
    WholeNumber, ExactNumeric, ApproximateNumeric, Boolean, Text, Binary,
    UniqueIdentifier, Date, Time, DateTime, DateTimeOffset, Xml,
    RowVersion,            // never compared: generated by each server
    Unsupported            // excluded and reported (sql_variant, CLR, spatial, Always Encrypted)
}

public sealed class DatabaseValue
{
    public ValueKind Kind { get; }
    public string SqlTypeName { get; }
    public bool IsNull { get; }
    // Created only through typed factories (FromInt64, FromDecimal, FromText, FromBinary, ...),
    // read through typed accessors (AsInt64, AsDecimal, ...). ToString never shows the value.
}

public readonly record struct DecimalValue(BigInteger Unscaled, int Scale);  // exact, up to 38 digits
```

The CLR representation is fixed per kind: `long`, `DecimalValue`,
`double`, `bool`, `string`, a private `byte[]` copy, `Guid`, `DateOnly`,
`TimeSpan`, `DateTime`, `DateTimeOffset`. Equality (`ValueEquality`) is
exact: NULL equals only NULL, decimals compare numerically, text and
binary compare ordinally in full (trailing spaces count), and a
`DateTimeOffset` must match in instant and offset. Key order
(`IKeyOrdering`) is provider-supplied (ADR-0014). `RowFingerprint` is a
canonical, length-prefixed, NULL-aware SHA-256 of a row's values.

## 7. Comparison

``` csharp
public enum DifferenceStatus
{
    Equal,
    SourceOnly,
    TargetOnly,
    Modified,
    Incompatible,
    Unknown
}

public enum ComparisonCategory
{
    Schema,
    Data
}

public enum ComparisonStatus
{
    Pending,
    Connecting,
    ReadingMetadata,
    ComparingSchema,
    ComparingData,
    Completed,
    Cancelled,
    Failed
}

// Schema differences
public sealed record SchemaObjectDifference(
    DatabaseObjectId ObjectId,
    DifferenceStatus Status,
    IReadOnlyList<PropertyDifference> Properties,
    string? Reason);                 // why Incompatible/Unknown

public sealed record PropertyDifference(
    string PropertyName,             // e.g. "Nullability", "IndexKeyColumns"
    string? SourceValue,             // normalized metadata text, never row data
    string? TargetValue);

// Data differences
public enum MatchingKeySource
{
    PrimaryKey,
    UniqueConstraint,
    UniqueIndex,
    UserSelected
}

public sealed record MatchingKey(
    DatabaseObjectId TableId,
    IReadOnlyList<string> OrderedColumnNames,
    MatchingKeySource Source);

public enum TableDataComparisonOutcome
{
    Compared,
    TableMissing,          // the table exists on one side only
    NoUniqueKey,
    DuplicateKeysFound,
    IncompatibleKeyTypes,  // also nullable or unorderable key columns
    KeyOrderMismatch,      // a server's order disagreed with the comparer (ADR-0014)
    Cancelled,
    Failed
}

public sealed record ExcludedColumn(string ColumnName, string Reason);

public sealed record TableDataComparisonSummary(
    DatabaseObjectId TableId,
    MatchingKey? Key,
    TableDataComparisonOutcome Outcome,
    long EqualRowCount,
    long SourceOnlyRowCount,
    long TargetOnlyRowCount,
    long ModifiedRowCount,
    IReadOnlyList<ExcludedColumn> ExcludedColumns,
    string? Reason,                              // never contains row values
    ReadConsistencyMode? SourceConsistency,      // achieved per table and side
    ReadConsistencyMode? TargetConsistency);

public sealed record ColumnDifference(
    string ColumnName,
    DatabaseValue? SourceValue,
    DatabaseValue? TargetValue,
    DifferenceStatus Status);

public sealed record RowDifference(
    DatabaseObjectId TableId,
    IReadOnlyList<DatabaseValue> KeyValues,   // ordered as MatchingKey.OrderedColumnNames
    DifferenceStatus Status,
    IReadOnlyList<ColumnDifference> Columns,  // changed columns, or every compared column of a one-sided row
    string? SourceFingerprint,                // RowFingerprint, to detect drift before synchronization
    string? TargetFingerprint);
```

Use these specialized models rather than one untyped catch-all
difference. `RowDifference` holds row values, so it is streamed to the
UI and not persisted by default; `TableDataComparisonSummary` is what
history stores.

## 8. Comparison session and options

``` csharp
public enum ReadConsistencyMode
{
    Snapshot,
    ReadCommitted
}

public sealed record ComparisonSession(
    Guid Id,
    Guid SourceConnectionId,
    Guid TargetConnectionId,
    ResolvedDatabaseIdentity? SourceIdentity,
    ResolvedDatabaseIdentity? TargetIdentity,
    ComparisonStatus Status,
    ComparisonOptions Options,
    ReadConsistencyMode? SourceConsistencyAchieved,
    ReadConsistencyMode? TargetConsistencyAchieved,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record ComparisonOptions(
    bool CompareSchema,
    bool CompareData,
    int BatchSize,
    int MaxDegreeOfParallelism,
    bool UseRowFingerprints,
    bool IncludeBinaryColumns,
    ReadConsistencyMode PreferredConsistency,
    IReadOnlySet<string> IncludedSchemas,
    IReadOnlySet<DatabaseObjectId> ExcludedObjects,
    IReadOnlyList<MatchingKey> MatchingKeyOverrides);
```

Validate options at the Application boundary. Empty filters must have
documented meaning. Whether deletes are allowed is a synchronization
choice, not a comparison option (§9).

As implemented in Phase 4, data comparison takes
`DataComparisonRequest` and `DataComparisonOptions` (Application):
selected tables (empty means every table on either side), user-selected
keys per table, `BatchSize` (1–100 000), `MaxDegreeOfParallelism`
(1–8 tables at once), `IncludeBinaryColumns` and `PreferredConsistency`.
Consistency is recorded per table and side on
`TableDataComparisonSummary`, because each table has its own read
session (ADR-0014). Row fingerprints are always computed for differing
rows, so `UseRowFingerprints` is not an option. Sessions are not
persisted yet (history is Phase 7).

## 9. Synchronization

``` csharp
public enum SynchronizationDirection
{
    SourceToTarget,
    TargetToSource
}

public enum SynchronizationOperationType
{
    CreateObject,
    AlterObject,
    DropObject,
    InsertRows,
    UpdateRows,
    DeleteRows
}

public enum OperationRisk
{
    Low,
    Medium,
    High,
    Critical
}

public enum PlanStatus
{
    Draft,
    Validated,
    ValidationFailed,
    Approved,
    Invalidated,          // write target changed or plan went stale
    Executing,
    Executed,
    PartiallyExecuted,
    Failed,
    Cancelled
}

public sealed record SynchronizationPlanOptions(
    bool AllowDestructiveOperations = false,
    int BatchSize = 1000,
    long LargeRowCountThreshold = 10_000);   // more rows than this: at least High risk

// A sealed class, not a record: there is no way to change a plan after Create.
public sealed class SynchronizationPlan
{
    Guid Id;
    Guid ComparisonSessionId;
    SynchronizationDirection Direction;
    Guid WriteTargetConnectionId;
    string WriteTargetDatabaseName;
    EnvironmentLabel WriteTargetEnvironment;
    string WriteTargetIdentityFingerprint;
    string WriteTargetSchemaFingerprint;      // SchemaFingerprint of the write target as compared
    SynchronizationPlanOptions Options;
    IReadOnlyList<SynchronizationOperation> Operations;
    IReadOnlyList<PlanIssue> NotIncluded;     // selected differences left out, with the reason
    DateTimeOffset CreatedAtUtc;
    string ContentHash;                       // over every field above and every operation
}

public sealed record SynchronizationOperation(
    Guid Id,
    int Order,                        // topological position
    DatabaseObjectId ObjectId,
    SynchronizationOperationType Type,
    OperationRisk Risk,
    bool IsDestructive,
    IReadOnlyList<Guid> DependsOn,    // all earlier in Order
    string Description,
    IReadOnlyList<string> SqlBatches, // DML: parameterized template, never row values
    long? EstimatedAffectedRows,
    IReadOnlyList<string> Warnings,   // e.g. triggers that will fire
    RowChangeSet? Rows)               // data operations only (ADR-0016)
{
    bool RequiresExplicitApproval => IsDestructive || Risk >= OperationRisk.High;
}

public sealed record RowChangeSet(
    string StoreId,                   // local change set file
    DifferenceStatus Status,          // which rows of the stored table
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string> Columns,    // columns written
    long RowCount,
    string ContentHash);              // SHA-256 over the stored keys and fingerprints

public sealed record PlanApproval(
    Guid PlanId,
    string ApprovedContentHash,
    string ConfirmedWriteTargetIdentityFingerprint,
    IReadOnlyList<Guid> ApprovedOperationIds,   // every High, Critical and destructive operation
    DateTimeOffset ApprovedAtUtc);
```

The plan's content never changes after creation; any edit produces a
new plan with a new ID. `PlanStatus` is lifecycle state held alongside
the plan (in persistence and the manifest), not inside its content.
Execution progress belongs in the execution manifest, not in the plan.

A plan is executable only when a `PlanApproval` exists whose
`ApprovedContentHash` equals the plan's `ContentHash` and whose
confirmed write-target fingerprint matches the write target resolved
immediately before execution.

Approval (`PlanApprovalPolicy`) also requires a non-empty plan, the
write target confirmed, and, for a Production write target, the
database name typed exactly. Data operations reference the rows they
change through a `RowChangeSet` recorded during comparison, never
holding the rows (ADR-0016).

## 10. Execution manifest

``` csharp
public enum OperationExecutionStatus
{
    Pending,
    Executing,
    Succeeded,
    Failed,
    Skipped,
    Cancelled
}

public enum VerificationOutcome
{
    NotVerified,
    Verified,
    Mismatch,
    VerificationFailed
}
```

Track:

-   Plan ID, approval reference and write-target identity.
-   Start/completion timestamps.
-   Operation ID and order.
-   Attempt number.
-   `OperationExecutionStatus` and affected row count.
-   Duration.
-   Redacted error details.
-   `VerificationOutcome`.
-   Cancellation/partial-completion reason.

## 11. Domain invariants

-   Source and target cannot resolve to the same database identity for a
    normal synchronization.
-   Data matching requires a unique key; a user-selected key is verified
    unique during comparison.
-   Composite key order is preserved.
-   An approved plan cannot be edited.
-   An approval applies only to the plan content hash it was given for.
-   Destructive operations require explicit approval and
    `AllowDestructiveOperations`.
-   An operation cannot execute until dependencies are satisfied.
-   A stale or changed write target invalidates execution approval.
-   Operations in a plan modify only its write target.
-   Unsupported data/object types are reported, never silently ignored.
-   Fingerprints are not treated as mathematical proof of equality.
-   Comparison results are observations, not locks on either database.

## 12. Revision history

-   **Revision 5 (2026-11-15):** environment labels (ADR-0015); plans
    and operations as implemented in Phase 5: plan as an immutable
    class with the write target's name and label and the differences
    not included, SQL as batches, `RowChangeSet` for data operations
    (ADR-0016), approvals of every High, Critical and destructive
    operation, and the large row count threshold.
-   **Revision 4 (2026-11-15):** typed data values, data comparison
    outcomes, summaries with reasons and consistency, row fingerprints
    and options as implemented in Phase 4 (ADR-0014).
-   **Revision 3 (2026-11-15):** schema model as implemented in Phase 3:
    `Schema` object type, case sensitivity and metadata completeness on
    `DatabaseSchema`, constraint lists split by kind with
    `IsSystemNamed`, identity and computed specifications, BigInteger
    identity and sequence values, `ModuleDefinition`; matching rules for
    system names, incompatible kinds and partial visibility.
-   **Revision 2 (2026-11-14):** added `UserName`, `CredentialReference`
    and `ResolvedDatabaseIdentity`; sequences and unsupported objects in
    the schema model; child object identity with `ParentObjectName`;
    typed schema/data difference models and `MatchingKey`; consistency
    tracking on sessions; moved delete opt-in from comparison options to
    plan options; split plan lifecycle (`PlanStatus`), content
    (`ContentHash`) and approval (`PlanApproval`); manifest status and
    verification enums. Removed `ColumnDefinition.DefaultExpression` and
    the plan-embedded `OperationStatus`.
-   **Revision 1:** approved baseline.
