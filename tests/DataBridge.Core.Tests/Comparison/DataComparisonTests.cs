using DataBridge.Core.Comparison;
using DataBridge.Core.Data;
using DataBridge.Core.Schema;
using static DataBridge.Core.Tests.Comparison.SchemaFixtures;

namespace DataBridge.Core.Tests.Comparison;

public sealed class DataComparisonTests
{
    private static readonly DataComparisonPlanner _planner = new(new TestClassifier());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TablePlanResult Plan(TableDefinition? source, TableDefinition? target, IReadOnlyList<string>? key = null, bool binary = true) =>
        _planner.Plan(source, target, key, binary, StringComparer.OrdinalIgnoreCase);

    private static TableDefinition Keyed(params ColumnDefinition[] columns) =>
        Table("T", columns) with { PrimaryKey = PrimaryKey("PK_T", false, columns[0].Name) };

    // Planning

    [Fact]
    public void Primary_key_is_preferred_and_columns_match_by_name()
    {
        var source = Keyed(Column("Id"), Text("Name", 50), Column("Qty")) with
        {
            UniqueConstraints = [new("UQ", false, "NONCLUSTERED", [Key("Name", 1)], DefaultIndexOptions)],
        };
        var target = Keyed(Column("Id"), Column("Qty"), Text("Name", 80));

        var plan = Plan(source, target).Plan!;

        Assert.Equal(MatchingKeySource.PrimaryKey, plan.Key.Source);
        Assert.Equal(["Id"], plan.Key.OrderedColumnNames);
        Assert.Equal(["Name", "Qty"], plan.ValueColumns.Select(column => column.Name));
        Assert.Empty(plan.ExcludedColumns);
    }

    [Fact]
    public void Unique_constraint_or_unique_index_is_used_without_a_primary_key()
    {
        var unique = Table("T", Column("A"), Column("B"), Column("C")) with
        {
            Indexes = [new("UX", "NONCLUSTERED", true, [Key("B", 1), Key("A", 2), Include("C")], null, DefaultIndexOptions)],
        };

        var plan = Plan(unique, unique).Plan!;

        Assert.Equal(MatchingKeySource.UniqueIndex, plan.Key.Source);
        Assert.Equal(["B", "A"], plan.Key.OrderedColumnNames);
        Assert.Equal(["C"], plan.ValueColumns.Select(column => column.Name));
    }

