# DataBridge --- Domain Model

## 1. Design principles

-   Domain types describe database concepts, not SqlClient or DacFx
    implementation details.
-   Use `Guid` identifiers for sessions, plans and operations.
-   Use `DateTimeOffset` with UTC-suffixed property names.
-   Preserve SQL Server type information.
-   Prefer immutable records for comparison results and approved plans.
-   Model unsupported or ambiguous states explicitly.

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
    ConnectionSettings Settings,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ConnectionSettings(
    bool Encrypt,
    bool TrustServerCertificate,
    int ConnectTimeoutSeconds,
    string? ApplicationName);
```

Secrets are referenced through a credential-store key/identifier, not
held in serializable profile objects or logs.

Resolved identity should include provider, server/instance, database,
database ID where safely available, and relevant environment labels.

## 4. Schema model

``` csharp
public sealed record DatabaseSchema(
    string DatabaseName,
    IReadOnlyList<SchemaDefinition> Schemas,
    DateTimeOffset CapturedAtUtc);

public sealed record SchemaDefinition(
    string Name,
    IReadOnlyList<TableDefinition> Tables,
    IReadOnlyList<ViewDefinition> Views,
    IReadOnlyList<ProgrammableObjectDefinition> ProgrammableObjects);

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
    int? MaxLength,
    byte? Precision,
    byte? Scale,
    bool IsNullable,
    bool IsIdentity,
    decimal? IdentitySeed,
    decimal? IdentityIncrement,
    bool IsComputed,
    string? ComputedExpression,
    string? DefaultExpression,
    string? CollationName,
    int OrdinalPosition);
```

Additional types: - `ViewDefinition` - `ProgrammableObjectDefinition` -
`SequenceDefinition` - `PrimaryKeyDefinition` - `ForeignKeyDefinition` -
`IndexDefinition` - `IndexColumnDefinition` - `ConstraintDefinition` -
`CheckConstraintDefinition` - `DefaultConstraintDefinition`

`IndexColumnDefinition` must preserve key ordinal, sort direction and
included-column status. Foreign keys must preserve ordered column
mappings and referential actions.

## 5. Object identity

``` csharp
public enum DatabaseObjectType
{
    Table,
    View,
    StoredProcedure,
    ScalarFunction,
    TableValuedFunction,
    Trigger,
    Sequence
}

public readonly record struct DatabaseObjectId(
    string SchemaName,
    string ObjectName,
    DatabaseObjectType ObjectType);
```

Comparison policy for case sensitivity must be
provider/database-collation aware. Do not globally lowercase names.

## 6. Typed data values

Do not use `object.ToString()` as a comparison or serialization format.

Define a typed value representation that preserves: - SQL type
identity. - Null state. - Exact decimal value/scale semantics. -
Date/time kind and precision. - Unicode text. - Binary bytes. - GUID. -
XML and other supported specialized types.

Possible shape:

``` csharp
public sealed record DatabaseValue(
    string SqlTypeName,
    bool IsNull,
    object? Value);
```

The implementation must constrain and validate `Value` according to
`SqlTypeName`; a stronger discriminated representation is preferred
where it materially improves safety.

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

public sealed record DatabaseDifference(
    string ObjectName,
    ComparisonCategory Category,
    DifferenceStatus Status,
    string Description);

public sealed record ColumnDifference(
    string TableName,
    string ColumnName,
    DatabaseValue? SourceValue,
    DatabaseValue? TargetValue,
    DifferenceStatus Status);

public sealed record RowDifference(
    string TableName,
    IReadOnlyList<DatabaseValue> KeyValues,
    DifferenceStatus Status,
    IReadOnlyList<ColumnDifference> Columns);
```

Specialized difference models should be used for schema objects and
rows. Avoid one untyped catch-all model for every difference.

## 8. Comparison session and options

``` csharp
public sealed record ComparisonSession(
    Guid Id,
    Guid SourceConnectionId,
    Guid TargetConnectionId,
    ComparisonStatus Status,
    ComparisonOptions Options,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record ComparisonOptions(
    bool CompareSchema,
    bool CompareData,
    int BatchSize,
    int MaxDegreeOfParallelism,
    bool UseRowFingerprints,
    bool IncludeBinaryColumns,
    bool IncludeDeletes,
    IReadOnlySet<string> IncludedSchemas,
    IReadOnlySet<string> ExcludedObjects);
```

Validate options at the Application boundary. Empty filters must have
documented meaning.

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

public enum OperationStatus
{
    Planned,
    Validated,
    AwaitingApproval,
    Approved,
    Executing,
    Executed,
    Verified,
    Failed,
    Skipped,
    Cancelled
}

public sealed record SynchronizationPlan(
    Guid Id,
    Guid ComparisonSessionId,
    SynchronizationDirection Direction,
    string TargetIdentityFingerprint,
    string TargetSchemaFingerprint,
    IReadOnlyList<SynchronizationOperation> Operations,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ApprovedAtUtc);

public sealed record SynchronizationOperation(
    Guid Id,
    DatabaseObjectId ObjectId,
    SynchronizationOperationType Type,
    OperationRisk Risk,
    IReadOnlyList<Guid> DependsOn,
    string Description,
    string? GeneratedSql,
    OperationStatus Status,
    bool RequiresExplicitApproval);
```

Approved plans are immutable. Execution progress belongs in an execution
manifest or event/history record rather than mutating the approved plan
definition.

## 10. Execution manifest

Track: - Plan ID and target identity. - Start/completion timestamps. -
Operation ID and order. - Attempt number. - Status and affected row
count. - Duration. - Redacted error details. - Verification outcome. -
Cancellation/partial-completion reason.

## 11. Domain invariants

-   Source and target cannot resolve to the same database identity for a
    normal synchronization.
-   Data matching requires a unique key.
-   Composite key order is preserved.
-   An approved plan cannot be edited.
-   Destructive operations require explicit approval.
-   An operation cannot execute until dependencies are satisfied.
-   A stale target invalidates execution approval.
-   Unsupported data/object types are reported, never silently ignored.
-   Fingerprints are not treated as mathematical proof of equality.
-   Comparison results are observations, not locks on either database.
