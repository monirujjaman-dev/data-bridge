using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using DataBridge.Application.DataComparison;
using DataBridge.Core.Comparison;
using DataBridge.Core.Data;
using DataBridge.Core.Schema;

namespace DataBridge.Desktop.ViewModels;

/// <summary>A table offered for comparison: whether it is selected, how it would be matched, and a key the user can set.</summary>
public sealed partial class DataTableItem(DataComparisonTable table) : ObservableObject
{
    public DataComparisonTable Table { get; } = table;

    public string Name => Table.TableId.DisplayName;

    /// <summary>The key rows are matched on, or why the table cannot be compared as it is.</summary>
    public string Detail => Table.Key is { } key
        ? $"Matched on {string.Join(", ", key.OrderedColumnNames)} ({Describe(key.Source)})"
        : Table.Reason ?? "";

    public bool NeedsKey => Table.Problem is TableDataComparisonOutcome.NoUniqueKey or TableDataComparisonOutcome.IncompatibleKeyTypes
        && Table.CandidateKeyColumns.Count > 0;

    public bool CanCompare => Table.Problem is null || NeedsKey;

    public string CandidateKeyText => Table.CandidateKeyColumns.Count == 0
        ? ""
        : $"Columns that can form a key: {string.Join(", ", Table.CandidateKeyColumns)}";

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = table.Problem is null;

    /// <summary>Key columns typed by the user, comma-separated and in key order.</summary>
    [ObservableProperty]
    public partial string? UserKey { get; set; }

    public IReadOnlyList<string> UserKeyColumns => (UserKey ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Describe(MatchingKeySource source) => source switch
    {
        MatchingKeySource.PrimaryKey => "primary key",
        MatchingKeySource.UniqueConstraint => "unique constraint",
        MatchingKeySource.UniqueIndex => "unique index",
        _ => "selected key",
    };
}

/// <summary>One table's result row.</summary>
public sealed record TableResultItem(TableDataComparisonSummary Summary, int ShownDifferences)
{
    public string Name => Summary.TableId.DisplayName;

    public string OutcomeText => Summary.Outcome switch
    {
        TableDataComparisonOutcome.Compared => Summary.DifferenceCount == 0 ? "Equal" : "Different",
        TableDataComparisonOutcome.TableMissing => "Only on one side",
        TableDataComparisonOutcome.NoUniqueKey => "No key",
        TableDataComparisonOutcome.DuplicateKeysFound => "Duplicate keys",
        TableDataComparisonOutcome.IncompatibleKeyTypes => "Key not usable",
        TableDataComparisonOutcome.KeyOrderMismatch => "Key order mismatch",
        TableDataComparisonOutcome.Cancelled => "Cancelled",
        _ => "Failed",
    };

    /// <summary>Counts for a compared table, otherwise the reason.</summary>
    public string Detail => Summary.Outcome == TableDataComparisonOutcome.Compared
        ? $"{Summary.SourceOnlyRowCount:N0} only in source · {Summary.TargetOnlyRowCount:N0} only in target · {Summary.ModifiedRowCount:N0} different · {Summary.EqualRowCount:N0} equal"
            + (Summary.ExcludedColumns.Count > 0 ? $" · {Summary.ExcludedColumns.Count} columns not compared" : "")
        : Summary.Reason ?? "";

    public string ConsistencyText => (Summary.SourceConsistency, Summary.TargetConsistency) switch
    {
        (null, null) => "",
        (ReadConsistencyMode.Snapshot, ReadConsistencyMode.Snapshot) => "Snapshot reads",
        _ => "Read committed: rows may have changed while being read",
    };

    public bool IsCleared => Summary.Outcome == TableDataComparisonOutcome.Compared && Summary.DifferenceCount == 0;

    public bool IsChange => Summary.Outcome == TableDataComparisonOutcome.Compared && Summary.DifferenceCount > 0;

    public bool IsBlocked => Summary.Outcome is not (TableDataComparisonOutcome.Compared or TableDataComparisonOutcome.TableMissing);

    public bool IsExcluded => Summary.Outcome == TableDataComparisonOutcome.TableMissing;

    public string ShownText => Summary.DifferenceCount > ShownDifferences
        ? $"Showing the first {ShownDifferences:N0} of {Summary.DifferenceCount:N0} differing rows."
        : "";
}

/// <summary>One differing row, with its key shown as text.</summary>
public sealed record RowDifferenceItem(RowDifference Difference)
{
    public string KeyText => string.Join(", ", Difference.KeyValues.Select(value => value.ToDisplayString()));

    public string StatusText => Difference.Status switch
    {
        DifferenceStatus.SourceOnly => "Only in source",
        DifferenceStatus.TargetOnly => "Only in target",
        _ => "Different",
    };

    public string Detail => Difference.Status == DifferenceStatus.Modified
        ? string.Join(", ", Difference.Columns.Select(column => column.ColumnName))
        : $"{Difference.Columns.Count} columns";

    public bool IsAdd => Difference.Status == DifferenceStatus.SourceOnly;

    public bool IsRemove => Difference.Status == DifferenceStatus.TargetOnly;

    public bool IsChange => Difference.Status == DifferenceStatus.Modified;

    public IReadOnlyList<ColumnValueItem> Columns =>
        [.. Difference.Columns.Select(column => new ColumnValueItem(column.ColumnName, Show(column.SourceValue), Show(column.TargetValue)))];

    private static string Show(DatabaseValue? value) => value?.ToDisplayString() ?? "(no row)";
}

/// <summary>A column of a differing row, shown source then target.</summary>
public sealed record ColumnValueItem(string ColumnName, string SourceValue, string TargetValue);

/// <summary>
/// Keeps the first differing rows of each table for display and counts the rest, so a table with millions of
/// differences never fills memory or the UI (NFR-PER-002). Thread-safe: tables are compared in parallel.
/// </summary>
public sealed class BoundedDifferenceSink(int maxRowsPerTable) : IRowDifferenceSink
{
    private readonly ConcurrentDictionary<DatabaseObjectId, List<RowDifference>> _rows = new();

    public int MaxRowsPerTable { get; } = maxRowsPerTable;

    public void Add(RowDifference difference)
    {
        ArgumentNullException.ThrowIfNull(difference);
        var rows = _rows.GetOrAdd(difference.TableId, _ => []);
        lock (rows)
        {
            if (rows.Count < MaxRowsPerTable)
            {
                rows.Add(difference);
            }
        }
    }

    public void Discard(DatabaseObjectId tableId)
    {
        if (_rows.TryGetValue(tableId, out var rows))
        {
            lock (rows)
            {
                rows.Clear();
            }
        }
    }

    public IReadOnlyList<RowDifference> RowsOf(DatabaseObjectId tableId)
    {
        if (!_rows.TryGetValue(tableId, out var rows))
        {
            return [];
        }

        lock (rows)
        {
            return [.. rows];
        }
    }
}
