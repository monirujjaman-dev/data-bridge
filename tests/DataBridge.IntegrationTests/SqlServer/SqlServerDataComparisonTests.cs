using DataBridge.Application.Abstractions;
using DataBridge.Core.Comparison;
using DataBridge.Core.Schema;
using DataBridge.Infrastructure.Providers.SqlServer.Data;
using DataBridge.Infrastructure.Providers.SqlServer.Schema;

namespace DataBridge.IntegrationTests.SqlServer;

/// <summary>
/// Phase 4 acceptance against a real SQL Server: missing and modified rows with composite keys (AC-05), typed values
/// (AC-06), string keys under differing collations (AC-16), keyless and duplicate-key tables, consistency, read-only
/// behavior and cancellation.
/// </summary>
public sealed class SqlServerDataComparisonTests(SqlServerFixture server)
{
    private readonly SqlServerSchemaReader _schemas = new(TimeProvider.System);
    private readonly SqlServerDataReaderProvider _data = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Source_only_target_only_and_modified_rows_are_found_with_a_composite_key()
    {
        server.RequireServer();
        const string table = "CREATE TABLE dbo.Lines (OrderId int NOT NULL, LineNumber smallint NOT NULL, Qty int NOT NULL, Note nvarchar(50) NULL, PRIMARY KEY (OrderId, LineNumber));";
        var source = await CreateAsync(table + """
            INSERT dbo.Lines VALUES (1, 1, 5, N'a'), (1, 2, 6, NULL), (2, 1, 7, N'c'), (3, 1, 8, N'd');
            """);
        var target = await CreateAsync(table + """
            INSERT dbo.Lines VALUES (1, 1, 5, N'a'), (1, 2, 6, N'b'), (2, 2, 7, N'c'), (3, 1, 9, N'd');
            """);

        var (result, sink) = await CompareAsync(source, target, "Lines", batchSize: 2);

        Assert.Equal(TableDataComparisonOutcome.Compared, result.Outcome);
        Assert.Equal((1L, 1L, 1L, 2L), (result.EqualRowCount, result.SourceOnlyRowCount, result.TargetOnlyRowCount, result.ModifiedRowCount));
        Assert.Equal(
            ["1/2 Modified Note", "2/1 SourceOnly", "2/2 TargetOnly", "3/1 Modified Qty"],
            sink.Rows.Select(row => $"{row.KeyValues[0].AsInt64()}/{row.KeyValues[1].AsInt64()} {row.Status}"
                + (row.Status == DifferenceStatus.Modified ? " " + string.Join(",", row.Columns.Select(column => column.ColumnName)) : "")));
    }

    [Fact]
    public async Task Typed_values_compare_exactly()
    {
        server.RequireServer();
        const string table = """
            CREATE TABLE dbo.Values_ (
                Id int NOT NULL PRIMARY KEY,
                Big decimal(38, 10) NULL, Cash money NULL, Approx float NULL,
                Stamp datetime2(7) NULL, OffsetAt datetimeoffset(7) NULL, Clock time(7) NULL, Day date NULL, Legacy datetime NULL,
                Uni nvarchar(50) NULL, Ansi varchar(20) NULL, Bytes varbinary(20) NULL, Id2 uniqueidentifier NULL, Flag bit NULL, Doc xml NULL);
            """;
        const string row = """
            (1, 1234567890123456789012345678.0123456789, 922337203685477.5807, 0.1, '2026-01-01T12:00:00.1234567',
             '2026-01-01T12:00:00+00:00', '23:59:59.9999999', '2026-01-01', '2026-01-01T12:00:00.003', N'Grüße 😀', 'trail ', 0x0102,
             '6F9619FF-8B86-D011-B42D-00C04FC964FF', 1, N'<a>1</a>')
            """;
        var source = await CreateAsync(table + $"INSERT dbo.Values_ VALUES {row}, (2, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);");
        var target = await CreateAsync(table + $"INSERT dbo.Values_ VALUES {row}, (2, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);" + """
            INSERT dbo.Values_ SELECT 3, Big, Cash, Approx, Stamp, OffsetAt, Clock, Day, Legacy, Uni, Ansi, Bytes, Id2, Flag, Doc FROM dbo.Values_ WHERE Id = 1;
            """);
        await server.ExecuteInAsync(source, """
            INSERT dbo.Values_ (Id, Big, Cash, Approx, Stamp, OffsetAt, Clock, Day, Legacy, Uni, Ansi, Bytes, Id2, Flag, Doc)
            VALUES (3, 1234567890123456789012345678.0123456788, 922337203685477.5806, 0.1000000001, '2026-01-01T12:00:00.1234568',
                    '2026-01-01T14:00:00+02:00', '23:59:59.9999998', '2026-01-02', '2026-01-01T12:00:00.007', N'Gruße 😀', 'trail', 0x010200,
                    '6F9619FF-8B86-D011-B42D-00C04FC964FE', 0, N'<a>2</a>');
            """, Token);

        var (result, sink) = await CompareAsync(source, target, "Values_");

        Assert.Equal((2L, 1L), (result.EqualRowCount, result.ModifiedRowCount));
        var changed = Assert.Single(sink.Rows).Columns.Select(column => column.ColumnName).ToArray();
        Assert.Equal(["Big", "Cash", "Approx", "Stamp", "OffsetAt", "Clock", "Day", "Legacy", "Uni", "Ansi", "Bytes", "Id2", "Flag", "Doc"], changed);
    }

