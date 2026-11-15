using DataBridge.Core.Comparison;
using DataBridge.Core.Schema;
using DataBridge.Shared.Errors;

namespace DataBridge.Core.Synchronization;

/// <summary>Which way a plan copies differences (FR-PLN-001). The write target follows from it.</summary>
public enum SynchronizationDirection
{
    /// <summary>Make the target match the source; the target is the write target.</summary>
    SourceToTarget,

    /// <summary>Make the source match the target; the source is the write target.</summary>
    TargetToSource,
}

/// <summary>What an operation does.</summary>
public enum SynchronizationOperationType
{
    CreateObject,
    AlterObject,
    DropObject,
    InsertRows,
    UpdateRows,
    DeleteRows,
}

/// <summary>How much harm an operation can do (architecture §10 risk baseline).</summary>
public enum OperationRisk
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>
/// A plan's lifecycle state. Held next to the plan, never inside its content: changing status never changes the
/// content hash (domain-model §9).
/// </summary>
public enum PlanStatus
{
    Draft,
    Validated,
    ValidationFailed,
    Approved,

    /// <summary>The write target changed or the plan went stale; a new plan is needed.</summary>
    Invalidated,
    Executing,
    Executed,
    PartiallyExecuted,
    Failed,
    Cancelled,
}

/// <summary>Helpers for <see cref="SynchronizationDirection"/>.</summary>
public static class SynchronizationDirections
{
    /// <summary>The side the plan writes to.</summary>
    public static ComparisonSide WriteSide(this SynchronizationDirection direction) =>
        direction == SynchronizationDirection.SourceToTarget ? ComparisonSide.Target : ComparisonSide.Source;

    /// <summary>The side the plan copies from.</summary>
    public static ComparisonSide CopySide(this SynchronizationDirection direction) =>
        direction == SynchronizationDirection.SourceToTarget ? ComparisonSide.Source : ComparisonSide.Target;

    /// <summary>The comparison status of something that exists only on the side being copied from.</summary>
    public static DifferenceStatus OnlyInCopySide(this SynchronizationDirection direction) =>
        direction == SynchronizationDirection.SourceToTarget ? DifferenceStatus.SourceOnly : DifferenceStatus.TargetOnly;

    /// <summary>The comparison status of something that exists only in the write target.</summary>
    public static DifferenceStatus OnlyInWriteTarget(this SynchronizationDirection direction) =>
        direction == SynchronizationDirection.SourceToTarget ? DifferenceStatus.TargetOnly : DifferenceStatus.SourceOnly;
}

/// <summary>Options recorded with a plan and covered by its content hash.</summary>
/// <param name="AllowDestructiveOperations">
/// Whether DROP, DELETE and narrowing or lossy ALTER may be planned at all. Off by default; even when on, each
/// destructive operation still needs its own approval (FR-PLN-010).
/// </param>
/// <param name="BatchSize">Rows per batch when data operations execute, 1 to 100 000.</param>
/// <param name="LargeRowCountThreshold">A data operation affecting more rows than this is at least High risk.</param>
public sealed record SynchronizationPlanOptions(
    bool AllowDestructiveOperations = false,
    int BatchSize = 1000,
    long LargeRowCountThreshold = 10_000)
{
    public const int MaxBatchSize = 100_000;

    public static SynchronizationPlanOptions Default { get; } = new();

    public IEnumerable<Error> Validate()
    {
        if (BatchSize is < 1 or > MaxBatchSize)
        {
            yield return SynchronizationErrors.BatchSizeOutOfRange;
        }

        if (LargeRowCountThreshold < 1)
        {
            yield return SynchronizationErrors.ThresholdOutOfRange;
        }
    }
}

/// <summary>
/// The rows one data operation changes, stored outside the plan because a plan cannot hold every row (product rule
/// 7, ADR-0016). The plan carries the counts and a hash of the stored keys and row fingerprints, so the content
/// hash covers exactly which rows will change and what they looked like when compared.
/// </summary>
/// <param name="StoreId">Where the keys and fingerprints are stored locally.</param>
/// <param name="Status">Which rows of the stored table this operation changes.</param>
/// <param name="KeyColumns">The matching key, in key order, as named in the write target.</param>
/// <param name="Columns">The columns written, as named in the write target (for inserts, the key columns too).</param>
/// <param name="RowCount">How many rows.</param>
/// <param name="ContentHash">SHA-256 over the stored rows' keys and fingerprints, in key order.</param>
public sealed record RowChangeSet(
    string StoreId,
    DifferenceStatus Status,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string> Columns,
    long RowCount,
    string ContentHash);

