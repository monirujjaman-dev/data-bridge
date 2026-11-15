using DataBridge.Application.Abstractions;
using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Data;
using DataBridge.Shared.Results;

namespace DataBridge.Application.DataComparison;

/// <summary>The tables of two profiles, and how each would be matched.</summary>
public sealed record DataComparisonTables(
    ResolvedDatabaseIdentity Source,
    ResolvedDatabaseIdentity Target,
    IReadOnlyList<DataComparisonTable> Tables);

/// <summary>
/// Lists the tables two saved profiles could compare (FR-DAT-001, FR-DAT-002, FR-DAT-012): for each, the key that would
/// match rows, or why none can, and the columns a user could select as a key. Reads catalog metadata only.
/// </summary>
public sealed class ListDataComparisonTables(ComparisonSetup setup, IDatabaseProviderFactory providers)
{
    public async Task<Result<DataComparisonTables>> ExecuteAsync(Guid sourceProfileId, Guid targetProfileId, CancellationToken cancellationToken)
    {
        var prepared = await setup.PrepareAsync(sourceProfileId, targetProfileId, cancellationToken);
        if (prepared.IsFailure)
        {
            return Result.Failure<DataComparisonTables>(prepared.Errors);
        }

        var comparison = prepared.Value;
        var reader = providers.GetDataReader(comparison.Source.Profile.Provider);
        var planner = new DataComparisonPlanner(reader.Classifier);
        var names = comparison.Names;
        var tables = comparison.PairTables()
            .Select(pair =>
            {
                var plan = planner.Plan(pair.Source, pair.Target, null, includeBinaryColumns: true, names);
                var candidates = pair is { Source: { } source, Target: { } target }
                    ? source.Columns
                        .Where(column => !column.IsNullable && reader.Classifier.Classify(column) is not (ValueKind.Unsupported or ValueKind.RowVersion or ValueKind.ApproximateNumeric or ValueKind.Xml))
                        .Where(column => target.Columns.Any(other => names.Equals(other.Name, column.Name) && !other.IsNullable))
                        .Select(column => column.Name)
                        .ToArray()
                    : [];
                return new DataComparisonTable(
                    pair.Id,
                    plan.Plan?.Key,
                    plan.NotCompared?.Outcome,
                    plan.NotCompared?.Reason,
                    candidates);
            })
            .ToArray();

        return Result.Success(new DataComparisonTables(comparison.SourceIdentity, comparison.TargetIdentity, tables));
    }
}
