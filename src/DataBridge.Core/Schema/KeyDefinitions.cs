namespace DataBridge.Core.Schema;

/// <summary>
/// One column of an index or key. Key columns keep their order and direction; included columns have no order
/// (<see cref="KeyOrdinal"/> is 0).
/// </summary>
public sealed record IndexColumnDefinition(string ColumnName, int KeyOrdinal, bool IsDescending, bool IsIncluded);

/// <summary>An index that does not back a primary key or unique constraint (those are modeled as constraints).</summary>
/// <param name="Name">Unique only within the table or view.</param>
/// <param name="IndexType">The engine's index type, for example <c>NONCLUSTERED</c>.</param>
/// <param name="IsUnique">Whether the index enforces uniqueness.</param>
/// <param name="Columns">Key columns in key order, then included columns.</param>
/// <param name="FilterDefinition">The filter predicate, or <see langword="null"/>.</param>
/// <param name="Options">Options that change behavior or storage.</param>
public sealed record IndexDefinition(
    string Name,
    string IndexType,
    bool IsUnique,
    IReadOnlyList<IndexColumnDefinition> Columns,
    string? FilterDefinition,
    IndexOptions Options);

/// <summary>Index options compared by schema comparison (FR-SCH-005).</summary>
/// <param name="IgnoreDuplicateKey">Whether duplicate inserts are ignored with a warning.</param>
/// <param name="FillFactor">1 to 100; the engine's 0 (default) is normalized to 100, which means the same.</param>
/// <param name="IsPadded">Whether the fill factor also applies to intermediate pages.</param>
/// <param name="AllowRowLocks">Whether row locks are allowed.</param>
/// <param name="AllowPageLocks">Whether page locks are allowed.</param>
/// <param name="IsDisabled">Whether the index is disabled.</param>
/// <param name="DataCompression">The compression of the first partition, for example <c>NONE</c> or <c>PAGE</c>.</param>
public sealed record IndexOptions(
    bool IgnoreDuplicateKey,
    int FillFactor,
    bool IsPadded,
    bool AllowRowLocks,
    bool AllowPageLocks,
    bool IsDisabled,
    string? DataCompression);

/// <summary>A primary key with its ordered key columns.</summary>
/// <param name="Name">The constraint name.</param>
/// <param name="IsSystemNamed">Whether the engine generated the name; such names differ between databases and are not compared.</param>
/// <param name="IndexType">The backing index type, for example <c>CLUSTERED</c>.</param>
/// <param name="Columns">The key columns in key order.</param>
public sealed record PrimaryKeyDefinition(
    string Name,
    bool IsSystemNamed,
    string IndexType,
    IReadOnlyList<IndexColumnDefinition> Columns);

/// <summary>A unique constraint with its ordered key columns.</summary>
/// <param name="Name">The constraint name.</param>
/// <param name="IsSystemNamed">Whether the engine generated the name.</param>
/// <param name="IndexType">The backing index type, for example <c>NONCLUSTERED</c>.</param>
/// <param name="Columns">The key columns in key order.</param>
public sealed record UniqueConstraintDefinition(
    string Name,
    bool IsSystemNamed,
    string IndexType,
    IReadOnlyList<IndexColumnDefinition> Columns);

/// <summary>What a foreign key does when the referenced row is deleted or its key updated.</summary>
public enum ReferentialAction
{
    NoAction,
    Cascade,
    SetNull,
    SetDefault,
}

/// <summary>One column mapping of a foreign key, in constraint order.</summary>
public sealed record ForeignKeyColumnMapping(string ColumnName, string ReferencedColumnName);

/// <summary>A foreign key with its ordered column mappings and referential actions (FR-SCH-004).</summary>
public sealed record ForeignKeyDefinition(
    string Name,
    bool IsSystemNamed,
    string ReferencedSchemaName,
    string ReferencedTableName,
    IReadOnlyList<ForeignKeyColumnMapping> Columns,
    ReferentialAction OnDelete,
    ReferentialAction OnUpdate,
    bool IsDisabled,
    bool IsNotForReplication);

/// <summary>A check constraint; the expression is as the engine stores it.</summary>
public sealed record CheckConstraintDefinition(
    string Name,
    bool IsSystemNamed,
    string Expression,
    bool IsDisabled,
    bool IsNotForReplication);

/// <summary>A default constraint on one column; the expression is as the engine stores it.</summary>
public sealed record DefaultConstraintDefinition(
    string Name,
    bool IsSystemNamed,
    string ColumnName,
    string Expression);