/// <summary>One step of a plan (domain-model §9). Execution state lives in the execution manifest, not here.</summary>
/// <param name="Id">Identifies the operation in approvals and manifests.</param>
/// <param name="Order">Its position in execution order, from 0.</param>
/// <param name="ObjectId">The object it changes; for data operations, the table.</param>
/// <param name="Type">What it does.</param>
/// <param name="Risk">How much harm it can do.</param>
/// <param name="IsDestructive">Whether it can remove objects, columns or rows, or lose data.</param>
/// <param name="DependsOn">Operations that must complete first; all come earlier in <see cref="Order"/>.</param>
/// <param name="Description">What it does, in words.</param>
/// <param name="SqlBatches">
/// The SQL it runs, one batch per entry. For data operations, a parameterized template: never row values.
/// </param>
/// <param name="EstimatedAffectedRows">For data operations, the rows found by comparison.</param>
/// <param name="Warnings">Side effects the plan cannot control, such as triggers that will fire (FR-PLN-014).</param>
/// <param name="Rows">For data operations, the rows it changes.</param>
public sealed record SynchronizationOperation(
    Guid Id,
    int Order,
    DatabaseObjectId ObjectId,
    SynchronizationOperationType Type,
    OperationRisk Risk,
    bool IsDestructive,
    IReadOnlyList<Guid> DependsOn,
    string Description,
    IReadOnlyList<string> SqlBatches,
    long? EstimatedAffectedRows,
    IReadOnlyList<string> Warnings,
    RowChangeSet? Rows = null)
{
    /// <summary>High and Critical operations, and every destructive one, must be approved one by one.</summary>
    public bool RequiresExplicitApproval => IsDestructive || Risk >= OperationRisk.High;

    public bool IsDataOperation => Type is SynchronizationOperationType.InsertRows
        or SynchronizationOperationType.UpdateRows
        or SynchronizationOperationType.DeleteRows;
}

/// <summary>A selected difference the plan does not include, and why (FR-PLN-006).</summary>
/// <param name="ObjectId">The object or table, when the issue is about one.</param>
/// <param name="Message">Why it is not included and what the user can do.</param>
public sealed record PlanIssue(DatabaseObjectId? ObjectId, string Message);

/// <summary>Synchronization planning and approval failures.</summary>
public static class SynchronizationErrors
{
    public static readonly Error BatchSizeOutOfRange =
        new("Synchronization.BatchSizeOutOfRange", $"The batch size must be between 1 and {SynchronizationPlanOptions.MaxBatchSize}.");

    public static readonly Error ThresholdOutOfRange =
        new("Synchronization.ThresholdOutOfRange", "The large row count threshold must be at least 1.");

    public static readonly Error NothingToSynchronize =
        new("Synchronization.NothingToSynchronize", "None of the selected differences can be synchronized. See the list of differences not included.");

    public static readonly Error WrongPlan =
        new("Synchronization.WrongPlan", "This approval was given for a different plan.");

    public static readonly Error ContentChanged =
        new("Synchronization.ContentChanged", "The plan's content does not match the content that was approved. Create and approve a new plan.");

    public static readonly Error WriteTargetNotConfirmed =
        new("Synchronization.WriteTargetNotConfirmed", "Confirm the database the plan writes to.");

    public static readonly Error WriteTargetChanged =
        new("Synchronization.WriteTargetChanged", "The database the plan writes to is not the one it was planned and approved for.");

    public static readonly Error OperationNotApproved =
        new("Synchronization.OperationNotApproved", "Every high-risk, critical and destructive operation must be approved individually.");

    public static readonly Error DatabaseNameNotTyped =
        new("Synchronization.DatabaseNameNotTyped", "This plan writes to a Production database. Type the database name exactly to approve it.");

    public static readonly Error DestructiveNotAllowed =
        new("Synchronization.DestructiveNotAllowed", "The plan contains destructive operations but was created without allowing them.");
}
