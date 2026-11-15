using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Schema;
using DataBridge.Core.Synchronization;
using DataBridge.Shared.Results;

namespace DataBridge.Core.Tests.Synchronization;

/// <summary>Builds plans from schemas for planner tests, with SQL that is just the change's description.</summary>
internal static class SynchronizationFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    public static ResolvedDatabaseIdentity Identity(string database = "Target") =>
        ResolvedDatabaseIdentity.Create(DatabaseProviderType.SqlServer, "db01", database, 7, "SQL Server 2022", "Latin1_General_CI_AS");

    public static (SynchronizationPlan Plan, PlanDraft Draft) PlanSchema(
        DatabaseSchema source,
        DatabaseSchema target,
        IEnumerable<DatabaseObjectId> selected,
        bool allowDestructive = false,
        SynchronizationDirection direction = SynchronizationDirection.SourceToTarget)
    {
        var comparison = new SchemaComparer(ExactSqlDefinitionNormalizer.Instance).Compare(source, target, TestContext.Current.CancellationToken);
        var draft = new PlanDraft();
        SchemaSynchronizationPlanner.Plan(new SchemaPlanningInput(source, target, comparison, direction, [.. selected], allowDestructive), draft);
        return (Assemble(draft, new SynchronizationPlanOptions(AllowDestructiveOperations: allowDestructive), direction), draft);
    }

    public static SynchronizationPlan Assemble(
        PlanDraft draft,
        SynchronizationPlanOptions options,
        SynchronizationDirection direction = SynchronizationDirection.SourceToTarget,
        EnvironmentLabel environment = EnvironmentLabel.None) =>
        PlanAssembler.Assemble(
            draft,
            new PlanHeader(Guid.NewGuid(), Guid.NewGuid(), direction, Guid.NewGuid(), Identity(), environment, "schema-hash", options, Now),
            change => Result.Success<IReadOnlyList<string>>([change.Description]),
            Guid.NewGuid).Plan;

    public static DatabaseObjectId TableId(string name, string schema = "dbo") => new(schema, name, DatabaseObjectType.Table);

    public static int Position(SynchronizationPlan plan, string descriptionStart) =>
        plan.Operations.Single(operation => operation.Description.StartsWith(descriptionStart, StringComparison.Ordinal)).Order;

    public static ForeignKeyDefinition ForeignKey(string name, string column, string referencedTable, string referencedColumn = "Id") =>
        new(name, false, "dbo", referencedTable, [new ForeignKeyColumnMapping(column, referencedColumn)], ReferentialAction.NoAction, ReferentialAction.NoAction, false, false);
}
