using DataBridge.Core.Schema;
using DataBridge.Core.Synchronization;
using static DataBridge.Core.Tests.Comparison.SchemaFixtures;
using static DataBridge.Core.Tests.Synchronization.SynchronizationFixtures;

namespace DataBridge.Core.Tests.Synchronization;

public sealed class SchemaSynchronizationPlannerTests
{
    private static TableDefinition Customers() =>
        Table("Customers", Column("Id"), Text("Name", 50)) with { PrimaryKey = PrimaryKey("PK_Customers", false, "Id") };

    private static TableDefinition Orders(bool withForeignKey = true) =>
        Table("Orders", Column("Id"), Column("CustomerId")) with
        {
            PrimaryKey = PrimaryKey("PK_Orders", false, "Id"),
            ForeignKeys = withForeignKey ? [ForeignKey("FK_Orders_Customers", "CustomerId", "Customers")] : [],
            Indexes = [Index("IX_Orders_Customer", Key("CustomerId", 1))],
        };

    [Fact]
    public void New_tables_are_created_before_the_foreign_keys_and_indexes_that_need_them()
    {
        var source = Database(InSchema("dbo", Orders(), Customers()));
        var target = Database(InSchema("dbo"));

        var (plan, _) = PlanSchema(source, target, [TableId("Orders"), TableId("Customers")]);

        Assert.Equal(4, plan.Operations.Count);
        var foreignKey = Position(plan, "Add foreign key");
        Assert.True(foreignKey > Position(plan, "Create table dbo.Customers"));
        Assert.True(foreignKey > Position(plan, "Create table dbo.Orders"));
        Assert.True(Position(plan, "Create index") > Position(plan, "Create table dbo.Orders"));
        Assert.All(plan.Operations, operation => Assert.Equal(OperationRisk.Low, operation.Risk));
        var fk = plan.Operations.Single(operation => operation.Description.StartsWith("Add foreign key", StringComparison.Ordinal));
        Assert.Equal(2, fk.DependsOn.Count);
    }

    [Fact]
    public void A_foreign_key_to_a_table_the_write_target_lacks_is_reported_not_planned()
    {
        var source = Database(InSchema("dbo", Orders(), Customers()));
        var target = Database(InSchema("dbo"));

        var (plan, _) = PlanSchema(source, target, [TableId("Orders")]);

        Assert.DoesNotContain(plan.Operations, operation => operation.Description.StartsWith("Add foreign key", StringComparison.Ordinal));
        Assert.Contains(plan.NotIncluded, issue => issue.Message.Contains("Select that table too", StringComparison.Ordinal));
        Assert.Contains(plan.Operations, operation => operation.Description == "Create table dbo.Orders");
    }

    [Fact]
    public void Drops_are_left_out_unless_destructive_operations_are_allowed()
    {
        var source = Database(InSchema("dbo"));
        var target = Database(InSchema("dbo", Customers()));

        var (plan, _) = PlanSchema(source, target, [TableId("Customers")]);

        Assert.Empty(plan.Operations);
        Assert.Contains(plan.NotIncluded, issue => issue.Message.Contains("destructive operations are not allowed", StringComparison.Ordinal));
    }