    [Fact]
    public void Filtered_disabled_and_nullable_keys_are_not_used()
    {
        var table = Table("T", Column("A"), Column("B", nullable: true)) with
        {
            Indexes =
            [
                new("UX_Filtered", "NONCLUSTERED", true, [Key("A", 1)], "([A]>(0))", DefaultIndexOptions),
                new("UX_Disabled", "NONCLUSTERED", true, [Key("A", 1)], null, DefaultIndexOptions with { IsDisabled = true }),
                new("UX_Nullable", "NONCLUSTERED", true, [Key("B", 1)], null, DefaultIndexOptions),
            ],
        };

        var rejected = Plan(table, table).NotCompared!;

        Assert.Equal(TableDataComparisonOutcome.IncompatibleKeyTypes, rejected.Outcome);
        Assert.Contains("allows NULL", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Table_without_any_key_is_reported_and_a_user_key_can_be_selected()
    {
        var table = Table("T", Column("A"), Column("B"));

        var rejected = Plan(table, table).NotCompared!;
        Assert.Equal(TableDataComparisonOutcome.NoUniqueKey, rejected.Outcome);

        var plan = Plan(table, table, ["b", "A"]).Plan!;
        Assert.Equal(MatchingKeySource.UserSelected, plan.Key.Source);
        Assert.Equal(["B", "A"], plan.Key.OrderedColumnNames);
        Assert.Empty(plan.ValueColumns);
    }

    [Theory]
    [InlineData("Missing", "does not exist on both sides")]
    [InlineData("Payload", "cannot be used to order")]
    public void Unusable_user_keys_are_rejected(string column, string reason)
    {
        var table = Table("T", Column("A"), Column("Payload", "sql_variant"));

        var rejected = Plan(table, table, [column]).NotCompared!;

        Assert.Equal(TableDataComparisonOutcome.IncompatibleKeyTypes, rejected.Outcome);
        Assert.Contains(reason, rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Key_type_mismatch_between_sides_is_incompatible()
    {
        var rejected = Plan(Keyed(Column("Id")), Keyed(Text("Id", 10, nullable: false))).NotCompared!;

        Assert.Equal(TableDataComparisonOutcome.IncompatibleKeyTypes, rejected.Outcome);
        Assert.Contains("cannot be matched safely", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void One_sided_incompatible_computed_row_version_unsupported_and_binary_columns_are_excluded()
    {
        var source = Keyed(
            Column("Id"),
            Column("OnlySource"),
            Column("Changed"),
            Column("Doubled") with { Computed = new("([Id]*(2))", false) },
            Column("Version", "timestamp"),
            Column("Variant", "sql_variant"),
            Column("Blob", "varbinary"));
        var target = Keyed(
            Column("Id"),
            Text("Changed", 10),
            Column("Doubled") with { Computed = new("([Id]*(2))", false) },
            Column("Version", "timestamp"),
            Column("Variant", "sql_variant"),
            Column("Blob", "varbinary"),
            Column("OnlyTarget"));

        var plan = Plan(source, target, binary: false).Plan!;

        Assert.Empty(plan.ValueColumns);
        Assert.Equal(
            ["OnlySource", "Changed", "Doubled", "Version", "Variant", "Blob", "OnlyTarget"],
            plan.ExcludedColumns.Select(column => column.ColumnName));
        Assert.All(plan.ExcludedColumns, column => Assert.False(string.IsNullOrWhiteSpace(column.Reason)));
    }

    [Fact]
    public void Table_on_one_side_only_is_reported()
    {
        Assert.Equal(TableDataComparisonOutcome.TableMissing, Plan(Keyed(Column("Id")), null).NotCompared!.Outcome);
    }

    // Merging

    private static TableDataComparisonPlan SimplePlan() => Plan(Keyed(Column("Id"), Text("Name", 50)), Keyed(Column("Id"), Text("Name", 50))).Plan!;

    private static DataRow Row(long id, string? name) => new(
        [DatabaseValue.FromInt64(id, "int")],
        [name is null ? DatabaseValue.Null(ValueKind.Text, "nvarchar") : DatabaseValue.FromText(name, "nvarchar")]);

    private static async IAsyncEnumerable<IReadOnlyList<DataRow>> Batches(int batchSize, params DataRow[] rows)
    {
        foreach (var chunk in rows.Chunk(batchSize))
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private static Task<MergeResult> MergeAsync(RecordingSink sink, DataRow[] source, DataRow[] target, int batchSize = 2) =>
        new OrderedRowMerger(SimplePlan(), DefaultKeyOrdering.Instance).MergeAsync(Batches(batchSize, source), Batches(batchSize, target), sink, null, Token);

    [Fact]
    public async Task Merge_finds_source_only_target_only_modified_and_equal_rows_across_batches()
    {
        var sink = new RecordingSink();

        var result = await MergeAsync(
            sink,
            [Row(1, "a"), Row(2, "b"), Row(4, null), Row(6, "f")],
            [Row(2, "b"), Row(3, "c"), Row(4, "d"), Row(7, "g")]);

        Assert.Equal(TableDataComparisonOutcome.Compared, result.Outcome);
        Assert.Equal((1L, 2L, 2L, 1L), (result.EqualRowCount, result.SourceOnlyRowCount, result.TargetOnlyRowCount, result.ModifiedRowCount));
        Assert.Equal(
            [(1L, DifferenceStatus.SourceOnly), (3L, DifferenceStatus.TargetOnly), (4L, DifferenceStatus.Modified), (6L, DifferenceStatus.SourceOnly), (7L, DifferenceStatus.TargetOnly)],
            sink.Rows.Select(row => (row.KeyValues[0].AsInt64(), row.Status)));
        var modified = sink.Rows.Single(row => row.Status == DifferenceStatus.Modified);
        var column = Assert.Single(modified.Columns);
        Assert.Equal("Name", column.ColumnName);
        Assert.True(column.SourceValue!.IsNull);
        Assert.Equal("d", column.TargetValue!.AsString());
        Assert.NotEqual(modified.SourceFingerprint, modified.TargetFingerprint);
        Assert.Equal((4L, 4L), (result.SourceRowsRead, result.TargetRowsRead));
    }

    [Fact]
    public async Task Merge_handles_empty_sides()
    {
        var sink = new RecordingSink();

        var result = await MergeAsync(sink, [], [Row(1, "a")]);

        Assert.Equal(1, result.TargetOnlyRowCount);
        Assert.Equal(DifferenceStatus.TargetOnly, Assert.Single(sink.Rows).Status);
    }

    [Fact]
    public async Task Duplicate_keys_stop_matching_and_discard_the_table()
    {
        var sink = new RecordingSink();

        var result = await MergeAsync(sink, [Row(1, "a"), Row(2, "b"), Row(2, "c")], [Row(3, "c")]);

        Assert.Equal(TableDataComparisonOutcome.DuplicateKeysFound, result.Outcome);
        Assert.Contains("source", result.Reason, StringComparison.Ordinal);
        Assert.Equal(SimplePlan().TableId, Assert.Single(sink.Discarded));
    }

    [Fact]
    public async Task Rows_out_of_key_order_stop_matching_instead_of_producing_wrong_results()
    {
        var sink = new RecordingSink();

        var result = await MergeAsync(sink, [Row(1, "a")], [Row(1, "a"), Row(3, "c"), Row(2, "b")]);

        Assert.Equal(TableDataComparisonOutcome.KeyOrderMismatch, result.Outcome);
        Assert.Contains("target", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("3", result.Reason!.Replace("row 2", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Single(sink.Discarded);
    }

    [Fact]
    public async Task Merge_can_be_cancelled()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var merger = new OrderedRowMerger(SimplePlan(), DefaultKeyOrdering.Instance);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            merger.MergeAsync(Batches(2, Row(1, "a")), Batches(2, Row(1, "a")), new RecordingSink(), null, cancellation.Token));
    }

    [Fact]
    public async Task Merge_reports_progress_per_batch()
    {
        var reports = new List<(long, long)>();
        var merger = new OrderedRowMerger(SimplePlan(), DefaultKeyOrdering.Instance);

        await merger.MergeAsync(Batches(1, Row(1, "a"), Row(2, "b")), Batches(1, Row(1, "a")), new RecordingSink(), (s, t) => reports.Add((s, t)), Token);

        Assert.Equal((2L, 1L), reports[^1]);
        Assert.True(reports.Count >= 3);
    }

    private sealed class RecordingSink : IRowDifferenceSink
    {
        public List<RowDifference> Rows { get; } = [];

        public List<DatabaseObjectId> Discarded { get; } = [];

        public void Add(RowDifference difference) => Rows.Add(difference);

        public void Discard(DatabaseObjectId tableId) => Discarded.Add(tableId);
    }

    private sealed class TestClassifier : IValueKindClassifier
    {
        public ValueKind Classify(ColumnDefinition column) => column.SqlTypeName switch
        {
            "int" or "bigint" => ValueKind.WholeNumber,
            "nvarchar" or "varchar" => ValueKind.Text,
            "decimal" => ValueKind.ExactNumeric,
            "varbinary" => ValueKind.Binary,
            "timestamp" => ValueKind.RowVersion,
            _ => ValueKind.Unsupported,
        };
    }
}

public sealed class DataComparisonReviewTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TableDataComparisonPlan TextKeyPlan(string keyType)
    {
        var key = new ColumnDefinition("Code", keyType, 10, false, null, null, false, null, null, null, 1);
        var value = new ColumnDefinition("N", "int", null, false, null, null, false, null, null, null, 2);
        var id = new DatabaseObjectId("dbo", "T", DatabaseObjectType.Table);
        var kind = keyType == "datetimeoffset" ? ValueKind.DateTimeOffset : ValueKind.Text;
        return new TableDataComparisonPlan(
            id, id, new MatchingKey(id, ["Code"], MatchingKeySource.PrimaryKey),
            [new ComparedColumn("Code", key, key, kind)], [new ComparedColumn("N", value, value, ValueKind.WholeNumber)], []);
    }

    private static async IAsyncEnumerable<IReadOnlyList<DataRow>> One(DatabaseValue key)
    {
        await Task.Yield();
        yield return [new DataRow([key], [DatabaseValue.FromInt64(1, "int")])];
    }

    private sealed class Sink : IRowDifferenceSink
    {
        public List<RowDifference> Rows { get; } = [];

        public void Add(RowDifference difference) => Rows.Add(difference);

        public void Discard(DatabaseObjectId tableId)
        {
        }
    }

    [Fact]
    public async Task Keys_matched_by_ordering_but_stored_differently_are_reported()
    {
        var sink = new Sink();

        var result = await new OrderedRowMerger(TextKeyPlan("varchar"), DefaultKeyOrdering.Instance).MergeAsync(
            One(DatabaseValue.FromText("ABC", "varchar")), One(DatabaseValue.FromText("ABC  ", "varchar")), sink, null, Token);

        Assert.Equal((0L, 1L), (result.EqualRowCount, result.ModifiedRowCount));
        var column = Assert.Single(Assert.Single(sink.Rows).Columns);
        Assert.Equal(("Code", "ABC", "ABC  "), (column.ColumnName, column.SourceValue!.AsString(), column.TargetValue!.AsString()));
        Assert.NotEqual(sink.Rows[0].SourceFingerprint, sink.Rows[0].TargetFingerprint);
    }

    [Fact]
    public async Task Same_instant_with_a_different_offset_is_a_modified_key()
    {
        var instant = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var sink = new Sink();

        var result = await new OrderedRowMerger(TextKeyPlan("datetimeoffset"), DefaultKeyOrdering.Instance).MergeAsync(
            One(DatabaseValue.FromDateTimeOffset(instant, "datetimeoffset")),
            One(DatabaseValue.FromDateTimeOffset(instant.ToOffset(TimeSpan.FromHours(2)), "datetimeoffset")),
            sink,
            null,
            Token);

        Assert.Equal(1, result.ModifiedRowCount);
    }

    [Fact]
    public void Text_keys_order_as_if_padded_with_spaces()
    {
        Assert.Equal(0, DefaultKeyOrdering.ComparePadded("a", "a  "));
        Assert.True(DefaultKeyOrdering.ComparePadded("a\t", "a") < 0, "A tab sorts below the padding space.");
        Assert.True(DefaultKeyOrdering.ComparePadded("a", "a!") < 0);
    }

    [Fact]
    public void Provider_comparability_excludes_columns_and_rejects_keys()
    {
        var classifier = new PaddingAwareClassifier();
        var planner = new DataComparisonPlanner(classifier);
        static TableDefinition Table(string codeType, string valueType) => new(
            new DatabaseObjectId("dbo", "T", DatabaseObjectType.Table),
            [
                new ColumnDefinition("Id", "int", null, false, null, null, false, null, null, null, 1),
                new ColumnDefinition("Code", codeType, 10, false, null, null, false, null, null, null, 2),
                new ColumnDefinition("Value", valueType, 10, false, null, null, true, null, null, null, 3),
            ],
            new PrimaryKeyDefinition("PK", false, "CLUSTERED", [new IndexColumnDefinition("Id", 1, false, false)], new IndexOptions(false, 100, false, true, true, false, "NONE")),
            [], [], [], [], []);

        var plan = planner.Plan(Table("varchar", "char"), Table("varchar", "varchar"), null, true, StringComparer.Ordinal).Plan!;
        Assert.Equal(["Code"], plan.ValueColumns.Select(column => column.Name));
        Assert.Equal("Value", Assert.Single(plan.ExcludedColumns).ColumnName);

        var rejected = planner.Plan(Table("char", "varchar"), Table("varchar", "varchar"), ["Code"], true, StringComparer.Ordinal).NotCompared!;
        Assert.Equal(TableDataComparisonOutcome.IncompatibleKeyTypes, rejected.Outcome);
    }

    private sealed class PaddingAwareClassifier : IValueKindClassifier
    {
        public ValueKind Classify(ColumnDefinition column) => column.SqlTypeName == "int" ? ValueKind.WholeNumber : ValueKind.Text;

        public bool AreComparable(ColumnDefinition source, ColumnDefinition target) =>
            Classify(source) == Classify(target) && (source.SqlTypeName == "char") == (target.SqlTypeName == "char");
    }
}
