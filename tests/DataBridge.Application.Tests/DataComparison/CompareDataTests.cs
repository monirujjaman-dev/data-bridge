using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;
using DataBridge.Application.DataComparison;
using DataBridge.Application.Tests.Connections;
using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Data;
using DataBridge.Core.Schema;
using DataBridge.Shared.Results;

namespace DataBridge.Application.Tests.DataComparison;

public sealed class CompareDataTests
{
    private readonly ConnectionProfileFixture _profiles = new();
    private readonly FakeWorld _world = new();
    private readonly CompareData _compare;
    private readonly ListDataComparisonTables _list;

    public CompareDataTests()
    {
        var setup = new ComparisonSetup(new SavedConnectionRequests(_profiles.Repository, _profiles.Secrets), _world);
        _compare = new CompareData(setup, _world, _profiles.Clock, _profiles.Log.For<CompareData>());
        _list = new ListDataComparisonTables(setup, _world);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Compares_selected_tables_and_streams_differences_to_the_sink()
    {
        var (source, target) = await ProfilesAsync();
        _world.Table("Source", "Orders", (1, "a"), (2, "b"));
        _world.Table("Target", "Orders", (1, "a"), (2, "B"), (3, "c"));
        _world.Table("Source", "Other", (1, "x"));
        _world.Table("Target", "Other", (1, "x"));
        var sink = new ListSink();

        var result = await _compare.ExecuteAsync(Request(source, target, ["Orders"]), sink, null, Token);

        var summary = Assert.Single(result.Value.Tables);
        Assert.Equal(TableDataComparisonOutcome.Compared, summary.Outcome);
        Assert.Equal((1L, 0L, 1L, 1L), (summary.EqualRowCount, summary.SourceOnlyRowCount, summary.TargetOnlyRowCount, summary.ModifiedRowCount));
        Assert.Equal(ReadConsistencyMode.Snapshot, summary.SourceConsistency);
        Assert.Equal(2, sink.Rows.Count);
        Assert.False(result.Value.IsSameDatabase);
    }

    [Fact]
    public async Task All_tables_on_either_side_are_compared_when_none_are_selected()
    {
        var (source, target) = await ProfilesAsync();
        _world.Table("Source", "Both", (1, "a"));
        _world.Table("Target", "Both", (1, "a"));
        _world.Table("Source", "SourceOnly", (1, "a"));
        _world.Table("Target", "TargetOnly", (1, "a"));

        var result = await _compare.ExecuteAsync(Request(source, target, []), new ListSink(), null, Token);

        Assert.Equal(
            ["Both Compared", "SourceOnly TableMissing", "TargetOnly TableMissing"],
            result.Value.Tables.Select(table => $"{table.TableId.ObjectName} {table.Outcome}").Order());
    }

    [Fact]
    public async Task A_failing_table_is_reported_and_the_others_continue()
    {
        var (source, target) = await ProfilesAsync();
        _world.Table("Source", "Good", (1, "a"));
        _world.Table("Target", "Good", (1, "a"));
        _world.Table("Source", "Bad", (1, "a"));
        _world.Table("Target", "Bad", (1, "a"));
        _world.FailingTables.Add("Bad");
        var sink = new ListSink();

        var result = await _compare.ExecuteAsync(Request(source, target, []), sink, null, Token);

        var bad = result.Value.Tables.Single(table => table.TableId.ObjectName == "Bad");
        Assert.Equal(TableDataComparisonOutcome.Failed, bad.Outcome);
        Assert.Equal("The server returned error 208 while reading the table.", bad.Reason);
        Assert.Contains(bad.TableId, sink.Discarded);
        Assert.Equal(TableDataComparisonOutcome.Compared, result.Value.Tables.Single(table => table.TableId.ObjectName == "Good").Outcome);
    }

    [Fact]
    public async Task A_side_that_cannot_open_fails_its_tables_and_releases_the_other_side()
    {
        var (source, target) = await ProfilesAsync();
        _world.Table("Source", "Orders", (1, "a"));
        _world.Table("Target", "Orders", (1, "a"));
        _world.FailOpenDatabases.Add("Target");

        var result = await _compare.ExecuteAsync(Request(source, target, []), new ListSink(), null, Token);

        var summary = Assert.Single(result.Value.Tables);
        Assert.Equal(TableDataComparisonOutcome.Failed, summary.Outcome);
        Assert.Equal("Could not start reading: The read session could not be started.", summary.Reason);
        Assert.Equal(0, _world.OpenReaders);
    }

    [Fact]
    public async Task Invalid_options_are_rejected_before_connecting()
    {
        var (source, target) = await ProfilesAsync();

        var result = await _compare.ExecuteAsync(
            Request(source, target, []) with { Options = new DataComparisonOptions(BatchSize: 0, MaxDegreeOfParallelism: 99) },
            new ListSink(),
            null,
            Token);

        Assert.Equal([DataComparisonErrors.BatchSizeOutOfRange, DataComparisonErrors.ParallelismOutOfRange], result.Errors);
        Assert.Equal(0, _world.Connections);
    }

    [Fact]
    public async Task User_keys_are_applied_per_table()
    {
        var (source, target) = await ProfilesAsync();
        _world.Table("Source", "Keyless", (1, "a"));
        _world.Table("Target", "Keyless", (1, "a"));
        _world.KeylessTables.Add("Keyless");
        var keyless = new DatabaseObjectId("dbo", "Keyless", DatabaseObjectType.Table);

        var withoutKey = await _compare.ExecuteAsync(Request(source, target, []), new ListSink(), null, Token);
        var withKey = await _compare.ExecuteAsync(
            Request(source, target, []) with { UserKeys = new Dictionary<DatabaseObjectId, IReadOnlyList<string>> { [keyless] = ["Id"] } },
            new ListSink(),
            null,
            Token);

        Assert.Equal(TableDataComparisonOutcome.NoUniqueKey, Assert.Single(withoutKey.Value.Tables).Outcome);
        var summary = Assert.Single(withKey.Value.Tables);
        Assert.Equal((TableDataComparisonOutcome.Compared, MatchingKeySource.UserSelected), (summary.Outcome, summary.Key!.Source));
    }

    [Fact]
    public async Task Listing_tables_shows_keys_problems_and_candidate_key_columns()
    {
        var (source, target) = await ProfilesAsync();
        _world.Table("Source", "Keyed", (1, "a"));
        _world.Table("Target", "Keyed", (1, "a"));
        _world.Table("Source", "Keyless", (1, "a"));
        _world.Table("Target", "Keyless", (1, "a"));
        _world.KeylessTables.Add("Keyless");

        var tables = (await _list.ExecuteAsync(source, target, Token)).Value.Tables;

        var keyed = tables.Single(table => table.TableId.ObjectName == "Keyed");
        Assert.Equal(MatchingKeySource.PrimaryKey, keyed.Key!.Source);
        var keylessTable = tables.Single(table => table.TableId.ObjectName == "Keyless");
        Assert.Equal(TableDataComparisonOutcome.NoUniqueKey, keylessTable.Problem);
        Assert.Equal(["Id"], keylessTable.CandidateKeyColumns);
        Assert.Equal(0, _world.Connections);
    }

    [Fact]
    public async Task Cancellation_stops_the_comparison_and_is_logged()
    {
        var (source, target) = await ProfilesAsync();
        _world.Table("Source", "Orders", (1, "a"), (2, "b"));
        _world.Table("Target", "Orders", (1, "a"), (2, "b"));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        _world.OnBatch = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _compare.ExecuteAsync(Request(source, target, []), new ListSink(), null, cancellation.Token));

        Assert.Contains(_profiles.Log.Entries, entry => entry.Message == "Data comparison cancelled");
        Assert.Equal(0, _world.OpenReaders);
    }