    [Fact]
    public async Task String_keys_match_exactly_under_differing_and_case_insensitive_collations()
    {
        server.RequireServer();
        const string keys = """
            INSERT dbo.Codes VALUES (N'a', 1), (N'A', 2), (N'b', 3), (N'é', 4), (N'e', 5), (N'Z', 6), (NCHAR(223), 7),
                                    (N'_', 9), (N'-', 10), (N'10', 11), (N'9', 12), (N'😀', 13), (NCHAR(255), 14);
            """;
        var source = await CreateWithCollationAsync("Latin1_General_100_CS_AS", "CREATE TABLE dbo.Codes (Code nvarchar(10) NOT NULL PRIMARY KEY, Value int NOT NULL);" + keys);
        var target = await CreateWithCollationAsync("Turkish_100_CS_AS", "CREATE TABLE dbo.Codes (Code nvarchar(10) NOT NULL PRIMARY KEY, Value int NOT NULL);" + keys);

        var (result, sink) = await CompareAsync(source, target, "Codes", batchSize: 3);

        Assert.Equal(TableDataComparisonOutcome.Compared, result.Outcome);
        Assert.Equal(13, result.EqualRowCount);
        Assert.Empty(sink.Rows);
    }

    [Fact]
    public async Task Varchar_keys_in_a_case_insensitive_database_differing_only_by_case_are_separate_rows()
    {
        server.RequireServer();
        var source = await CreateWithCollationAsync("Latin1_General_CS_AS", "CREATE TABLE dbo.Names (Name varchar(20) NOT NULL PRIMARY KEY, N int NOT NULL); INSERT dbo.Names VALUES ('abc', 1), ('Abc', 2), ('café', 3);");
        var target = await CreateWithCollationAsync("SQL_Latin1_General_CP1_CI_AS", "CREATE TABLE dbo.Names (Name varchar(20) NOT NULL PRIMARY KEY, N int NOT NULL); INSERT dbo.Names VALUES ('ABC', 1), ('café', 3);");

        var (result, sink) = await CompareAsync(source, target, "Names");

        Assert.Equal(TableDataComparisonOutcome.Compared, result.Outcome);
        Assert.Equal((1L, 2L, 1L), (result.EqualRowCount, result.SourceOnlyRowCount, result.TargetOnlyRowCount));
        Assert.Equal(["Abc", "abc"], sink.Rows.Where(row => row.Status == DifferenceStatus.SourceOnly).Select(row => row.KeyValues[0].AsString()));
    }

