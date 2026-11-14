using DataBridge.Core.Data;
using DataBridge.Core.Schema;

namespace DataBridge.Core.Comparison;

/// <summary>Maps a provider's column types to comparison families. Implemented by each provider.</summary>
public interface IValueKindClassifier
{
    ValueKind Classify(ColumnDefinition column);
}

/// <summary>A column compared on both sides, with the family its values are compared in.</summary>
public sealed record ComparedColumn(string Name, ColumnDefinition Source, ColumnDefinition Target, ValueKind Kind);

/// <summary>How one table's rows will be read and matched: the key, the compared columns and the excluded ones.</summary>
/// <param name="TableId">The table, named as in the source.</param>
/// <param name="TargetTableId">The table as named in the target (it can differ in case).</param>
/// <param name="Key">The matching key.</param>
/// <param name="KeyColumns">The key columns, in key order.</param>
/// <param name="ValueColumns">The other compared columns, in source column order.</param>
/// <param name="ExcludedColumns">Columns left out, with reasons.</param>
public sealed record TableDataComparisonPlan(
    DatabaseObjectId TableId,
    DatabaseObjectId TargetTableId,
    MatchingKey Key,
    IReadOnlyList<ComparedColumn> KeyColumns,
    IReadOnlyList<ComparedColumn> ValueColumns,
    IReadOnlyList<ExcludedColumn> ExcludedColumns);

/// <summary>Either a plan, or the summary explaining why the table cannot be compared.</summary>
public sealed record TablePlanResult(TableDataComparisonPlan? Plan, TableDataComparisonSummary? NotCompared)
{
    public static TablePlanResult Of(TableDataComparisonPlan plan) => new(plan, null);

    public static TablePlanResult Rejected(TableDataComparisonSummary summary) => new(null, summary);
}

/// <summary>
/// Decides how to compare one table's data (FR-DAT-002, FR-DAT-003, FR-DAT-012, FR-DAT-015): picks a matching key and
/// matches columns by name. Never matches rows by position, and never coerces values across type families.
/// </summary>
/// <remarks>
/// Key candidates come from the source table, in order: primary key, unique constraints, then unique indexes that are
/// neither filtered nor disabled. The first candidate whose columns exist on both sides, allow no NULL on either side
/// and have the same orderable kind is used. A user-selected key replaces discovery and is checked the same way; its
/// uniqueness is verified while merging. The target does not need the same constraint: duplicates there are detected
/// while merging too.
/// </remarks>
public sealed class DataComparisonPlanner(IValueKindClassifier classifier)
{
    private static readonly HashSet<ValueKind> _orderableKinds =
    [
        ValueKind.WholeNumber, ValueKind.ExactNumeric, ValueKind.Boolean, ValueKind.Text, ValueKind.Binary, ValueKind.UniqueIdentifier,
        ValueKind.Date, ValueKind.Time, ValueKind.DateTime, ValueKind.DateTimeOffset,
    ];

