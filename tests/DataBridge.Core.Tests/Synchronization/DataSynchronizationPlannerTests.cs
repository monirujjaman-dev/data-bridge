using DataBridge.Core.Comparison;
using DataBridge.Core.Schema;
using DataBridge.Core.Synchronization;
using static DataBridge.Core.Tests.Comparison.SchemaFixtures;
using static DataBridge.Core.Tests.Synchronization.SynchronizationFixtures;

namespace DataBridge.Core.Tests.Synchronization;

public sealed class DataSynchronizationPlannerTests
{
    private static TableDefinition Parent() =>
        Table("Parent", Column("Id"), Text("Name", 50)) with { PrimaryKey = PrimaryKey("PK_Parent", false, "Id") };

    private static TableDefinition Child() =>
        Table("Child", Column("Id"), Column("ParentId")) with
        {
            PrimaryKey = PrimaryKey("PK_Child", false, "Id"),
            ForeignKeys = [ForeignKey("FK_Child_Parent", "ParentId", "Parent")],
        };

    private static DataTableSelection Selection(string table, string[] values, long sourceOnly = 0, long targetOnly = 0, long modified = 0)
    {
        var id = TableId(table);
        var summary = new TableDataComparisonSummary(
            id, new MatchingKey(id, ["Id"], MatchingKeySource.PrimaryKey), TableDataComparisonOutcome.Compared,
            0, sourceOnly, targetOnly, modified, [], ValueColumns: values);
        var rows = new Dictionary<DifferenceStatus, RecordedRows>();
        if (sourceOnly > 0)
        {
            rows[DifferenceStatus.SourceOnly] = new RecordedRows(sourceOnly, "hash-s");
        }

        if (targetOnly > 0)
        {
            rows[DifferenceStatus.TargetOnly] = new RecordedRows(targetOnly, "hash-t");
        }

        if (modified > 0)
        {
            rows[DifferenceStatus.Modified] = new RecordedRows(modified, "hash-m");
        }

        return new DataTableSelection(summary, values, new RecordedChangeSet($"store-{table}", rows));
    }

    private static SynchronizationPlan PlanData(
        DatabaseSchema schema,
        SynchronizationPlanOptions options,
        params DataTableSelection[] tables)
    {
        var draft = new PlanDraft();
        DataSynchronizationPlanner.Plan(new DataPlanningInput(schema, schema, SynchronizationDirection.SourceToTarget, tables, options, []), draft);
        return Assemble(draft, options);
    }

    [Fact]
    public void Parents_are_inserted_first_and_deleted_last()
    {
        var schema = Database(InSchema("dbo", Child(), Parent()));
        var options = new SynchronizationPlanOptions(AllowDestructiveOperations: true);

        var plan = PlanData(
            schema,
            options,
            Selection("Child", ["ParentId"], sourceOnly: 3, targetOnly: 1, modified: 2),
            Selection("Parent", ["Name"], sourceOnly: 2, targetOnly: 4));

        Assert.True(Position(plan, "Insert 2 rows into dbo.Parent") < Position(plan, "Insert 3 rows into dbo.Child"));
        Assert.True(Position(plan, "Insert 2 rows into dbo.Parent") < Position(plan, "Update 2 changed rows in dbo.Child"));
        Assert.True(Position(plan, "Delete 1 rows from dbo.Child") < Position(plan, "Delete 4 rows from dbo.Parent"));
        Assert.True(Position(plan, "Update 2 changed rows in dbo.Child") < Position(plan, "Delete 4 rows from dbo.Parent"));

        var insert = plan.Operations.Single(operation => operation.Description == "Insert 3 rows into dbo.Child");
        Assert.Equal(OperationRisk.Low, insert.Risk);
        Assert.Equal(DifferenceStatus.SourceOnly, insert.Rows!.Status);
        Assert.Equal(["Id", "ParentId"], insert.Rows.Columns);
        Assert.Equal("hash-s", insert.Rows.ContentHash);
        var delete = plan.Operations.Single(operation => operation.Description == "Delete 4 rows from dbo.Parent");
        Assert.True(delete.IsDestructive);
        Assert.Equal(OperationRisk.Critical, delete.Risk);
        Assert.Equal(4, delete.EstimatedAffectedRows);
    }

