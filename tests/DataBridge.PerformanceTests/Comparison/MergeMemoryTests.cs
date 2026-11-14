using System.Runtime.CompilerServices;
using DataBridge.Core.Comparison;
using DataBridge.Core.Data;
using DataBridge.Core.Schema;

namespace DataBridge.PerformanceTests.Comparison;

/// <summary>
/// NFR-PER-001 and the Phase 4 acceptance: memory while merging is bounded by batch size, not table size. Rows are
/// generated on demand, so only the merge itself holds data.
/// </summary>
public sealed class MergeMemoryTests
{
    private const int BatchSize = 1000;

    /// <summary>Generous for two batches of small rows plus runtime noise, far below what a materialized table needs.</summary>
    private const long MaxRetainedBytes = 64L * 1024 * 1024;

    private static readonly DatabaseObjectId _table = new("dbo", "Big", DatabaseObjectType.Table);

    [Fact]
    public async Task Retained_memory_does_not_grow_with_row_count()
    {
        var small = await PeakRetainedBytesAsync(rows: 100_000);
        var large = await PeakRetainedBytesAsync(rows: 2_000_000);

        Assert.True(large < MaxRetainedBytes, $"Peak retained memory was {large / 1024 / 1024} MB for 2,000,000 rows.");

        // Twenty times the rows must not mean noticeably more memory: allow 16 MB of measurement noise.
        Assert.True(large - small < 16L * 1024 * 1024, $"Retained memory grew from {small / 1024 / 1024} MB to {large / 1024 / 1024} MB.");
    }

    private static async Task<long> PeakRetainedBytesAsync(int rows)
    {
        var plan = Plan();
        var sink = new CountingSink();
        long peak = 0;
        long nextSample = 0;
        void Sample(long sourceRead, long targetRead)
        {
            if (sourceRead < nextSample)
            {
                return;
            }

            nextSample = sourceRead + (rows / 10);
            peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true));
        }

        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var result = await new OrderedRowMerger(plan, DefaultKeyOrdering.Instance).MergeAsync(
            Generate(rows, modifyEvery: 0, TestContext.Current.CancellationToken),
            Generate(rows, modifyEvery: 97, TestContext.Current.CancellationToken),
            sink,
            Sample,
            TestContext.Current.CancellationToken);

        Assert.Equal(TableDataComparisonOutcome.Compared, result.Outcome);
        Assert.Equal(rows, result.EqualRowCount + result.ModifiedRowCount);
        Assert.Equal(sink.Count, result.ModifiedRowCount);
        return Math.Max(0, peak - baseline);
    }

    private static TableDataComparisonPlan Plan()
    {
        var id = new ColumnDefinition("Id", "bigint", null, false, null, null, false, null, null, null, 1);
        var name = new ColumnDefinition("Name", "nvarchar", 100, false, null, null, true, null, null, null, 2);
        var amount = new ColumnDefinition("Amount", "decimal", null, false, 18, 2, true, null, null, null, 3);
        return new TableDataComparisonPlan(
            _table,
            _table,
            new MatchingKey(_table, ["Id"], MatchingKeySource.PrimaryKey),
            [new ComparedColumn("Id", id, id, ValueKind.WholeNumber)],
            [new ComparedColumn("Name", name, name, ValueKind.Text), new ComparedColumn("Amount", amount, amount, ValueKind.ExactNumeric)],
            []);
    }

    private static async IAsyncEnumerable<IReadOnlyList<DataRow>> Generate(
        int rows,
        int modifyEvery,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var start = 0; start < rows; start += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = new DataRow[Math.Min(BatchSize, rows - start)];
            for (var index = 0; index < batch.Length; index++)
            {
                var id = start + index;
                var amount = modifyEvery > 0 && id % modifyEvery == 0 ? id + 1 : id;
                batch[index] = new DataRow(
                    [DatabaseValue.FromInt64(id, "bigint")],
                    [DatabaseValue.FromText($"Customer {id % 1000}", "nvarchar"), DatabaseValue.FromDecimal(new DecimalValue(amount, 2), "decimal")]);
            }

            await Task.Yield();
            yield return batch;
        }
    }

    /// <summary>Counts differences without keeping them, as a bounded UI sink would beyond its display limit.</summary>
    private sealed class CountingSink : IRowDifferenceSink
    {
        public long Count { get; private set; }

        public void Add(RowDifference difference) => Count++;

        public void Discard(DatabaseObjectId tableId)
        {
        }
    }
}
