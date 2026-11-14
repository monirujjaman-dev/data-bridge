using DataBridge.Core.Data;

namespace DataBridge.Core.Comparison;

/// <summary>One row as read for comparison: its key values in key order and its compared values in plan order.</summary>
public sealed record DataRow(IReadOnlyList<DatabaseValue> Key, IReadOnlyList<DatabaseValue> Values);

/// <summary>Receives differing rows as they are found. Implementations must bound what they keep.</summary>
public interface IRowDifferenceSink
{
    void Add(RowDifference difference);

    /// <summary>The table's matching was stopped (duplicate keys, order mismatch, failure): its rows so far are unreliable.</summary>
    void Discard(Schema.DatabaseObjectId tableId);
}

/// <summary>Counts and outcome of one merge.</summary>
public sealed record MergeResult(
    TableDataComparisonOutcome Outcome,
    long EqualRowCount,
    long SourceOnlyRowCount,
    long TargetOnlyRowCount,
    long ModifiedRowCount,
    long SourceRowsRead,
    long TargetRowsRead,
    string? Reason);

/// <summary>
/// Compares two row streams that are each sorted by the matching key, in one pass (architecture §8): the smaller key
/// is source- or target-only, equal keys are compared column by column. Holds one row per side plus the current
/// batches, so memory is bounded by the batch size, not the table size (NFR-PER-001).
/// </summary>
/// <remarks>
/// The merge is only correct if both streams arrive in strictly increasing order under <see cref="IKeyOrdering"/>.
/// That is verified row by row instead of assumed: an equal neighbour is a duplicate key
/// (<see cref="TableDataComparisonOutcome.DuplicateKeysFound"/>), a smaller one means the server ordered keys
/// differently (<see cref="TableDataComparisonOutcome.KeyOrderMismatch"/>). Either stops the merge, and the sink is
/// told to discard the table's rows. Reasons never contain row values.
/// </remarks>
public sealed class OrderedRowMerger
{
    private readonly TableDataComparisonPlan _plan;
    private readonly CompositeKeyComparer _keys;

