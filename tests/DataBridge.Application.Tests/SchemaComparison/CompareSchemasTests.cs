using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;
using DataBridge.Application.SchemaComparison;
using DataBridge.Application.Tests.Connections;
using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Schema;
using DataBridge.Shared.Results;

namespace DataBridge.Application.Tests.SchemaComparison;

public sealed class CompareSchemasTests
{
    private readonly ConnectionProfileFixture _profiles = new();
    private readonly FakeProvider _provider = new();
    private readonly FakeSchemaReader _reader = new();
    private readonly CompareSchemas _compare;

    public CompareSchemasTests() =>
        _compare = new CompareSchemas(
            new SavedConnectionRequests(_profiles.Repository, _profiles.Secrets),
            new FakeFactory(_provider, _reader),
            _profiles.Clock,
            _profiles.Log.For<CompareSchemas>());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Compares_the_schemas_of_two_profiles_and_reports_progress()
    {
        var source = await _profiles.CreateSqlProfileAsync("Source");
        var target = await _profiles.CreateSqlProfileAsync("Target");
        _reader.Schemas[source.Id] = Schema(Table("Orders"), Table("Customers"));
        _reader.Schemas[target.Id] = Schema(Table("Orders"));
        var stages = new List<ComparisonStatus>();

        var result = await _compare.ExecuteAsync(source.Id, target.Id, new SynchronousProgress(stages), Token);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        var report = result.Value;
        Assert.Equal(DifferenceStatus.SourceOnly, Assert.Single(report.Result.Differences, item => item.ObjectId.ObjectName == "Customers").Status);
        Assert.Equal("Source", report.Source.DatabaseName);
        Assert.Equal("Target", report.Target.DatabaseName);
        Assert.False(report.IsSameDatabase);
        Assert.Equal([ComparisonStatus.Connecting, ComparisonStatus.ReadingMetadata, ComparisonStatus.ComparingSchema], stages);
        Assert.Equal("s3cr3t!", _reader.Requests.Single(request => request.Profile.Id == source.Id).Password!.Reveal());
    }

