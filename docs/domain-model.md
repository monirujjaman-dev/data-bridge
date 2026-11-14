# DataBridge --- Domain Model

**Status:** Approved baseline (revision 2, 2026-11-14)

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
route sign in the UI and write-target revalidation. Environment labels
are an open product decision (`requirements.md` §10).

## 4. Schema model

``` csharp
public sealed record DatabaseSchema(
    string DatabaseName,
    IReadOnlyList<SchemaDefinition> Schemas,
    IReadOnlyList<UnsupportedObject> UnsupportedObjects,
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
    IReadOnlyList<ForeignKeyDefinition> ForeignKeys,
    IReadOnlyList<IndexDefinition> Indexes,
    IReadOnlyList<ConstraintDefinition> Constraints);

public sealed record ColumnDefinition(
    string Name,
    string SqlTypeName,
    int? MaxLength,          // in characters for (n)char/(n)varchar, bytes for binary; null = MAX
    byte? Precision,
    byte? Scale,
    bool IsNullable,
    bool IsIdentity,
    decimal? IdentitySeed,
    decimal? IdentityIncrement,
    bool IsComputed,
    bool IsComputedPersisted,
    string? ComputedExpression,
    string? CollationName,
    int OrdinalPosition);

public sealed record UnsupportedObject(
    string SchemaName,
    string ObjectName,
    string ObjectTypeDescription,
    string Reason);          // e.g. unsupported type, encrypted, no VIEW DEFINITION
```

Additional types:

-   `ViewDefinition`
-   `ProgrammableObjectDefinition`
-   `SequenceDefinition`
-   `PrimaryKeyDefinition`
-   `ForeignKeyDefinition`
-   `IndexDefinition`
-   `IndexColumnDefinition`
-   `ConstraintDefinition`
-   `CheckConstraintDefinition`
-   `DefaultConstraintDefinition`

Column defaults are modeled only as `DefaultConstraintDefinition`
(SQL Server default constraints are named objects), not duplicated on
`ColumnDefinition`.

`IndexColumnDefinition` must preserve key ordinal, sort direction and
included-column status. Foreign keys must preserve ordered column
mappings and referential actions.

## 5. Object identity

``` csharp
public enum DatabaseObjectType
{
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

Possible shape:

``` csharp
public sealed record DatabaseValue(
    string SqlTypeName,
    bool IsNull,
    object? Value);
```

The implementation must constrain and validate `Value` according to
`SqlTypeName`, and `IsNull` implies `Value` is null. A stronger
discriminated representation is preferred where it materially improves
safety.

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
    NoUniqueKey,
    DuplicateKeysFound,
    IncompatibleKeyTypes,
    Cancelled,
    Failed
}

public sealed record TableDataComparisonSummary(
    DatabaseObjectId TableId,
    MatchingKey? Key,
    TableDataComparisonOutcome Outcome,
    long EqualRowCount,
    long SourceOnlyRowCount,
    long TargetOnlyRowCount,
    long ModifiedRowCount,
    IReadOnlyList<string> ExcludedColumns);

public sealed record ColumnDifference(
    string ColumnName,
    DatabaseValue? SourceValue,
    DatabaseValue? TargetValue,
    DifferenceStatus Status);

public sealed record RowDifference(
    DatabaseObjectId TableId,
    IReadOnlyList<DatabaseValue> KeyValues,   // ordered as MatchingKey.OrderedColumnNames
    DifferenceStatus Status,
    IReadOnlyList<ColumnDifference> Columns);
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
    bool AllowDestructiveOperations,  // default false
    int BatchSize);

public sealed record SynchronizationPlan(
    Guid Id,
    Guid ComparisonSessionId,
    SynchronizationDirection Direction,
    Guid WriteTargetConnectionId,
    string WriteTargetIdentityFingerprint,
    string WriteTargetSchemaFingerprint,
    SynchronizationPlanOptions Options,
    IReadOnlyList<SynchronizationOperation> Operations,
    string ContentHash,               // over all fields above and operations
    DateTimeOffset CreatedAtUtc);

public sealed record SynchronizationOperation(
    Guid Id,
    int Order,                        // topological position
    DatabaseObjectId ObjectId,
    SynchronizationOperationType Type,
    OperationRisk Risk,
    bool IsDestructive,
    bool RequiresExplicitApproval,
    IReadOnlyList<Guid> DependsOn,
    string Description,
    string? GeneratedSql,             // DML: parameterized template, never row values
    long? EstimatedAffectedRows,
    IReadOnlyList<string> Warnings);  // e.g. triggers that will fire

public sealed record PlanApproval(
    Guid PlanId,
    string ApprovedContentHash,
    string ConfirmedWriteTargetIdentityFingerprint,
    IReadOnlyList<Guid> ApprovedDestructiveOperationIds,
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

How data operations reference the rows they change without holding
them in memory is an open decision (`architecture.md` §16).

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