    [Fact]
    public async Task Uniqueidentifier_keys_follow_sql_server_order()
    {
        server.RequireServer();
        const string script = """
            CREATE TABLE dbo.Things (Id uniqueidentifier NOT NULL PRIMARY KEY, N int NOT NULL);
            INSERT dbo.Things SELECT TOP (2000) NEWID(), ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM sys.all_columns a CROSS JOIN sys.all_columns b;
            """;
        var source = await CreateAsync(script);
        var target = await server.CreateDatabaseAsync(Token);
        await server.ExecuteInAsync(target, "CREATE TABLE dbo.Things (Id uniqueidentifier NOT NULL PRIMARY KEY, N int NOT NULL);", Token);
        await server.ExecuteInAsync(target, $"INSERT dbo.Things SELECT Id, N FROM [{source}].dbo.Things;", Token);

        var (result, _) = await CompareAsync(source, target, "Things", batchSize: 128);

        Assert.Equal(TableDataComparisonOutcome.Compared, result.Outcome);
        Assert.Equal(2000, result.EqualRowCount);
    }

    [Fact]
    public async Task Tables_without_a_key_and_duplicate_user_keys_are_not_matched_automatically()
    {
        server.RequireServer();
        const string script = "CREATE TABLE dbo.Heap (A int NOT NULL, B int NOT NULL); INSERT dbo.Heap VALUES (1, 1), (1, 2);";
        var source = await CreateAsync(script);
        var target = await CreateAsync(script);

        var noKey = await PlanAsync(source, target, "Heap", null);
        Assert.Equal(TableDataComparisonOutcome.NoUniqueKey, noKey.NotCompared!.Outcome);

        var (result, sink) = await CompareAsync(source, target, "Heap", userKey: ["A"]);
        Assert.Equal(TableDataComparisonOutcome.DuplicateKeysFound, result.Outcome);
        Assert.Contains(PlanTableId("Heap"), sink.Discarded);

        var (unique, _) = await CompareAsync(source, target, "Heap", userKey: ["A", "B"]);
        Assert.Equal((TableDataComparisonOutcome.Compared, 2L), (unique.Outcome, unique.EqualRowCount));
    }

    [Fact]
    public async Task Snapshot_is_used_only_when_the_database_already_allows_it()
    {
        server.RequireServer();
        var plain = await CreateAsync("SELECT 1;");
        var snapshot = await CreateAsync("SELECT 1;");
        await server.ExecuteAsync($"ALTER DATABASE [{snapshot}] SET ALLOW_SNAPSHOT_ISOLATION ON;", Token);

        await using var plainReader = (await _data.OpenAsync(server.Request(plain), ReadConsistencyMode.Snapshot, Token)).Value;
        await using var snapshotReader = (await _data.OpenAsync(server.Request(snapshot), ReadConsistencyMode.Snapshot, Token)).Value;
        await using var committedReader = (await _data.OpenAsync(server.Request(snapshot), ReadConsistencyMode.ReadCommitted, Token)).Value;

        Assert.Equal(ReadConsistencyMode.ReadCommitted, plainReader.AchievedConsistency);
        Assert.Equal(ReadConsistencyMode.Snapshot, snapshotReader.AchievedConsistency);
        Assert.Equal(ReadConsistencyMode.ReadCommitted, committedReader.AchievedConsistency);
    }

    [Fact]
    public async Task Comparing_data_does_not_modify_either_database()
    {
        server.RequireServer();
        const string script = "CREATE TABLE dbo.T (Id int NOT NULL PRIMARY KEY, V nvarchar(10) NULL); INSERT dbo.T VALUES (1, N'x'), (2, N'y');";
        var source = await CreateAsync(script);
        var target = await CreateAsync(script);
        const string state = """
            SELECT (SELECT COUNT(*) FROM dbo.T), (SELECT CHECKSUM_AGG(CHECKSUM(Id, V)) FROM dbo.T),
                   (SELECT MAX(modify_date) FROM sys.objects), (SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID());
            """;
        var before = await server.QueryRowAsync(target, state, Token);

        await CompareAsync(source, target, "T");

        Assert.Equal(before, await server.QueryRowAsync(target, state, Token));
    }