    [Fact]
    public async Task Same_profile_on_both_sides_is_rejected_without_connecting()
    {
        var profile = await _profiles.CreateSqlProfileAsync();

        var result = await _compare.ExecuteAsync(profile.Id, profile.Id, null, Token);

        Assert.Equal(SchemaComparisonErrors.SameProfile, Assert.Single(result.Errors));
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task Same_database_behind_two_profiles_is_flagged()
    {
        var source = await _profiles.CreateSqlProfileAsync("Source");
        var target = await _profiles.CreateSqlProfileAsync("Target");
        _provider.DatabaseNameOverride = "Shared";

        var report = (await _compare.ExecuteAsync(source.Id, target.Id, null, Token)).Value;

        Assert.True(report.IsSameDatabase);
    }

    [Fact]
    public async Task Missing_profile_and_password_are_reported_per_side()
    {
        var source = await _profiles.CreateSqlProfileAsync("Source", password: null);

        var result = await _compare.ExecuteAsync(source.Id, Guid.NewGuid(), null, Token);

        Assert.Equal(
            [ConnectionErrors.PasswordRequired.Code, ConnectionProfileErrors.NotFound.Code],
            result.Errors.Select(error => error.Code));
        Assert.StartsWith("Source: No password is saved", result.Errors[0].Message, StringComparison.Ordinal);
        Assert.StartsWith("Target: ", result.Errors[1].Message, StringComparison.Ordinal);
        Assert.Empty(_reader.Requests);
    }

    [Fact]
    public async Task Connection_failure_stops_before_reading_metadata()
    {
        var source = await _profiles.CreateSqlProfileAsync("Source");
        var target = await _profiles.CreateSqlProfileAsync("Target");
        _provider.Failures[target.Id] = ConnectionErrors.LoginFailed;

        var result = await _compare.ExecuteAsync(source.Id, target.Id, null, Token);

        var error = Assert.Single(result.Errors);
        Assert.Equal(ConnectionErrors.LoginFailed.Code, error.Code);
        Assert.Equal($"Target: {ConnectionErrors.LoginFailed.Message}", error.Message);
        Assert.Empty(_reader.Requests);
    }

    [Fact]
    public async Task Schema_read_failure_is_returned()
    {
        var source = await _profiles.CreateSqlProfileAsync("Source");
        var target = await _profiles.CreateSqlProfileAsync("Target");
        _reader.Failures[source.Id] = SchemaComparisonErrors.MetadataChanged;

        var result = await _compare.ExecuteAsync(source.Id, target.Id, null, Token);

        Assert.Equal(SchemaComparisonErrors.MetadataChanged.Code, Assert.Single(result.Errors).Code);
        Assert.Contains(_profiles.Log.Entries, entry => entry.Message.Contains("SchemaComparison.MetadataChanged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_propagates_and_is_logged()
    {
        var source = await _profiles.CreateSqlProfileAsync("Source");
        var target = await _profiles.CreateSqlProfileAsync("Target");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        _reader.OnRead = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _compare.ExecuteAsync(source.Id, target.Id, null, cancellation.Token));

        Assert.Contains(_profiles.Log.Entries, entry => entry.Message == "Schema comparison cancelled");
    }

    [Fact]
    public async Task Logs_carry_counts_and_ids_but_no_names_or_secrets()
    {
        var source = await _profiles.CreateSqlProfileAsync("Source");
        var target = await _profiles.CreateSqlProfileAsync("Target");
        _reader.Schemas[source.Id] = Schema(Table("SecretTableName"));

        await _compare.ExecuteAsync(source.Id, target.Id, null, Token);

        var logged = string.Join("\n", _profiles.Log.Entries.Select(entry => entry.AllText));
        Assert.Contains("Schema comparison completed", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretTableName", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t!", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("db01", logged, StringComparison.Ordinal);
    }

    private static TableDefinition Table(string name) =>
        new(new DatabaseObjectId("dbo", name, DatabaseObjectType.Table), [], null, [], [], [], [], []);

    private static DatabaseSchema Schema(params TableDefinition[] tables) =>
        new("Database", [new SchemaDefinition("dbo", tables, [], [], [])], [], false, true, DateTimeOffset.UnixEpoch);

    private sealed class SynchronousProgress(List<ComparisonStatus> stages) : IProgress<ComparisonStatus>
    {
        public void Report(ComparisonStatus value) => stages.Add(value);
    }

    private sealed class FakeProvider : IDatabaseProvider
    {
        public List<DatabaseConnectionRequest> Requests { get; } = [];

        public Dictionary<Guid, DataBridge.Shared.Errors.Error> Failures { get; } = [];

        public string? DatabaseNameOverride { get; set; }

        public DatabaseProviderType ProviderType => DatabaseProviderType.SqlServer;

        public Task<Result<ResolvedDatabaseIdentity>> ResolveIdentityAsync(DatabaseConnectionRequest request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            if (Failures.TryGetValue(request.Profile.Id, out var error))
            {
                return Task.FromResult(Result.Failure<ResolvedDatabaseIdentity>(error));
            }

            var name = DatabaseNameOverride ?? request.Profile.Name;
            return Task.FromResult(Result.Success(ResolvedDatabaseIdentity.Create(
                DatabaseProviderType.SqlServer, "DB01", name, 5, "SQL Server 2022", "Latin1_General_CI_AS")));
        }
    }

    private sealed class FakeSchemaReader : ISchemaReader
    {
        public List<DatabaseConnectionRequest> Requests { get; } = [];

        public Dictionary<Guid, DatabaseSchema> Schemas { get; } = [];

        public Dictionary<Guid, DataBridge.Shared.Errors.Error> Failures { get; } = [];

        public Action? OnRead { get; set; }

        public DatabaseProviderType ProviderType => DatabaseProviderType.SqlServer;

        public ISqlDefinitionNormalizer DefinitionNormalizer => ExactSqlDefinitionNormalizer.Instance;

        public Task<Result<DatabaseSchema>> ReadSchemaAsync(DatabaseConnectionRequest request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            OnRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failures.TryGetValue(request.Profile.Id, out var error))
            {
                return Task.FromResult(Result.Failure<DatabaseSchema>(error));
            }

            return Task.FromResult(Result.Success(Schemas.GetValueOrDefault(request.Profile.Id) ?? Schema()));
        }
    }

    private sealed class FakeFactory(IDatabaseProvider provider, ISchemaReader reader) : IDatabaseProviderFactory
    {
        public IDatabaseProvider GetProvider(DatabaseProviderType providerType) => provider;

        public ISchemaReader GetSchemaReader(DatabaseProviderType providerType) => reader;

        public IDataReaderProvider GetDataReader(DatabaseProviderType providerType) => throw new NotSupportedException();
    }
}
