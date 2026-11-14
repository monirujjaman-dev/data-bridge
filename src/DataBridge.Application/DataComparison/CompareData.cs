using DataBridge.Application.Abstractions;
using DataBridge.Core.Comparison;
using DataBridge.Core.Schema;
using DataBridge.Shared.Results;
using Microsoft.Extensions.Logging;

namespace DataBridge.Application.DataComparison;

/// <summary>
/// Compares the rows of selected tables in two saved profiles (FR-DAT-001 to FR-DAT-016). Read-only on both databases.
/// </summary>
/// <remarks>
/// <para>
/// Each table is planned (key and columns), then read on both sides in key order and merged in one pass. Differing rows
/// go to the caller's <see cref="IRowDifferenceSink"/> as they are found; only per-table summaries are returned, so
/// memory is bounded by batch size and parallelism, not table size (NFR-PER-001).
/// </para>
/// <para>
/// Up to <see cref="DataComparisonOptions.MaxDegreeOfParallelism"/> tables run at once, each with its own read
/// session per side, so the sink must be thread-safe. A table that fails is reported as failed and the others continue;
/// cancellation stops everything and throws.
/// </para>
/// </remarks>
public sealed class CompareData(
    ComparisonSetup setup,
    IDatabaseProviderFactory providers,
    TimeProvider timeProvider,
    ILogger<CompareData> logger)
{
    public async Task<Result<DataComparisonReport>> ExecuteAsync(
        DataComparisonRequest request,
        IRowDifferenceSink sink,
        IProgress<DataComparisonProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);
        if (request.Options.Validate().ToArray() is { Length: > 0 } invalid)
        {
            return Result.Failure<DataComparisonReport>(invalid);
        }

        var sessionId = Guid.NewGuid();
        var startedAtUtc = timeProvider.GetUtcNow();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["SessionId"] = sessionId });
        DataComparisonLog.Started(logger, request.SourceProfileId, request.TargetProfileId, request.Tables.Count);

        try
        {
            progress?.Report(new DataComparisonProgress(null, ComparisonStatus.Connecting, 0, 0, 0, 0));
            var prepared = await setup.PrepareAsync(request.SourceProfileId, request.TargetProfileId, cancellationToken);
            if (prepared.IsFailure)
            {
                var codes = string.Join(",", prepared.Errors.Select(error => error.Code));
                DataComparisonLog.Failed(logger, codes);
                return Result.Failure<DataComparisonReport>(prepared.Errors);
            }

            var comparison = prepared.Value;
            var tables = SelectTables(comparison, request);
            if (tables.Count == 0)
            {
                return Result.Failure<DataComparisonReport>(DataComparisonErrors.NoTables);
            }

            var summaries = await CompareTablesAsync(comparison, request, tables, sink, progress, cancellationToken);
            var report = new DataComparisonReport(
                sessionId, comparison.SourceIdentity, comparison.TargetIdentity, summaries, startedAtUtc, timeProvider.GetUtcNow());
            LogCompleted(report);
            return Result.Success(report);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DataComparisonLog.Cancelled(logger);
            throw;
        }
    }

    private static List<(DatabaseObjectId Id, TableDefinition? Source, TableDefinition? Target)> SelectTables(
        PreparedComparison comparison,
        DataComparisonRequest request)
    {
        var pairs = comparison.PairTables();
        if (request.Tables.Count == 0)
        {
            return [.. pairs];
        }

        var names = comparison.Names;
        return pairs
            .Where(pair => request.Tables.Any(id => names.Equals(id.SchemaName, pair.Id.SchemaName) && names.Equals(id.ObjectName, pair.Id.ObjectName)))
            .ToList();
    }

    private async Task<TableDataComparisonSummary[]> CompareTablesAsync(
        PreparedComparison comparison,
        DataComparisonRequest request,
        List<(DatabaseObjectId Id, TableDefinition? Source, TableDefinition? Target)> tables,
        IRowDifferenceSink sink,
        IProgress<DataComparisonProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourceReader = providers.GetDataReader(comparison.Source.Profile.Provider);
        var targetReader = providers.GetDataReader(comparison.Target.Profile.Provider);
        var planner = new DataComparisonPlanner(sourceReader.Classifier);
        var names = comparison.Names;
        var completed = 0;
        using var slots = new SemaphoreSlim(request.Options.MaxDegreeOfParallelism);

        async Task<TableDataComparisonSummary> RunAsync((DatabaseObjectId Id, TableDefinition? Source, TableDefinition? Target) table)
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                var userKey = request.UserKeys.FirstOrDefault(pair =>
                    names.Equals(pair.Key.SchemaName, table.Id.SchemaName) && names.Equals(pair.Key.ObjectName, table.Id.ObjectName)).Value;
                var plan = planner.Plan(table.Source, table.Target, userKey, request.Options.IncludeBinaryColumns, names);
                var summary = plan.Plan is { } tablePlan
                    ? await CompareTableAsync(comparison, sourceReader, targetReader, tablePlan, request.Options, sink, progress, () => completed, tables.Count, cancellationToken)
                    : plan.NotCompared!;
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new DataComparisonProgress(table.Id, ComparisonStatus.ComparingData, done, tables.Count, 0, 0));
                return summary;
            }
            finally
            {
                slots.Release();
            }
        }

        return await Task.WhenAll(tables.Select(RunAsync));
    }

    private async Task<TableDataComparisonSummary> CompareTableAsync(
        PreparedComparison comparison,
        IDataReaderProvider sourceProvider,
        IDataReaderProvider targetProvider,
        TableDataComparisonPlan plan,
        DataComparisonOptions options,
        IRowDifferenceSink sink,
        IProgress<DataComparisonProgress>? progress,
        Func<int> completed,
        int total,
        CancellationToken cancellationToken)
    {
        var opened = await Task.WhenAll(
            sourceProvider.OpenAsync(comparison.Source, options.PreferredConsistency, cancellationToken),
            targetProvider.OpenAsync(comparison.Target, options.PreferredConsistency, cancellationToken));
        await using var sourceRows = opened[0].IsSuccess ? opened[0].Value : null;
        await using var targetRows = opened[1].IsSuccess ? opened[1].Value : null;
        if (sourceRows is null || targetRows is null)
        {
            var error = (opened[0].IsFailure ? opened[0] : opened[1]).Errors[0];
            return Failed(plan, $"Could not start reading: {error.Message}");
        }

        var sourceRequest = new TableReadRequest(
            plan.TableId,
            [.. plan.KeyColumns.Select(column => new ReadColumn(column.Source, column.Kind))],
            [.. plan.ValueColumns.Select(column => new ReadColumn(column.Source, column.Kind))],
            options.BatchSize);
        var targetRequest = new TableReadRequest(
            plan.TargetTableId,
            [.. plan.KeyColumns.Select(column => new ReadColumn(column.Target, column.Kind))],
            [.. plan.ValueColumns.Select(column => new ReadColumn(column.Target, column.Kind))],
            options.BatchSize);

        try
        {
            var result = await new OrderedRowMerger(plan, sourceProvider.KeyOrdering).MergeAsync(
                sourceRows.ReadRowsAsync(sourceRequest, cancellationToken),
                targetRows.ReadRowsAsync(targetRequest, cancellationToken),
                sink,
                (sourceRead, targetRead) => progress?.Report(new DataComparisonProgress(
                    plan.TableId, ComparisonStatus.ComparingData, completed(), total, sourceRead, targetRead)),
                cancellationToken);

            return new TableDataComparisonSummary(
                plan.TableId,
                plan.Key,
                result.Outcome,
                result.EqualRowCount,
                result.SourceOnlyRowCount,
                result.TargetOnlyRowCount,
                result.ModifiedRowCount,
                plan.ExcludedColumns,
                result.Reason,
                sourceRows.AchievedConsistency,
                targetRows.AchievedConsistency);
        }
        catch (DataReadException exception) when (!cancellationToken.IsCancellationRequested)
        {
            sink.Discard(plan.TableId);
            DataComparisonLog.TableFailed(logger, exception.GetType().Name);
            return Failed(plan, exception.Message) with
            {
                SourceConsistency = sourceRows.AchievedConsistency,
                TargetConsistency = targetRows.AchievedConsistency,
            };
        }
    }

    private static TableDataComparisonSummary Failed(TableDataComparisonPlan plan, string reason) =>
        TableDataComparisonSummary.NotCompared(plan.TableId, TableDataComparisonOutcome.Failed, reason, plan.Key, plan.ExcludedColumns);

    private void LogCompleted(DataComparisonReport report)
    {
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var compared = report.Tables.Count(table => table.Outcome == TableDataComparisonOutcome.Compared);
        var differences = report.Tables.Sum(table => table.DifferenceCount);
        var durationMs = (report.CompletedAtUtc - report.StartedAtUtc).TotalMilliseconds;
        DataComparisonLog.Completed(logger, report.Tables.Count, compared, differences, durationMs);
    }
}

/// <summary>Data comparison log events: ids, counts and durations only, never names or row values.</summary>
internal static partial class DataComparisonLog
{
    [LoggerMessage(EventId = 3100, Level = LogLevel.Information, Message = "Data comparison started: source profile {SourceProfileId}, target profile {TargetProfileId}, {RequestedTableCount} tables requested")]
    public static partial void Started(ILogger logger, Guid sourceProfileId, Guid targetProfileId, int requestedTableCount);

    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "Data comparison completed: {TableCount} tables, {ComparedCount} compared, {DifferenceCount} differing rows, {DurationMs} ms")]
    public static partial void Completed(ILogger logger, int tableCount, int comparedCount, long differenceCount, double durationMs);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Information, Message = "Data comparison failed: {ErrorCodes}")]
    public static partial void Failed(ILogger logger, string errorCodes);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Information, Message = "Data comparison cancelled")]
    public static partial void Cancelled(ILogger logger);

    [LoggerMessage(EventId = 3104, Level = LogLevel.Warning, Message = "A table's data read failed ({ExceptionType})")]
    public static partial void TableFailed(ILogger logger, string exceptionType);
}
