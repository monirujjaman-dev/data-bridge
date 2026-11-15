using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;
using DataBridge.Application.DataComparison;
using DataBridge.Application.SchemaComparison;
using DataBridge.Application.Synchronization;
using DataBridge.Application.Tests.Connections;
using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Data;
using DataBridge.Core.Schema;
using DataBridge.Core.Synchronization;
using DataBridge.Shared.Results;

namespace DataBridge.Application.Tests.Synchronization;

public sealed class SynchronizationUseCaseTests
{
    private readonly ConnectionProfileFixture _profiles = new();
    private readonly World _world = new();
    private readonly SynchronizationPlanStore _store = new();
    private readonly CreateSynchronizationPlan _create;
    private readonly ValidateSynchronizationPlan _validate;
    private readonly ApproveSynchronizationPlan _approve;
    private readonly SynchronizationScripts _scripts;

    public SynchronizationUseCaseTests()
    {
        _create = new CreateSynchronizationPlan(_profiles.Repository, _world, _store, _profiles.Clock, _profiles.Log.For<CreateSynchronizationPlan>());
        _validate = new ValidateSynchronizationPlan(
            new SavedConnectionRequests(_profiles.Repository, _profiles.Secrets), _world, _world, _store, _profiles.Clock, _profiles.Log.For<ValidateSynchronizationPlan>());
        _approve = new ApproveSynchronizationPlan(_store, _profiles.Clock, _profiles.Log.For<ApproveSynchronizationPlan>());
        _scripts = new SynchronizationScripts(_store, _world, _world, _profiles.Repository);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TableDefinition Table(string name, params string[] columns) => new(
        new DatabaseObjectId("dbo", name, DatabaseObjectType.Table),
        [.. columns.Select((column, index) => new ColumnDefinition(column, "int", null, false, null, null, index > 0, null, null, null, index + 1))],
        new PrimaryKeyDefinition($"PK_{name}", false, "CLUSTERED", [new IndexColumnDefinition(columns[0], 1, false, false)], new IndexOptions(false, 100, false, true, true, false, "NONE")),
        [], [], [], [], []);

    private static DatabaseSchema Schema(string name, params TableDefinition[] tables) =>
        new(name, [new SchemaDefinition("dbo", tables, [], [], [])], [], false, true, DateTimeOffset.UnixEpoch);

    private async Task<(DatabaseConnection Source, DatabaseConnection Target)> ProfilesAsync(EnvironmentLabel targetEnvironment = EnvironmentLabel.None)
    {
        var source = await _profiles.CreateSqlProfileAsync("Source");
        var target = (await _profiles.Create.ExecuteAsync(ConnectionProfileFixture.SqlInput("Target") with { Environment = targetEnvironment }, Token)).Value.Profile;
        _world.Schemas[source.Id] = Schema("Source", Table("Orders", "Id", "Note"), Table("Customers", "Id", "Name"));
        _world.Schemas[target.Id] = Schema("Target", Table("Orders", "Id"));
        return (source, target);
    }

    private async Task<SchemaComparisonReport> CompareSchemasAsync(DatabaseConnection source, DatabaseConnection target)
    {
        var compare = new CompareSchemas(new SavedConnectionRequests(_profiles.Repository, _profiles.Secrets), _world, _profiles.Clock, _profiles.Log.For<CompareSchemas>());
        return (await compare.ExecuteAsync(source.Id, target.Id, null, Token)).Value;
    }

    private static SynchronizationPlanRequest SchemaRequest(DatabaseConnection source, DatabaseConnection target, SchemaComparisonReport report, bool allowDestructive = false) =>
        new(source.Id, target.Id, SynchronizationDirection.SourceToTarget, report,
            [.. report.Result.Differences.Where(difference => difference.Status != DifferenceStatus.Equal && !difference.ObjectId.IsChild).Select(difference => difference.ObjectId)],
            null, [], new Dictionary<DatabaseObjectId, RecordedChangeSet>(), new SynchronizationPlanOptions(allowDestructive));

    [Fact]
    public async Task A_plan_is_created_from_a_comparison_without_connecting_to_either_database()
    {
        var (source, target) = await ProfilesAsync();
        var report = await CompareSchemasAsync(source, target);
        var connectionsBefore = _world.Connections;

        var created = await _create.ExecuteAsync(SchemaRequest(source, target, report), Token);

        Assert.True(created.IsSuccess, string.Join("; ", created.Errors));
        Assert.Equal(connectionsBefore, _world.Connections);
        var plan = created.Value.Plan;
        Assert.Equal(PlanStatus.Draft, created.Value.Status);
        Assert.Equal(target.Id, plan.WriteTargetConnectionId);
        Assert.Equal(report.Target.IdentityFingerprint, plan.WriteTargetIdentityFingerprint);
        Assert.Equal(SchemaFingerprint.Compute(report.TargetSchema!), plan.WriteTargetSchemaFingerprint);
        Assert.Contains(plan.Operations, operation => operation.Description == "Create table dbo.Customers");
        Assert.Contains(plan.Operations, operation => operation.Description == "Add column dbo.Orders.Note");
        Assert.Same(created.Value, _store.Find(plan.Id));
    }

    [Fact]
    public async Task A_plan_moves_from_draft_through_validation_to_approval()
    {
        var (source, target) = await ProfilesAsync();
        var plan = (await _create.ExecuteAsync(SchemaRequest(source, target, await CompareSchemasAsync(source, target)), Token)).Value.Plan;

        Assert.Equal(SynchronizationUseCaseErrors.NotValidated, Assert.Single(_approve.Execute(plan.Id, new ApprovalRequest(plan.WriteTargetIdentityFingerprint, [], null)).Errors));

        var validated = await _validate.ExecuteAsync(plan.Id, Token);
        Assert.Equal(PlanStatus.Validated, validated.Value.Status);

        var approved = _approve.Execute(plan.Id, new ApprovalRequest(plan.WriteTargetIdentityFingerprint, [], null));
        Assert.True(approved.IsSuccess, string.Join("; ", approved.Errors));
        Assert.Equal(PlanStatus.Approved, approved.Value.Status);
        Assert.Equal(plan.ContentHash, approved.Value.Approval!.ApprovedContentHash);
        Assert.Same(plan, approved.Value.Plan);
    }

    [Fact]
    public async Task A_write_target_whose_schema_changed_since_comparison_invalidates_the_plan()
    {
        var (source, target) = await ProfilesAsync();
        var plan = (await _create.ExecuteAsync(SchemaRequest(source, target, await CompareSchemasAsync(source, target)), Token)).Value.Plan;
        _world.Schemas[target.Id] = Schema("Target", Table("Orders", "Id", "AddedMeanwhile"));

        var validated = await _validate.ExecuteAsync(plan.Id, Token);

        Assert.Equal(PlanStatus.Invalidated, validated.Value.Status);
        Assert.Contains("schema changed", validated.Value.StatusReason, StringComparison.Ordinal);
        Assert.Equal(SynchronizationUseCaseErrors.Invalidated, Assert.Single(_approve.Execute(plan.Id, new ApprovalRequest(plan.WriteTargetIdentityFingerprint, [], null)).Errors));
    }

    [Fact]
    public async Task A_profile_that_now_reaches_another_database_invalidates_the_plan()
    {
        var (source, target) = await ProfilesAsync();
        var plan = (await _create.ExecuteAsync(SchemaRequest(source, target, await CompareSchemasAsync(source, target)), Token)).Value.Plan;
        _world.DatabaseNameOverrides[target.Id] = "Elsewhere";

        Assert.Equal(PlanStatus.Invalidated, (await _validate.ExecuteAsync(plan.Id, Token)).Value.Status);
    }

    [Fact]
    public async Task Blocking_findings_fail_validation_and_block_approval()
    {
        var (source, target) = await ProfilesAsync();
        var plan = (await _create.ExecuteAsync(SchemaRequest(source, target, await CompareSchemasAsync(source, target)), Token)).Value.Plan;
        _world.Findings.Add(new ValidationFinding(plan.Operations[0].Id, ValidationSeverity.Blocking, "The login lacks CREATE TABLE."));

        var validated = await _validate.ExecuteAsync(plan.Id, Token);

        Assert.Equal(PlanStatus.ValidationFailed, validated.Value.Status);
        Assert.True(validated.Value.Validation!.HasBlockingFindings);
        Assert.Equal(SynchronizationUseCaseErrors.ValidationFailed, Assert.Single(_approve.Execute(plan.Id, new ApprovalRequest(plan.WriteTargetIdentityFingerprint, [], null)).Errors));
    }

    [Fact]
    public async Task A_production_write_target_needs_its_name_typed_to_approve()
    {
        var (source, target) = await ProfilesAsync(EnvironmentLabel.Production);
        var plan = (await _create.ExecuteAsync(SchemaRequest(source, target, await CompareSchemasAsync(source, target)), Token)).Value.Plan;
        await _validate.ExecuteAsync(plan.Id, Token);
        var risky = plan.OperationsRequiringApproval.Select(operation => operation.Id).ToArray();

        Assert.Equal(EnvironmentLabel.Production, plan.WriteTargetEnvironment);
        Assert.Contains(_approve.Execute(plan.Id, new ApprovalRequest(plan.WriteTargetIdentityFingerprint, risky, null)).Errors, error => error == SynchronizationErrors.DatabaseNameNotTyped);
        Assert.True(_approve.Execute(plan.Id, new ApprovalRequest(plan.WriteTargetIdentityFingerprint, risky, "Target")).IsSuccess);
    }

    [Fact]
    public async Task Data_rows_are_planned_from_recorded_change_sets_and_a_damaged_change_set_invalidates_the_plan()
    {
        var (source, target) = await ProfilesAsync();
        _world.Schemas[target.Id] = _world.Schemas[source.Id] with { DatabaseName = "Target" };
        var customers = new DatabaseObjectId("dbo", "Customers", DatabaseObjectType.Table);
        var data = new DataComparisonReport(
            Guid.NewGuid(), World.Identity("Source"), World.Identity("Target"),
            [new TableDataComparisonSummary(customers, new MatchingKey(customers, ["Id"], MatchingKeySource.PrimaryKey), TableDataComparisonOutcome.Compared, 5, 2, 0, 1, [], ValueColumns: ["Name"])],
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
        {
            SourceSchema = _world.Schemas[source.Id],
            TargetSchema = _world.Schemas[target.Id],
        };
        var recorder = new ChangeSetRecorder(_world);
        recorder.Add(new RowDifference(customers, [DatabaseValue.FromInt64(1, "int")], DifferenceStatus.SourceOnly, [], "a", null));
        recorder.Add(new RowDifference(customers, [DatabaseValue.FromInt64(2, "int")], DifferenceStatus.SourceOnly, [], "b", null));
        recorder.Add(new RowDifference(customers, [DatabaseValue.FromInt64(3, "int")], DifferenceStatus.Modified, [], "c", "d"));
        var changeSets = await recorder.CompleteAsync(Token);

        var created = await _create.ExecuteAsync(
            new SynchronizationPlanRequest(source.Id, target.Id, SynchronizationDirection.SourceToTarget, null, [], data, [customers], changeSets, SynchronizationPlanOptions.Default),
            Token);

        var plan = created.Value.Plan;
        Assert.Equal(2, plan.Operations.Count);
        var insert = plan.Operations.Single(operation => operation.Type == SynchronizationOperationType.InsertRows);
        Assert.Equal(2, insert.Rows!.RowCount);
        Assert.Equal(changeSets[customers].Rows[DifferenceStatus.SourceOnly].ContentHash, insert.Rows.ContentHash);
        Assert.Equal(PlanStatus.Validated, (await _validate.ExecuteAsync(plan.Id, Token)).Value.Status);

        _world.DamagedStores.Add(insert.Rows.StoreId);
        Assert.Equal(PlanStatus.Invalidated, (await _validate.ExecuteAsync(plan.Id, Token)).Value.Status);

        await _scripts.DiscardAsync(plan.Id, Token);
        Assert.Null(_store.Find(plan.Id));
        Assert.Contains(insert.Rows.StoreId, _world.DeletedStores);
    }

    [Fact]
    public async Task Plans_are_refused_without_a_comparison_or_for_the_same_database()
    {
        var (source, target) = await ProfilesAsync();
        var none = await _create.ExecuteAsync(
            new SynchronizationPlanRequest(source.Id, target.Id, SynchronizationDirection.SourceToTarget, null, [], null, [], new Dictionary<DatabaseObjectId, RecordedChangeSet>(), SynchronizationPlanOptions.Default),
            Token);
        Assert.Equal(SynchronizationUseCaseErrors.NoComparison, Assert.Single(none.Errors));

        _world.DatabaseNameOverrides[target.Id] = "Source";
        var same = await _create.ExecuteAsync(SchemaRequest(source, target, await CompareSchemasAsync(source, target)), Token);
        Assert.Equal(SynchronizationUseCaseErrors.SameDatabase, Assert.Single(same.Errors));
    }

    [Fact]
    public async Task The_script_comes_from_the_providers_generator()
    {
        var (source, target) = await ProfilesAsync();
        var plan = (await _create.ExecuteAsync(SchemaRequest(source, target, await CompareSchemasAsync(source, target)), Token)).Value.Plan;

        Assert.Equal($"script for {plan.Id}", (await _scripts.ScriptAsync(plan.Id, Token)).Value);
        Assert.Equal(SynchronizationUseCaseErrors.PlanNotFound, Assert.Single((await _scripts.ScriptAsync(Guid.NewGuid(), Token)).Errors));
    }

    /// <summary>Fake provider, schema reader, generator, inspector and change set store.</summary>
    private sealed class World : IDatabaseProviderFactory, IDatabaseProvider, ISchemaReader, ISqlGenerator, ISynchronizationTargetInspector, IChangeSetStore
    {
        public Dictionary<Guid, DatabaseSchema> Schemas { get; } = [];

        public Dictionary<Guid, string> DatabaseNameOverrides { get; } = [];

        public List<ValidationFinding> Findings { get; } = [];

        public HashSet<string> DamagedStores { get; } = [];

        public List<string> DeletedStores { get; } = [];

        public int Connections { get; private set; }

        private readonly Dictionary<string, RecordedChangeSet> _stored = [];

        public DatabaseProviderType ProviderType => DatabaseProviderType.SqlServer;

        public ISqlDefinitionNormalizer DefinitionNormalizer => ExactSqlDefinitionNormalizer.Instance;

        public static ResolvedDatabaseIdentity Identity(string name) =>
            ResolvedDatabaseIdentity.Create(DatabaseProviderType.SqlServer, "DB01", name, 5, "SQL Server 2022", null);

        public IDatabaseProvider GetProvider(DatabaseProviderType providerType) => this;

        public ISchemaReader GetSchemaReader(DatabaseProviderType providerType) => this;

        public IDataReaderProvider GetDataReader(DatabaseProviderType providerType) => throw new NotSupportedException();

        public ISqlGenerator GetSqlGenerator(DatabaseProviderType providerType) => this;

        public ISynchronizationTargetInspector GetTargetInspector(DatabaseProviderType providerType) => this;

        public Task<Result<ResolvedDatabaseIdentity>> ResolveIdentityAsync(DatabaseConnectionRequest request, CancellationToken cancellationToken)
        {
            Connections++;
            return Task.FromResult(Result.Success(Identity(DatabaseNameOverrides.GetValueOrDefault(request.Profile.Id) ?? request.Profile.Name)));
        }

        public Task<Result<DatabaseSchema>> ReadSchemaAsync(DatabaseConnectionRequest request, CancellationToken cancellationToken)
        {
            Connections++;
            return Task.FromResult(Result.Success(Schemas[request.Profile.Id]));
        }

        public Result<IReadOnlyList<string>> Generate(PlannedChange change) => Result.Success<IReadOnlyList<string>>([change.Description]);

        public string BuildScript(SynchronizationPlan plan) => $"script for {plan.Id}";

        public Task<Result<IReadOnlyList<ValidationFinding>>> InspectAsync(DatabaseConnectionRequest writeTarget, AssembledPlan plan, CancellationToken cancellationToken)
        {
            Connections++;
            return Task.FromResult(Result.Success<IReadOnlyList<ValidationFinding>>([.. Findings]));
        }

        public IChangeSetWriter CreateWriter(DatabaseObjectId table) => new Writer(this);

        public Task<bool> VerifyAsync(RowChangeSet rows, CancellationToken cancellationToken) =>
            Task.FromResult(_stored.TryGetValue(rows.StoreId, out var stored)
                && !DamagedStores.Contains(rows.StoreId)
                && stored.Rows[rows.Status].ContentHash == rows.ContentHash);

        public IAsyncEnumerable<StoredRowChange> ReadAsync(string storeId, DifferenceStatus status, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string storeId, CancellationToken cancellationToken)
        {
            DeletedStores.Add(storeId);
            return Task.CompletedTask;
        }

        private sealed class Writer(World world) : IChangeSetWriter
        {
            private readonly string _id = Guid.NewGuid().ToString("N");
            private readonly Dictionary<DifferenceStatus, long> _counts = [];

            public void Append(StoredRowChange row) => _counts[row.Status] = _counts.GetValueOrDefault(row.Status) + 1;

            public Task<RecordedChangeSet> CompleteAsync(CancellationToken cancellationToken)
            {
                var recorded = new RecordedChangeSet(_id, _counts.ToDictionary(pair => pair.Key, pair => new RecordedRows(pair.Value, $"hash-{pair.Key}-{pair.Value}")));
                world._stored[_id] = recorded;
                return Task.FromResult(recorded);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