    private readonly IValueKindClassifier _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));

    /// <param name="source">The source table, or <see langword="null"/> when it does not exist there.</param>
    /// <param name="target">The target table, or <see langword="null"/> when it does not exist there.</param>
    /// <param name="userKeyColumns">Key columns the user selected, in order; <see langword="null"/> to discover a key.</param>
    /// <param name="includeBinaryColumns">Whether binary columns are compared.</param>
    /// <param name="names">How column names match: ordinal, or ignoring case when both databases do.</param>
    public TablePlanResult Plan(
        TableDefinition? source,
        TableDefinition? target,
        IReadOnlyList<string>? userKeyColumns,
        bool includeBinaryColumns,
        StringComparer names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (source is null || target is null)
        {
            var id = (source ?? target ?? throw new ArgumentException("At least one side must have the table.")).Id;
            return Rejected(id, TableDataComparisonOutcome.TableMissing, $"The table exists only in the {(source is null ? "target" : "source")}.");
        }

        var targetColumns = target.Columns.ToDictionary(column => column.Name, names);
        var key = userKeyColumns is { Count: > 0 }
            ? UserKey(source, targetColumns, userKeyColumns, names)
            : DiscoverKey(source, targetColumns, names);
        if (key.Rejection is { } rejection)
        {
            return TablePlanResult.Rejected(rejection);
        }

        var keyNames = new HashSet<string>(key.Columns!.Select(column => column.Name), names);
        var (values, excluded) = MatchValueColumns(source, target, targetColumns, keyNames, includeBinaryColumns, names);
        return TablePlanResult.Of(new TableDataComparisonPlan(source.Id, target.Id, key.Key!, key.Columns!, values, excluded));
    }

    private sealed record KeyChoice(MatchingKey? Key, IReadOnlyList<ComparedColumn>? Columns, TableDataComparisonSummary? Rejection);

    private KeyChoice UserKey(
        TableDefinition source,
        Dictionary<string, ColumnDefinition> targetColumns,
        IReadOnlyList<string> columns,
        StringComparer names)
    {
        if (columns.Distinct(names).Count() != columns.Count)
        {
            return new KeyChoice(null, null, Summary(source.Id, TableDataComparisonOutcome.IncompatibleKeyTypes, "The selected key lists a column more than once."));
        }

        var sourceColumns = source.Columns.ToDictionary(column => column.Name, names);
        var missing = columns.FirstOrDefault(name => !sourceColumns.ContainsKey(name) || !targetColumns.ContainsKey(name));
        if (missing is not null)
        {
            return new KeyChoice(null, null, Summary(source.Id, TableDataComparisonOutcome.IncompatibleKeyTypes, $"The selected key column {missing} does not exist on both sides."));
        }

        var key = new MatchingKey(source.Id, columns.Select(name => sourceColumns[name].Name).ToArray(), MatchingKeySource.UserSelected);
        return Validate(key, sourceColumns, targetColumns) is { } problem
            ? new KeyChoice(null, null, Summary(source.Id, TableDataComparisonOutcome.IncompatibleKeyTypes, problem))
            : new KeyChoice(key, KeyColumns(key, sourceColumns, targetColumns), null);
    }

    private KeyChoice DiscoverKey(TableDefinition source, Dictionary<string, ColumnDefinition> targetColumns, StringComparer names)
    {
        var sourceColumns = source.Columns.ToDictionary(column => column.Name, names);
        string? firstProblem = null;
        foreach (var candidate in Candidates(source))
        {
            if (candidate.OrderedColumnNames.Any(name => !targetColumns.ContainsKey(name)))
            {
                firstProblem ??= $"The target has no column {candidate.OrderedColumnNames.First(name => !targetColumns.ContainsKey(name))} of the {Describe(candidate.Source)}.";
                continue;
            }

            if (Validate(candidate, sourceColumns, targetColumns) is { } problem)
            {
                firstProblem ??= problem;
                continue;
            }

            return new KeyChoice(candidate, KeyColumns(candidate, sourceColumns, targetColumns), null);
        }

        return firstProblem is null
            ? new KeyChoice(null, null, Summary(
                source.Id,
                TableDataComparisonOutcome.NoUniqueKey,
                "The table has no primary key, unique constraint or unique index. Select the columns that identify a row."))
            : new KeyChoice(null, null, Summary(source.Id, TableDataComparisonOutcome.IncompatibleKeyTypes, firstProblem));
    }

    private static IEnumerable<MatchingKey> Candidates(TableDefinition table)
    {
        static string[] KeyNames(IReadOnlyList<IndexColumnDefinition> columns) =>
            columns.Where(column => !column.IsIncluded).OrderBy(column => column.KeyOrdinal).Select(column => column.ColumnName).ToArray();

        if (table.PrimaryKey is { } primaryKey)
        {
            yield return new MatchingKey(table.Id, KeyNames(primaryKey.Columns), MatchingKeySource.PrimaryKey);
        }

        foreach (var constraint in table.UniqueConstraints)
        {
            yield return new MatchingKey(table.Id, KeyNames(constraint.Columns), MatchingKeySource.UniqueConstraint);
        }

        foreach (var index in table.Indexes.Where(index => index.IsUnique && index.FilterDefinition is null && !index.Options.IsDisabled))
        {
            var columns = KeyNames(index.Columns);
            if (columns.Length > 0)
            {
                yield return new MatchingKey(table.Id, columns, MatchingKeySource.UniqueIndex);
            }
        }
    }

    /// <summary>Why <paramref name="key"/> cannot order and match rows, or <see langword="null"/> when it can.</summary>
    private string? Validate(
        MatchingKey key,
        Dictionary<string, ColumnDefinition> sourceColumns,
        Dictionary<string, ColumnDefinition> targetColumns)
    {
        foreach (var name in key.OrderedColumnNames)
        {
            var (source, target) = (sourceColumns[name], targetColumns[name]);
            var (sourceKind, targetKind) = (_classifier.Classify(source), _classifier.Classify(target));
            if (sourceKind != targetKind)
            {
                return $"Key column {name} is {source.SqlTypeName} in the source and {target.SqlTypeName} in the target, which cannot be matched safely.";
            }

            if (!_orderableKinds.Contains(sourceKind))
            {
                return $"Key column {name} has type {source.SqlTypeName}, which cannot be used to order and match rows.";
            }

            if (source.IsNullable || target.IsNullable)
            {
                return $"Key column {name} allows NULL in the {(source.IsNullable ? "source" : "target")}; key columns must not allow NULL.";
            }
        }

        return null;
    }

    private ComparedColumn[] KeyColumns(
        MatchingKey key,
        Dictionary<string, ColumnDefinition> sourceColumns,
        Dictionary<string, ColumnDefinition> targetColumns) =>
        key.OrderedColumnNames
            .Select(name => new ComparedColumn(sourceColumns[name].Name, sourceColumns[name], targetColumns[name], _classifier.Classify(sourceColumns[name])))
            .ToArray();

    private (IReadOnlyList<ComparedColumn> Values, IReadOnlyList<ExcludedColumn> Excluded) MatchValueColumns(
        TableDefinition source,
        TableDefinition target,
        Dictionary<string, ColumnDefinition> targetColumns,
        HashSet<string> keyNames,
        bool includeBinaryColumns,
        StringComparer names)
    {
        var values = new List<ComparedColumn>();
        var excluded = new List<ExcludedColumn>();
        foreach (var column in source.Columns.Where(column => !keyNames.Contains(column.Name)))
        {
            if (!targetColumns.TryGetValue(column.Name, out var other))
            {
                excluded.Add(new ExcludedColumn(column.Name, "The column exists only in the source."));
                continue;
            }

            if (ExclusionReason(column, other, includeBinaryColumns) is { } reason)
            {
                excluded.Add(new ExcludedColumn(column.Name, reason));
                continue;
            }

            values.Add(new ComparedColumn(column.Name, column, other, _classifier.Classify(column)));
        }

        var sourceNames = new HashSet<string>(source.Columns.Select(column => column.Name), names);
        excluded.AddRange(target.Columns
            .Where(column => !sourceNames.Contains(column.Name))
            .Select(column => new ExcludedColumn(column.Name, "The column exists only in the target.")));
        return (values, excluded);
    }

    private string? ExclusionReason(ColumnDefinition source, ColumnDefinition target, bool includeBinaryColumns)
    {
        var (sourceKind, targetKind) = (_classifier.Classify(source), _classifier.Classify(target));
        if (source.Computed is not null || target.Computed is not null)
        {
            return "Computed columns are derived from other columns and are not compared.";
        }

        if (sourceKind == ValueKind.RowVersion || targetKind == ValueKind.RowVersion)
        {
            return "Row versions are generated by each server and always differ.";
        }

        if (sourceKind == ValueKind.Unsupported || targetKind == ValueKind.Unsupported)
        {
            var type = sourceKind == ValueKind.Unsupported ? source.SqlTypeName : target.SqlTypeName;
            return $"Values of type {type} are not compared.";
        }

        if (sourceKind != targetKind)
        {
            return $"The column is {source.SqlTypeName} in the source and {target.SqlTypeName} in the target, which cannot be compared without conversion.";
        }

        return sourceKind == ValueKind.Binary && !includeBinaryColumns ? "Binary columns are excluded by the comparison options." : null;
    }

    private static TablePlanResult Rejected(DatabaseObjectId id, TableDataComparisonOutcome outcome, string reason) =>
        TablePlanResult.Rejected(Summary(id, outcome, reason));

    private static TableDataComparisonSummary Summary(DatabaseObjectId id, TableDataComparisonOutcome outcome, string reason) =>
        TableDataComparisonSummary.NotCompared(id, outcome, reason);

    private static string Describe(MatchingKeySource source) => source switch
    {
        MatchingKeySource.PrimaryKey => "primary key",
        MatchingKeySource.UniqueConstraint => "unique constraint",
        MatchingKeySource.UniqueIndex => "unique index",
        _ => "selected key",
    };
}
