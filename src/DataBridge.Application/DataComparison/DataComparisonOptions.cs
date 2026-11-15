using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Schema;
using DataBridge.Shared.Errors;

namespace DataBridge.Application.DataComparison;

/// <summary>How a data comparison reads and matches rows (FR-DAT-011, FR-DAT-014).</summary>
/// <param name="BatchSize">Rows per batch on each side, 1 to 100 000. Memory grows with it, not with table size.</param>
/// <param name="MaxDegreeOfParallelism">Tables compared at once, 1 to 8; each uses one connection per side.</param>
/// <param name="IncludeBinaryColumns">Whether binary columns are compared.</param>
/// <param name="PreferredConsistency">Snapshot reads when the database already allows them; otherwise read committed.</param>
public sealed record DataComparisonOptions(
    int BatchSize = 1000,
    int MaxDegreeOfParallelism = 2,
    bool IncludeBinaryColumns = true,
    ReadConsistencyMode PreferredConsistency = ReadConsistencyMode.Snapshot)
{
    public const int MaxBatchSize = 100_000;
    public const int MaxParallelism = 8;

    public static DataComparisonOptions Default { get; } = new();

    public IEnumerable<Error> Validate()
    {
        if (BatchSize is < 1 or > MaxBatchSize)
        {
            yield return DataComparisonErrors.BatchSizeOutOfRange;
        }

        if (MaxDegreeOfParallelism is < 1 or > MaxParallelism)
        {
            yield return DataComparisonErrors.ParallelismOutOfRange;
        }
    }
}

/// <summary>Which tables to compare, and keys the user selected for tables without a usable one.</summary>
/// <param name="SourceProfileId">The profile to compare from.</param>
/// <param name="TargetProfileId">The profile to compare with.</param>
/// <param name="Tables">Tables to compare, named as in the source; empty means every table on either side.</param>
/// <param name="UserKeys">Ordered key columns per table, overriding key discovery.</param>
/// <param name="Options">Read and match options.</param>
public sealed record DataComparisonRequest(
    Guid SourceProfileId,
    Guid TargetProfileId,
    IReadOnlyList<DatabaseObjectId> Tables,
    IReadOnlyDictionary<DatabaseObjectId, IReadOnlyList<string>> UserKeys,
    DataComparisonOptions Options);

/// <summary>Progress of a data comparison.</summary>
/// <param name="Table">The table that progressed, or <see langword="null"/> before tables start.</param>
/// <param name="Status">The comparison stage.</param>
/// <param name="TablesCompleted">Tables finished so far.</param>
/// <param name="TablesTotal">Tables to compare.</param>
/// <param name="SourceRowsRead">Rows read from the source for <see cref="Table"/>.</param>
/// <param name="TargetRowsRead">Rows read from the target for <see cref="Table"/>.</param>
public sealed record DataComparisonProgress(
    DatabaseObjectId? Table,
    ComparisonStatus Status,
    int TablesCompleted,
    int TablesTotal,
    long SourceRowsRead,
    long TargetRowsRead);

/// <summary>The outcome of a data comparison: identities and one summary per table. Holds no row values.</summary>
public sealed record DataComparisonReport(
    Guid SessionId,
    ResolvedDatabaseIdentity Source,
    ResolvedDatabaseIdentity Target,
    IReadOnlyList<TableDataComparisonSummary> Tables,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc)
{
    public bool IsSameDatabase => Source.IsSameDatabaseAs(Target);

    /// <summary>Whether any table was read without a snapshot, so results may mix states of concurrently changing data.</summary>
    public bool HasNonSnapshotReads => Tables.Any(table =>
        table.SourceConsistency == ReadConsistencyMode.ReadCommitted || table.TargetConsistency == ReadConsistencyMode.ReadCommitted);

    /// <summary>The source schema the tables were planned from.</summary>
    public DatabaseSchema? SourceSchema { get; init; }

    /// <summary>The target schema the tables were planned from.</summary>
    public DatabaseSchema? TargetSchema { get; init; }
}

/// <summary>A table offered for data comparison, with how it would be matched.</summary>
/// <param name="TableId">The table, named as in the source when it exists there.</param>
/// <param name="Key">The key rows would be matched on, when one was found.</param>
/// <param name="Problem">Why it cannot be compared as it is, when it cannot.</param>
/// <param name="Reason">A user-facing explanation of <see cref="Problem"/>.</param>
/// <param name="CandidateKeyColumns">Columns on both sides that could form a user-selected key.</param>
public sealed record DataComparisonTable(
    DatabaseObjectId TableId,
    MatchingKey? Key,
    TableDataComparisonOutcome? Problem,
    string? Reason,
    IReadOnlyList<string> CandidateKeyColumns);

/// <summary>Data comparison failures. Messages never contain row values.</summary>
public static class DataComparisonErrors
{
    public static readonly Error BatchSizeOutOfRange =
        new("DataComparison.BatchSizeOutOfRange", $"The batch size must be between 1 and {DataComparisonOptions.MaxBatchSize}.");

    public static readonly Error ParallelismOutOfRange =
        new("DataComparison.ParallelismOutOfRange", $"Tables compared at once must be between 1 and {DataComparisonOptions.MaxParallelism}.");

    public static readonly Error NoTables =
        new("DataComparison.NoTables", "Select at least one table to compare.");
}