    [Fact]
    public async Task Logs_carry_counts_but_no_names_or_row_values()
    {
        var (source, target) = await ProfilesAsync();
        _world.Table("Source", "SecretTable", (1, "SecretValue"));
        _world.Table("Target", "SecretTable", (1, "OtherSecret"));

        await _compare.ExecuteAsync(Request(source, target, []), new ListSink(), null, Token);

        var logged = string.Join("\n", _profiles.Log.Entries.Select(entry => entry.AllText));
        Assert.Contains("Data comparison completed", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", logged, StringComparison.Ordinal);
    }

    private async Task<(Guid Source, Guid Target)> ProfilesAsync() =>
        ((await _profiles.CreateSqlProfileAsync("Source")).Id, (await _profiles.CreateSqlProfileAsync("Target")).Id);

    private static DataComparisonRequest Request(Guid source, Guid target, string[] tables) => new(
        source,
        target,
        [.. tables.Select(name => new DatabaseObjectId("dbo", name, DatabaseObjectType.Table))],
        new Dictionary<DatabaseObjectId, IReadOnlyList<string>>(),
        new DataComparisonOptions(BatchSize: 1, MaxDegreeOfParallelism: 2));

    private sealed class ListSink : IRowDifferenceSink
    {
        public ConcurrentQueue<RowDifference> Queue { get; } = new();

        public ConcurrentBag<DatabaseObjectId> Discarded { get; } = [];

        public List<RowDifference> Rows => [.. Queue];

        public void Add(RowDifference difference) => Queue.Enqueue(difference);

        public void Discard(DatabaseObjectId tableId) => Discarded.Add(tableId);
    }

    /// <summary>Fake provider, schema reader and data reader over in-memory tables keyed by the profile name.</summary>
    private sealed class FakeWorld : IDatabaseProviderFactory, IDatabaseProvider, ISchemaReader, IDataReaderProvider, IValueKindClassifier
    {
        private readonly Dictionary<string, Dictionary<string, (long Id, string Name)[]>> _tables = [];
        private int _connections;
        private int _openReaders;

        public HashSet<string> FailingTables { get; } = [];

        public HashSet<string> KeylessTables { get; } = [];

        public HashSet<string> FailOpenDatabases { get; } = [];

        public Action? OnBatch { get; set; }

        public int Connections => _connections;

        public int OpenReaders => _openReaders;

        public DatabaseProviderType ProviderType => DatabaseProviderType.SqlServer;

        public ISqlDefinitionNormalizer DefinitionNormalizer => ExactSqlDefinitionNormalizer.Instance;

        public IKeyOrdering KeyOrdering => DefaultKeyOrdering.Instance;

        public IValueKindClassifier Classifier => this;

        public void Table(string database, string name, params (long Id, string Name)[] rows)
        {
            if (!_tables.TryGetValue(database, out var tables))
            {
                _tables[database] = tables = [];
            }

            tables[name] = rows;
        }

        public IDatabaseProvider GetProvider(DatabaseProviderType providerType) => this;

        public ISchemaReader GetSchemaReader(DatabaseProviderType providerType) => this;

        public IDataReaderProvider GetDataReader(DatabaseProviderType providerType) => this;

        public ISqlGenerator GetSqlGenerator(DatabaseProviderType providerType) => throw new NotSupportedException();

        public ISynchronizationTargetInspector GetTargetInspector(DatabaseProviderType providerType) => throw new NotSupportedException();

        public ValueKind Classify(ColumnDefinition column) => column.SqlTypeName == "int" ? ValueKind.WholeNumber : ValueKind.Text;

        public Task<Result<ResolvedDatabaseIdentity>> ResolveIdentityAsync(DatabaseConnectionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(ResolvedDatabaseIdentity.Create(
                DatabaseProviderType.SqlServer, "DB01", request.Profile.Name, request.Profile.Name.Length, "SQL Server 2022", null)));

        public Task<Result<DatabaseSchema>> ReadSchemaAsync(DatabaseConnectionRequest request, CancellationToken cancellationToken)
        {
            var tables = _tables.GetValueOrDefault(request.Profile.Name, []).Keys.Select(name =>
            {
                var id = new DatabaseObjectId("dbo", name, DatabaseObjectType.Table);
                ColumnDefinition[] columns =
                [
                    new("Id", "int", null, false, null, null, false, null, null, null, 1),
                    new("Name", "nvarchar", 50, false, null, null, true, null, null, null, 2),
                ];
                var key = KeylessTables.Contains(name)
                    ? null
                    : new PrimaryKeyDefinition("PK", false, "CLUSTERED", [new IndexColumnDefinition("Id", 1, false, false)], new IndexOptions(false, 100, false, true, true, false, "NONE"));
                return new TableDefinition(id, columns, key, [], [], [], [], []);
            });
            return Task.FromResult(Result.Success(new DatabaseSchema(
                request.Profile.Name, [new SchemaDefinition("dbo", [.. tables], [], [], [])], [], false, true, DateTimeOffset.UnixEpoch)));
        }

        public Task<Result<ITableRowReader>> OpenAsync(DatabaseConnectionRequest request, ReadConsistencyMode preferredConsistency, CancellationToken cancellationToken)
        {
            if (FailOpenDatabases.Contains(request.Profile.Name))
            {
                throw new DataReadException("The read session could not be started.");
            }

            Interlocked.Increment(ref _connections);
            Interlocked.Increment(ref _openReaders);
            return Task.FromResult(Result.Success<ITableRowReader>(new Reader(this, request.Profile.Name, preferredConsistency)));
        }

        private sealed class Reader(FakeWorld world, string database, ReadConsistencyMode consistency) : ITableRowReader
        {
            public ReadConsistencyMode AchievedConsistency => consistency;

            public async IAsyncEnumerable<IReadOnlyList<DataRow>> ReadRowsAsync(
                TableReadRequest request,
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                if (world.FailingTables.Contains(request.TableId.ObjectName))
                {
                    throw new DataReadException("The server returned error 208 while reading the table.");
                }

                foreach (var (id, name) in world._tables[database][request.TableId.ObjectName])
                {
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                    world.OnBatch?.Invoke();
                    yield return [new DataRow([DatabaseValue.FromInt64(id, "int")], [DatabaseValue.FromText(name, "nvarchar")])];
                }
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Decrement(ref world._openReaders);
                return ValueTask.CompletedTask;
            }
        }
    }
}