    [Fact]
    public async Task Reading_can_be_cancelled_mid_stream()
    {
        server.RequireServer();
        var database = await CreateAsync("""
            CREATE TABLE dbo.Many (Id int NOT NULL PRIMARY KEY);
            INSERT dbo.Many SELECT TOP (20000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM sys.all_columns a CROSS JOIN sys.all_columns b;
            """);
        var plan = (await PlanAsync(database, database, "Many", null)).Plan!;
        await using var reader = (await _data.OpenAsync(server.Request(database), ReadConsistencyMode.ReadCommitted, Token)).Value;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var batches = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in reader.ReadRowsAsync(Request(plan, side: 0, batchSize: 100), cancellation.Token))
            {
                if (++batches == 3)
                {
                    await cancellation.CancelAsync();
                }
            }
        });
        Assert.Equal(3, batches);
    }

    [Fact]
    public async Task Missing_table_on_the_server_is_a_read_failure_without_row_values()
    {
        server.RequireServer();
        var database = await CreateAsync("CREATE TABLE dbo.Gone (Id int NOT NULL PRIMARY KEY, Secret nvarchar(20) NOT NULL); INSERT dbo.Gone VALUES (1, N'TopSecretValue');");
        var plan = (await PlanAsync(database, database, "Gone", null)).Plan!;
        await server.ExecuteInAsync(database, "DROP TABLE dbo.Gone;", Token);
        await using var reader = (await _data.OpenAsync(server.Request(database), ReadConsistencyMode.ReadCommitted, Token)).Value;

        var failure = await Assert.ThrowsAsync<DataReadException>(async () =>
        {
            await foreach (var _ in reader.ReadRowsAsync(Request(plan, side: 0, batchSize: 10), Token))
            {
            }
        });

        Assert.Equal("The server returned error 208 while reading the table.", failure.Message);
    }

    [Fact]
    public async Task Columns_whose_types_store_values_differently_are_excluded()
    {
        server.RequireServer();
        var source = await CreateAsync("CREATE TABLE dbo.Mixed (Id int NOT NULL PRIMARY KEY, Code char(10) NULL, Moment datetime NULL, Ratio real NULL, Label varchar(20) NULL); INSERT dbo.Mixed VALUES (1, 'abc', '2026-01-01T00:00:00.003', 0.1, 'x');");
        var target = await CreateAsync("CREATE TABLE dbo.Mixed (Id int NOT NULL PRIMARY KEY, Code varchar(10) NULL, Moment datetime2(7) NULL, Ratio float NULL, Label nvarchar(20) NULL); INSERT dbo.Mixed VALUES (1, 'abc', '2026-01-01T00:00:00.003', 0.1, N'x');");

        var plan = (await PlanAsync(source, target, "Mixed", null)).Plan!;
        var (result, _) = await CompareAsync(source, target, "Mixed");

        Assert.Equal(["Code", "Moment", "Ratio"], plan.ExcludedColumns.Select(column => column.ColumnName));
        Assert.Equal(["Label"], plan.ValueColumns.Select(column => column.Name));
        Assert.Equal(1, result.EqualRowCount);
    }

    [Fact]
    public async Task Keys_equal_to_sql_server_but_stored_differently_are_reported_as_modified()
    {
        server.RequireServer();
        var source = await CreateAsync("CREATE TABLE dbo.Codes (Code varchar(10) NOT NULL PRIMARY KEY, N int NOT NULL); INSERT dbo.Codes VALUES ('ABC', 1);");
        var target = await CreateAsync("CREATE TABLE dbo.Codes (Code varchar(10) NOT NULL PRIMARY KEY, N int NOT NULL); INSERT dbo.Codes VALUES ('ABC  ', 1);");

        var (result, sink) = await CompareAsync(source, target, "Codes");

        Assert.Equal((0L, 1L), (result.EqualRowCount, result.ModifiedRowCount));
        Assert.Equal("Code", Assert.Single(Assert.Single(sink.Rows).Columns).ColumnName);
    }

    [Fact]
    public async Task Stopping_early_returns_promptly()
    {
        server.RequireServer();
        var database = await CreateAsync("""
            CREATE TABLE dbo.Wide (Id int NOT NULL PRIMARY KEY, Payload nvarchar(2000) NOT NULL);
            INSERT dbo.Wide SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), REPLICATE(N'x', 2000)
            FROM sys.all_columns a CROSS JOIN sys.all_columns b;
            """);
        var plan = (await PlanAsync(database, database, "Wide", null)).Plan!;
        await using var reader = (await _data.OpenAsync(server.Request(database), ReadConsistencyMode.ReadCommitted, Token)).Value;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var _ in reader.ReadRowsAsync(Request(plan, side: 0, batchSize: 10), Token))
        {
            break;
        }

        // A smoke test: about 400 MB remain on the server. It does not prove the cancel path, because a local server
        // drains this quickly; the reader cancels the command so a remote or much larger table is not drained.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Stopping took {stopwatch.Elapsed}.");
    }

    private static DatabaseObjectId PlanTableId(string name) => new("dbo", name, DatabaseObjectType.Table);

    private async Task<string> CreateAsync(string script)
    {
        var database = await server.CreateDatabaseAsync(Token);
        await server.ExecuteInAsync(database, script, Token);
        return database;
    }

    private async Task<string> CreateWithCollationAsync(string collation, string script)
    {
        var database = $"DataBridgeTest_{Guid.NewGuid():N}";
        await server.ExecuteAsync($"CREATE DATABASE [{database}] COLLATE {collation};", Token);
        await server.ExecuteInAsync(database, script, Token);
        return database;
    }

    private async Task<TablePlanResult> PlanAsync(string source, string target, string table, IReadOnlyList<string>? userKey)
    {
        var sourceSchema = (await _schemas.ReadSchemaAsync(server.Request(source), Token)).Value;
        var targetSchema = (await _schemas.ReadSchemaAsync(server.Request(target), Token)).Value;
        var names = sourceSchema.IdentifiersAreCaseSensitive || targetSchema.IdentifiersAreCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        static TableDefinition? Find(DatabaseSchema schema, string name) =>
            schema.Schemas.SelectMany(item => item.Tables).SingleOrDefault(item => item.Id.ObjectName == name);
        return new DataComparisonPlanner(_data.Classifier).Plan(Find(sourceSchema, table), Find(targetSchema, table), userKey, true, names);
    }

    private async Task<(MergeResult Result, RecordingSink Sink)> CompareAsync(
        string source,
        string target,
        string table,
        int batchSize = 1000,
        IReadOnlyList<string>? userKey = null)
    {
        var plan = await PlanAsync(source, target, table, userKey);
        Assert.True(plan.Plan is not null, plan.NotCompared?.Reason);
        await using var sourceReader = (await _data.OpenAsync(server.Request(source), ReadConsistencyMode.Snapshot, Token)).Value;
        await using var targetReader = (await _data.OpenAsync(server.Request(target), ReadConsistencyMode.Snapshot, Token)).Value;
        var sink = new RecordingSink();
        var result = await new OrderedRowMerger(plan.Plan!, _data.KeyOrdering).MergeAsync(
            sourceReader.ReadRowsAsync(Request(plan.Plan!, 0, batchSize), Token),
            targetReader.ReadRowsAsync(Request(plan.Plan!, 1, batchSize), Token),
            sink,
            null,
            Token);
        return (result, sink);
    }

    private static TableReadRequest Request(TableDataComparisonPlan plan, int side, int batchSize) => new(
        side == 0 ? plan.TableId : plan.TargetTableId,
        [.. plan.KeyColumns.Select(column => new ReadColumn(side == 0 ? column.Source : column.Target, column.Kind))],
        [.. plan.ValueColumns.Select(column => new ReadColumn(side == 0 ? column.Source : column.Target, column.Kind))],
        batchSize);

    private sealed class RecordingSink : IRowDifferenceSink
    {
        public List<RowDifference> Rows { get; } = [];

        public List<DatabaseObjectId> Discarded { get; } = [];

        public void Add(RowDifference difference) => Rows.Add(difference);

        public void Discard(DatabaseObjectId tableId) => Discarded.Add(tableId);
    }
}