    [Fact]
    public void Deletes_are_left_out_unless_destructive_operations_are_allowed()
    {
        var plan = PlanData(Database(InSchema("dbo", Parent())), SynchronizationPlanOptions.Default, Selection("Parent", ["Name"], targetOnly: 5));

        Assert.Empty(plan.Operations);
        Assert.Contains(plan.NotIncluded, issue => issue.Message.Contains("are not deleted", StringComparison.Ordinal));
    }

    [Fact]
    public void Inserts_need_a_value_for_every_required_column()
    {
        var schema = Database(InSchema("dbo", Table("T", Column("Id"), Column("Required"), Column("Optional", nullable: true))));

        var plan = PlanData(schema, SynchronizationPlanOptions.Default, Selection("T", [], sourceOnly: 1));

        Assert.Empty(plan.Operations);
        Assert.Contains(plan.NotIncluded, issue => issue.Message.Contains("Required would have no value", StringComparison.Ordinal));
    }

    [Fact]
    public void Large_changes_are_high_risk_and_identity_values_are_kept_on_insert()
    {
        var table = Table("T", Column("Id") with { Identity = new IdentitySpecification(1, 1) }, Text("Name", 10));
        var plan = PlanData(
            Database(InSchema("dbo", table)),
            new SynchronizationPlanOptions(LargeRowCountThreshold: 10),
            Selection("T", ["Name"], sourceOnly: 11, modified: 3));

        var insert = plan.Operations.Single(operation => operation.Type == SynchronizationOperationType.InsertRows);
        Assert.Equal(OperationRisk.High, insert.Risk);
        Assert.Contains(insert.Warnings, warning => warning.Contains("IDENTITY_INSERT", StringComparison.Ordinal));
        Assert.Equal(OperationRisk.Medium, plan.Operations.Single(operation => operation.Type == SynchronizationOperationType.UpdateRows).Risk);
    }

    [Fact]
    public void Tables_that_reference_each_other_cannot_be_ordered_and_are_reported()
    {
        var a = Table("A", Column("Id"), Column("BId")) with { ForeignKeys = [ForeignKey("FK_A_B", "BId", "B")] };
        var b = Table("B", Column("Id"), Column("AId")) with { ForeignKeys = [ForeignKey("FK_B_A", "AId", "A")] };

        var plan = PlanData(Database(InSchema("dbo", a, b)), SynchronizationPlanOptions.Default, Selection("A", ["BId"], sourceOnly: 1), Selection("B", ["AId"], sourceOnly: 1));

        Assert.Empty(plan.Operations);
        Assert.Contains(plan.NotIncluded, issue => issue.Message.Contains("cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void Triggers_on_the_write_target_are_warned_about_and_schema_changed_tables_are_skipped()
    {
        var schema = Database(InSchema("dbo", Parent(), Trigger("TR_Parent_Audit", "Parent", "CREATE TRIGGER ...")));
        var draft = new PlanDraft();
        DataSynchronizationPlanner.Plan(
            new DataPlanningInput(schema, schema, SynchronizationDirection.SourceToTarget, [Selection("Parent", ["Name"], modified: 1)], SynchronizationPlanOptions.Default, []),
            draft);
        Assert.Contains(Assert.Single(draft.Changes).Warnings, warning => warning.Contains("TR_Parent_Audit", StringComparison.Ordinal));

        var skipped = new PlanDraft();
        DataSynchronizationPlanner.Plan(
            new DataPlanningInput(schema, schema, SynchronizationDirection.SourceToTarget, [Selection("Parent", ["Name"], modified: 1)], SynchronizationPlanOptions.Default, [TableId("Parent")]),
            skipped);
        Assert.Empty(skipped.Changes);
        Assert.Contains("compare the data again", Assert.Single(skipped.Issues).Message, StringComparison.Ordinal);
    }
}