    [Fact]
    public void Dropping_a_referenced_table_first_drops_the_foreign_key_the_copied_side_also_lacks()
    {
        var source = Database(InSchema("dbo", Orders(withForeignKey: false)));
        var target = Database(InSchema("dbo", Orders(), Customers()));

        var (plan, _) = PlanSchema(source, target, [TableId("Customers")], allowDestructive: true);

        var dropForeignKey = plan.Operations.Single(operation => operation.ObjectId.ObjectType == DatabaseObjectType.ForeignKey);
        var dropTable = plan.Operations.Single(operation => operation.ObjectId.ObjectType == DatabaseObjectType.Table);
        Assert.Contains(dropForeignKey.Id, dropTable.DependsOn);
        Assert.True(dropForeignKey.Order < dropTable.Order);
        Assert.All(plan.Operations, operation => Assert.True(operation.IsDestructive && operation.Risk == OperationRisk.Critical));
        Assert.Contains("references dbo.Customers", dropForeignKey.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Widening_a_column_is_a_medium_alter_and_narrowing_one_is_destructive()
    {
        var source = Database(InSchema("dbo", Table("T", Column("Id"), Text("Wide", 100), Text("Narrow", 10))));
        var target = Database(InSchema("dbo", Table("T", Column("Id"), Text("Wide", 50), Text("Narrow", 20))));

        var (safe, _) = PlanSchema(source, target, [TableId("T")]);
        var widen = Assert.Single(safe.Operations);
        Assert.Equal(SynchronizationOperationType.AlterObject, widen.Type);
        Assert.Equal(OperationRisk.Medium, widen.Risk);
        Assert.False(widen.IsDestructive);
        Assert.Contains(safe.NotIncluded, issue => issue.Message.Contains("can lose data", StringComparison.Ordinal));

        var (all, _) = PlanSchema(source, target, [TableId("T")], allowDestructive: true);
        var narrow = all.Operations.Single(operation => operation.ObjectId.ObjectName == "Narrow");
        Assert.True(narrow.IsDestructive);
        Assert.Equal(OperationRisk.High, narrow.Risk);
        Assert.True(narrow.RequiresExplicitApproval);
    }

    [Fact]
    public void A_new_not_null_column_without_a_default_is_high_risk_and_one_with_a_default_includes_it()
    {
        var source = Database(InSchema("dbo", Table("T", Column("Id"), Column("Required"), Column("Defaulted")) with
        {
            DefaultConstraints = [new DefaultConstraintDefinition("DF_T_Defaulted", false, "Defaulted", "((0))")],
        }));
        var target = Database(InSchema("dbo", Table("T", Column("Id"))));

        var (plan, _) = PlanSchema(source, target, [TableId("T")]);

        Assert.Equal(2, plan.Operations.Count);
        Assert.Equal(OperationRisk.High, plan.Operations.Single(operation => operation.ObjectId.ObjectName == "Required").Risk);
        var defaulted = plan.Operations.Single(operation => operation.ObjectId.ObjectName == "Defaulted");
        Assert.Equal(OperationRisk.Medium, defaulted.Risk);
        Assert.Contains("with default DF_T_Defaulted", defaulted.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_changed_constraint_is_dropped_and_recreated_around_column_changes()
    {
        var source = Database(InSchema("dbo", Table("T", Column("Id"), Text("Code", 20)) with
        {
            CheckConstraints = [new CheckConstraintDefinition("CK_T_Code", false, "(len([Code])>(2))", false, false)],
        }));
        var target = Database(InSchema("dbo", Table("T", Column("Id"), Text("Code", 10)) with
        {
            CheckConstraints = [new CheckConstraintDefinition("CK_T_Code", false, "(len([Code])>(1))", false, false)],
        }));

        var (plan, _) = PlanSchema(source, target, [TableId("T")]);

        var drop = Position(plan, "Drop check constraint");
        var alter = Position(plan, "Change column");
        var create = Position(plan, "Recreate check constraint");
        Assert.True(drop < alter && alter < create);
        Assert.All(plan.Operations, operation => Assert.False(operation.IsDestructive));
        Assert.Equal(OperationRisk.High, plan.Operations[drop].Risk);
    }

    [Fact]
    public void A_constraint_that_differs_only_in_being_disabled_is_reported()
    {
        var check = new CheckConstraintDefinition("CK_T", false, "([Id]>(0))", false, false);
        var source = Database(InSchema("dbo", Table("T", Column("Id")) with { CheckConstraints = [check with { IsDisabled = true }] }));
        var target = Database(InSchema("dbo", Table("T", Column("Id")) with { CheckConstraints = [check] }));

        var (plan, _) = PlanSchema(source, target, [TableId("T")]);

        Assert.Empty(plan.Operations);
        Assert.Contains(plan.NotIncluded, issue => issue.Message.Contains("does not enable, disable", StringComparison.Ordinal));
    }

    [Fact]
    public void A_view_is_created_after_the_table_it_references()
    {
        var view = View("CustomerNames", "CREATE VIEW dbo.CustomerNames AS SELECT Name FROM dbo.Customers") with
        {
            Module = Module("CREATE VIEW dbo.CustomerNames AS SELECT Name FROM dbo.Customers") with { References = [new ModuleReference("dbo", "Customers")] },
        };
        var source = Database(InSchema("dbo", Customers(), view));
        var target = Database(InSchema("dbo"));

        var (plan, _) = PlanSchema(source, target, [view.Id, TableId("Customers")]);

        Assert.True(Position(plan, "Create view") > Position(plan, "Create table"));
    }

    [Fact]
    public void Functions_that_reference_each_other_are_reported_as_a_cycle()
    {
        ProgrammableObjectDefinition Function(string name, string other) => new(
            new DatabaseObjectId("dbo", name, DatabaseObjectType.ScalarFunction),
            Module($"CREATE FUNCTION dbo.{name}() RETURNS int AS BEGIN RETURN dbo.{other}() END") with { References = [new ModuleReference("dbo", other)] });
        var source = Database(InSchema("dbo", Function("A", "B"), Function("B", "A")));
        var target = Database(InSchema("dbo"));

        var (plan, _) = PlanSchema(source, target, [new("dbo", "A", DatabaseObjectType.ScalarFunction), new("dbo", "B", DatabaseObjectType.ScalarFunction)]);

        Assert.Empty(plan.Operations);
        Assert.Contains(plan.NotIncluded, issue => issue.Message.Contains("cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void Target_to_source_writes_the_source()
    {
        var source = Database(InSchema("dbo"));
        var target = Database(InSchema("dbo", Customers()));

        var (plan, _) = PlanSchema(source, target, [TableId("Customers")], direction: SynchronizationDirection.TargetToSource);

        var create = Assert.Single(plan.Operations);
        Assert.Equal(SynchronizationOperationType.CreateObject, create.Type);
    }

    [Fact]
    public void A_table_in_a_new_schema_creates_the_schema_first()
    {
        var table = Table("Ledger", Column("Id")) with { Id = TableId("Ledger", "finance") };
        var source = Database(InSchema("dbo"), InSchema("finance", table));
        var target = Database(InSchema("dbo"));

        var (plan, _) = PlanSchema(source, target, [table.Id]);

        Assert.Equal(2, plan.Operations.Count);
        Assert.True(Position(plan, "Create schema finance") < Position(plan, "Create table finance.Ledger"));
    }

    [Fact]
    public void Column_order_differences_are_reported_not_planned()
    {
        var source = Database(InSchema("dbo", Table("T", Column("A"), Column("B"))));
        var target = Database(InSchema("dbo", Table("T", Column("B"), Column("A"))));

        var (plan, _) = PlanSchema(source, target, [TableId("T")]);

        Assert.Empty(plan.Operations);
        Assert.Contains(plan.NotIncluded, issue => issue.Message.Contains("does not reorder columns", StringComparison.Ordinal));
    }

    [Fact]
    public void Lossy_type_changes_are_detected()
    {
        Assert.False(SchemaSynchronizationPlanner.IsLossy(Column("A", "int"), Column("A", "bigint")));
        Assert.True(SchemaSynchronizationPlanner.IsLossy(Column("A", "bigint"), Column("A", "int")));
        Assert.True(SchemaSynchronizationPlanner.IsLossy(Text("A", 10), Text("A", 5)));
        Assert.False(SchemaSynchronizationPlanner.IsLossy(Text("A", 10), Text("A", 10) with { IsMaxLength = true, MaxLength = null }));
        Assert.True(SchemaSynchronizationPlanner.IsLossy(Text("A", 10) with { IsMaxLength = true, MaxLength = null }, Text("A", 4000)));
        var money = Column("A", "decimal") with { Precision = 10, Scale = 2 };
        Assert.False(SchemaSynchronizationPlanner.IsLossy(money, money with { Precision = 12 }));
        Assert.True(SchemaSynchronizationPlanner.IsLossy(money, money with { Scale = 1, Precision = 9 }));
        Assert.True(SchemaSynchronizationPlanner.IsLossy(money, money with { Precision = 11, Scale = 4 }));
    }
}