    public OrderedRowMerger(TableDataComparisonPlan plan, IKeyOrdering ordering)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _keys = new CompositeKeyComparer(ordering);
    }

    /// <param name="source">Source rows in key order, in batches.</param>
    /// <param name="target">Target rows in key order, in batches.</param>
    /// <param name="sink">Receives each differing row.</param>
    /// <param name="progress">Receives rows read so far (source, target) after each batch.</param>
    /// <param name="cancellationToken">Stops the merge between rows.</param>
    public async Task<MergeResult> MergeAsync(
        IAsyncEnumerable<IReadOnlyList<DataRow>> source,
        IAsyncEnumerable<IReadOnlyList<DataRow>> target,
        IRowDifferenceSink sink,
        Action<long, long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sink);

        await using var sourceRows = new RowCursor(source, ComparisonSide.Source, _keys, cancellationToken);
        await using var targetRows = new RowCursor(target, ComparisonSide.Target, _keys, cancellationToken);
        var counts = new Counts();
        void Report() => progress?.Invoke(sourceRows.RowsRead, targetRows.RowsRead);
        sourceRows.BatchRead += Report;
        targetRows.BatchRead += Report;

        try
        {
            var hasSource = await sourceRows.MoveNextAsync();
            var hasTarget = await targetRows.MoveNextAsync();
            while (hasSource || hasTarget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var order = (hasSource, hasTarget) switch
                {
                    (true, true) => _keys.Compare(sourceRows.Current.Key, targetRows.Current.Key),
                    (true, false) => -1,
                    _ => 1,
                };

                if (order < 0)
                {
                    sink.Add(OneSided(sourceRows.Current, DifferenceStatus.SourceOnly));
                    counts.SourceOnly++;
                    hasSource = await sourceRows.MoveNextAsync();
                }
                else if (order > 0)
                {
                    sink.Add(OneSided(targetRows.Current, DifferenceStatus.TargetOnly));
                    counts.TargetOnly++;
                    hasTarget = await targetRows.MoveNextAsync();
                }
                else
                {
                    CompareMatched(sourceRows.Current, targetRows.Current, sink, counts);
                    hasSource = await sourceRows.MoveNextAsync();
                    hasTarget = await targetRows.MoveNextAsync();
                }
            }
        }
        catch (MergeStoppedException stopped)
        {
            sink.Discard(_plan.TableId);
            return counts.ToResult(stopped.Outcome, sourceRows.RowsRead, targetRows.RowsRead, stopped.Message);
        }

        Report();
        return counts.ToResult(TableDataComparisonOutcome.Compared, sourceRows.RowsRead, targetRows.RowsRead, null);
    }

    private void CompareMatched(DataRow source, DataRow target, IRowDifferenceSink sink, Counts counts)
    {
        List<ColumnDifference>? changed = null;
        for (var index = 0; index < _plan.ValueColumns.Count; index++)
        {
            if (!ValueEquality.AreEqual(source.Values[index], target.Values[index]))
            {
                (changed ??= []).Add(new ColumnDifference(
                    _plan.ValueColumns[index].Name, source.Values[index], target.Values[index], DifferenceStatus.Modified));
            }
        }

        if (changed is null)
        {
            counts.Equal++;
            return;
        }

        counts.Modified++;
        sink.Add(new RowDifference(
            _plan.TableId,
            source.Key,
            DifferenceStatus.Modified,
            changed,
            RowFingerprint.Compute(source.Values),
            RowFingerprint.Compute(target.Values)));
    }

    private RowDifference OneSided(DataRow row, DifferenceStatus status)
    {
        var isSource = status == DifferenceStatus.SourceOnly;
        var columns = new ColumnDifference[_plan.ValueColumns.Count];
        for (var index = 0; index < columns.Length; index++)
        {
            var value = row.Values[index];
            columns[index] = new ColumnDifference(_plan.ValueColumns[index].Name, isSource ? value : null, isSource ? null : value, status);
        }

        var fingerprint = RowFingerprint.Compute(row.Values);
        return new RowDifference(_plan.TableId, row.Key, status, columns, isSource ? fingerprint : null, isSource ? null : fingerprint);
    }

    private sealed class Counts
    {
        public long Equal { get; set; }

        public long SourceOnly { get; set; }

        public long TargetOnly { get; set; }

        public long Modified { get; set; }

        public MergeResult ToResult(TableDataComparisonOutcome outcome, long sourceRows, long targetRows, string? reason) =>
            new(outcome, Equal, SourceOnly, TargetOnly, Modified, sourceRows, targetRows, reason);
    }

    private sealed class MergeStoppedException(TableDataComparisonOutcome outcome, string message) : Exception(message)
    {
        public TableDataComparisonOutcome Outcome { get; } = outcome;
    }

    /// <summary>Walks one side's batches row by row and checks that keys strictly increase.</summary>
    private sealed class RowCursor(
        IAsyncEnumerable<IReadOnlyList<DataRow>> batches,
        ComparisonSide side,
        CompositeKeyComparer keys,
        CancellationToken cancellationToken) : IAsyncDisposable
    {
        private readonly IAsyncEnumerator<IReadOnlyList<DataRow>> _batches = batches.GetAsyncEnumerator(cancellationToken);
        private IReadOnlyList<DataRow> _batch = [];
        private int _index = -1;
        private DataRow? _current;

        public event Action? BatchRead;

        public long RowsRead { get; private set; }

        public DataRow Current => _current ?? throw new InvalidOperationException("No current row.");

        public async ValueTask<bool> MoveNextAsync()
        {
            var previous = _current;
            while (++_index >= _batch.Count)
            {
                if (!await _batches.MoveNextAsync())
                {
                    _current = null;
                    return false;
                }

                _batch = _batches.Current;
                _index = -1;
                BatchRead?.Invoke();
            }

            _current = _batch[_index];
            RowsRead++;
            if (previous is not null)
            {
                Check(previous, _current);
            }

            return true;
        }

        public ValueTask DisposeAsync() => _batches.DisposeAsync();

        private void Check(DataRow previous, DataRow current)
        {
            var order = keys.Compare(previous.Key, current.Key);
            if (order == 0)
            {
                throw new MergeStoppedException(
                    TableDataComparisonOutcome.DuplicateKeysFound,
                    $"The {Name} has more than one row with the same key value, so rows cannot be matched on this key. Select key columns that are unique.");
            }

            if (order > 0)
            {
                throw new MergeStoppedException(
                    TableDataComparisonOutcome.KeyOrderMismatch,
                    $"The {Name} server returned keys in an order the comparison does not reproduce (after row {RowsRead - 1}), so matching was stopped to avoid wrong results.");
            }
        }

        private string Name => side == ComparisonSide.Source ? "source" : "target";
    }
}
