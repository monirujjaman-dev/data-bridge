using DataBridge.Core.Data;
using DataBridge.Core.Schema;

namespace DataBridge.Core.Comparison;

/// <summary>Where a matching key came from (domain-model §7).</summary>
public enum MatchingKeySource
{
    PrimaryKey,
    UniqueConstraint,
    UniqueIndex,
    UserSelected,
}

/// <summary>The ordered columns rows are matched on. Composite key order is preserved.</summary>
public sealed record MatchingKey(DatabaseObjectId TableId, IReadOnlyList<string> OrderedColumnNames, MatchingKeySource Source);

/// <summary>How one table's data comparison ended.</summary>
public enum TableDataComparisonOutcome
{
    /// <summary>Every row was matched and compared.</summary>
    Compared,

    /// <summary>The table exists on one side only.</summary>
    TableMissing,

    /// <summary>No primary key, unique constraint or unique index can match rows, and none was selected.</summary>
    NoUniqueKey,

    /// <summary>A key value occurs twice on one side; row matching was stopped (FR-DAT-003).</summary>
    DuplicateKeysFound,

    /// <summary>The key columns' types differ between the sides or cannot be ordered safely.</summary>
    IncompatibleKeyTypes,

    /// <summary>
    /// A server returned rows in an order the comparer does not reproduce, so matching was stopped rather than risk
    /// wrong results (FR-DAT-016).
    /// </summary>
    KeyOrderMismatch,

    Cancelled,

    Failed,
}

/// <summary>Whether a read saw one consistent point in time (architecture §9).</summary>
public enum ReadConsistencyMode
{
    /// <summary>One snapshot transaction: the table is read as of a single point in time.</summary>
    Snapshot,

    /// <summary>Committed data only, but rows can change while the table is read.</summary>
    ReadCommitted,
}

/// <summary>A column left out of row comparison, and why (FR-DAT-015).</summary>
public sealed record ExcludedColumn(string ColumnName, string Reason);

/// <summary>
/// The outcome of comparing one table's rows. Holds counts, never row values, so it is safe to keep in history.
/// </summary>
/// <param name="TableId">The table.</param>
/// <param name="Key">The key rows were matched on, when one was found.</param>
/// <param name="Outcome">How the comparison ended.</param>
/// <param name="EqualRowCount">Rows equal on both sides.</param>
/// <param name="SourceOnlyRowCount">Keys found only in the source.</param>
/// <param name="TargetOnlyRowCount">Keys found only in the target.</param>
/// <param name="ModifiedRowCount">Keys on both sides with at least one differing compared column.</param>
/// <param name="ExcludedColumns">Columns not compared, with reasons.</param>
/// <param name="Reason">Why the outcome is not <see cref="TableDataComparisonOutcome.Compared"/>; never contains row values.</param>
/// <param name="SourceConsistency">The isolation achieved when reading the source.</param>
/// <param name="TargetConsistency">The isolation achieved when reading the target.</param>
public sealed record TableDataComparisonSummary(
    DatabaseObjectId TableId,
    MatchingKey? Key,
    TableDataComparisonOutcome Outcome,
    long EqualRowCount,
    long SourceOnlyRowCount,
    long TargetOnlyRowCount,
    long ModifiedRowCount,
    IReadOnlyList<ExcludedColumn> ExcludedColumns,
    string? Reason = null,
    ReadConsistencyMode? SourceConsistency = null,
    ReadConsistencyMode? TargetConsistency = null)
{
    public long DifferenceCount => SourceOnlyRowCount + TargetOnlyRowCount + ModifiedRowCount;

    public static TableDataComparisonSummary NotCompared(
        DatabaseObjectId tableId,
        TableDataComparisonOutcome outcome,
        string reason,
        MatchingKey? key = null,
        IReadOnlyList<ExcludedColumn>? excludedColumns = null) =>
        new(tableId, key, outcome, 0, 0, 0, 0, excludedColumns ?? [], reason);
}

/// <summary>One column of a differing row.</summary>
/// <param name="ColumnName">The column.</param>
/// <param name="SourceValue">The source value; <see langword="null"/> for a target-only row.</param>
/// <param name="TargetValue">The target value; <see langword="null"/> for a source-only row.</param>
/// <param name="Status">Modified for a changed column of a modified row; SourceOnly or TargetOnly for a one-sided row.</param>
public sealed record ColumnDifference(
    string ColumnName,
    DatabaseValue? SourceValue,
    DatabaseValue? TargetValue,
    DifferenceStatus Status);

/// <summary>
/// One row that differs. Holds row values, so it is streamed to the UI and never logged or persisted by default.
/// </summary>
/// <param name="TableId">The table.</param>
/// <param name="KeyValues">The key, ordered as <see cref="MatchingKey.OrderedColumnNames"/>.</param>
/// <param name="Status">SourceOnly, TargetOnly or Modified.</param>
/// <param name="Columns">For a modified row, the changed columns; for a one-sided row, every compared column.</param>
/// <param name="SourceFingerprint">The <see cref="RowFingerprint"/> of the source values, to detect drift before synchronization.</param>
/// <param name="TargetFingerprint">The fingerprint of the target values.</param>
public sealed record RowDifference(
    DatabaseObjectId TableId,
    IReadOnlyList<DatabaseValue> KeyValues,
    DifferenceStatus Status,
    IReadOnlyList<ColumnDifference> Columns,
    string? SourceFingerprint,
    string? TargetFingerprint);
