using BenchmarkDotNet.Attributes;
using DataBridge.Core.Comparison;
using DataBridge.Core.Data;
using DataBridge.Core.Schema;

namespace DataBridge.Benchmarks;

/// <summary>
/// Throughput of the ordered merge (rows per second) for equal and mostly-different tables, and the cost of the
/// canonical row fingerprint computed for each differing row. Run with <c>--filter "*RowMerge*"</c>.
/// </summary>
[MemoryDiagnoser]
public class RowMergeBenchmarks
{
    private static readonly DatabaseObjectId _table = new("dbo", "Bench", DatabaseObjectType.Table);
    private DataRow[][] _source = [];
    private DataRow[][] _equalTarget = [];
    private DataRow[][] _changedTarget = [];
    private TableDataComparisonPlan _plan = null!;

    [Params(100_000)]
    public int Rows { get; set; }

    [Params(1000)]
    public int BatchSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var id = new ColumnDefinition("Id", "bigint", null, false, null, null, false, null, null, null, 1);
        var name = new ColumnDefinition("Name", "nvarchar", 100, false, null, null, true, null, null, null, 2);
        var amount = new ColumnDefinition("Amount", "decimal", null, false, 18, 2, true, null, null, null, 3);
        _plan = new TableDataComparisonPlan(
            _table,
            _table,
            new MatchingKey(_table, ["Id"], MatchingKeySource.PrimaryKey),
            [new ComparedColumn("Id", id, id, ValueKind.WholeNumber)],
            [new ComparedColumn("Name", name, name, ValueKind.Text), new ComparedColumn("Amount", amount, amount, ValueKind.ExactNumeric)],
            []);
        _source = Build(changed: false);
        _equalTarget = Build(changed: false);
        _changedTarget = Build(changed: true);
    }

    [Benchmark(Baseline = true)]
    public Task<MergeResult> EqualTables() => MergeAsync(_equalTarget);

    [Benchmark]
    public Task<MergeResult> AllRowsModified() => MergeAsync(_changedTarget);

    [Benchmark]
    public string Fingerprint() => RowFingerprint.Compute(_source[0][0].Values);

    private Task<MergeResult> MergeAsync(DataRow[][] target) =>
        new OrderedRowMerger(_plan, DefaultKeyOrdering.Instance).MergeAsync(Stream(_source), Stream(target), new NullSink(), null, CancellationToken.None);

    private DataRow[][] Build(bool changed) =>
        Enumerable.Range(0, Rows)
            .Select(id => new DataRow(
                [DatabaseValue.FromInt64(id, "bigint")],
                [DatabaseValue.FromText($"Customer {id % 1000}", "nvarchar"), DatabaseValue.FromDecimal(new DecimalValue(changed ? id + 1 : id, 2), "decimal")]))
            .Chunk(BatchSize)
            .ToArray();

#pragma warning disable CS1998 // A synchronous async stream isolates merge cost from I/O.
    private static async IAsyncEnumerable<IReadOnlyList<DataRow>> Stream(DataRow[][] batches)
#pragma warning restore CS1998
    {
        foreach (var batch in batches)
        {
            yield return batch;
        }
    }

    private sealed class NullSink : IRowDifferenceSink
    {
        public void Add(RowDifference difference)
        {
        }

        public void Discard(DatabaseObjectId tableId)
        {
        }
    }
}
