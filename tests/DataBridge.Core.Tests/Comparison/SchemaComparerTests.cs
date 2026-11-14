using DataBridge.Core.Comparison;
using DataBridge.Core.Schema;
using static DataBridge.Core.Tests.Comparison.SchemaFixtures;

namespace DataBridge.Core.Tests.Comparison;

public sealed class SchemaComparerTests
{
    private static readonly SchemaComparer _comparer = new(ExactSqlDefinitionNormalizer.Instance);

    private static TableDefinition Orders() => Table("Orders", Column("Id"), Text("Customer", 50)) with
    {
        PrimaryKey = PrimaryKey("PK_Orders", systemNamed: false, "Id"),
        Indexes = [Index("IX_Orders_Customer", Key("Customer", 1), Include("Id"))],
    };

    private static DatabaseSchema Full() => Database(InSchema(
        "dbo",
        Orders(),
        View("OrderView", "CREATE VIEW dbo.OrderView AS SELECT Id FROM dbo.Orders"),
        Procedure("GetOrders", "CREATE PROCEDURE dbo.GetOrders AS SELECT 1"),
        Trigger("Orders_Audit", "Orders", "CREATE TRIGGER dbo.Orders_Audit ON dbo.Orders AFTER INSERT AS SELECT 1"),
        Sequence("OrderNumbers")));

    private static SchemaComparisonResult Run(DatabaseSchema source, DatabaseSchema target, SchemaComparer? comparer = null) =>
        (comparer ?? _comparer).Compare(source, target, TestContext.Current.CancellationToken);

    private static SchemaObjectDifference Find(SchemaComparisonResult result, DatabaseObjectType type, string name) =>
        Assert.Single(result.Differences, difference => difference.ObjectId.ObjectType == type && difference.ObjectId.ObjectName == name);

    [Fact]
    public void Identical_schemas_have_no_differences()
    {
        var result = Run(Full(), Full());

        Assert.False(result.HasDifferences);
        Assert.All(result.Differences, difference => Assert.Equal(DifferenceStatus.Equal, difference.Status));
        Assert.Contains(result.Differences, difference => difference.ObjectId.ObjectType == DatabaseObjectType.Index);
        Assert.Contains(result.Differences, difference => difference.ObjectId.ObjectType == DatabaseObjectType.Sequence);
    }

    [Fact]
    public void Objects_on_one_side_only_are_source_or_target_only()
    {
        var source = Database(InSchema("dbo", Table("A", Column("Id"))), InSchema("sales"));
        var target = Database(InSchema("dbo", Table("B", Column("Id"))), InSchema("hr"));

        var result = Run(source, target);

        Assert.Equal(DifferenceStatus.SourceOnly, Find(result, DatabaseObjectType.Table, "A").Status);
        Assert.Equal(DifferenceStatus.TargetOnly, Find(result, DatabaseObjectType.Table, "B").Status);
        Assert.Equal(DifferenceStatus.SourceOnly, Find(result, DatabaseObjectType.Schema, "sales").Status);
        Assert.Equal(DifferenceStatus.TargetOnly, Find(result, DatabaseObjectType.Schema, "hr").Status);
        Assert.DoesNotContain(result.Differences, difference => difference.ObjectId.ObjectType == DatabaseObjectType.Column);
    }

    [Fact]
    public void Column_changes_are_reported_property_by_property_under_a_modified_table()
    {
        var source = Database(InSchema("dbo", Table("T", Column("Id"), Text("Name", 50, nullable: true))));
        var target = Database(InSchema("dbo", Table("T", Column("Id"), Text("Name", 100, nullable: false, collation: "Latin1_General_CS_AS"))));

        var result = Run(source, target);

        Assert.Equal(DifferenceStatus.Modified, Find(result, DatabaseObjectType.Table, "T").Status);
        Assert.Equal(DifferenceStatus.Equal, Find(result, DatabaseObjectType.Column, "Id").Status);
        var name = Find(result, DatabaseObjectType.Column, "Name");
        Assert.Equal(DifferenceStatus.Modified, name.Status);
        Assert.Equal("T", name.ObjectId.ParentObjectName);
        Assert.Equal(
            [new("Length", "50", "100"), new("Nullable", "Yes", "No"), new("Collation", "Latin1_General_CI_AS", "Latin1_General_CS_AS")],
            name.Properties);
    }

    [Fact]
    public void Column_identity_computed_definition_and_ordinal_are_compared()
    {
        var source = Database(InSchema("dbo", Table("T", Column("A") with { Identity = new(1, 1) }, Column("B") with { Computed = new("([A]*(2))", false) })));
        var target = Database(InSchema("dbo", Table("T", Column("B") with { Computed = new("([A]*(3))", false) }, Column("A") with { Identity = new(1000, 1) })));

        var result = Run(source, target);

        var a = Find(result, DatabaseObjectType.Column, "A");
        Assert.Contains(new PropertyDifference("Identity", "IDENTITY(1, 1)", "IDENTITY(1000, 1)"), a.Properties);
        Assert.Contains(new PropertyDifference("OrdinalPosition", "1", "2"), a.Properties);
        Assert.Contains(new PropertyDifference("ComputedExpression", "([A]*(2))", "([A]*(3))"), Find(result, DatabaseObjectType.Column, "B").Properties);
    }

    [Fact]
    public void Names_match_case_insensitively_only_when_both_databases_ignore_case()
    {
        var source = Database(InSchema("dbo", Table("Orders", Column("Id"))));
        var target = Database(InSchema("dbo", Table("ORDERS", Column("ID"))));

        Assert.False(Run(source, target).HasDifferences);

        var caseSensitive = Run(source, target with { IdentifiersAreCaseSensitive = true });
        Assert.Equal(DifferenceStatus.SourceOnly, Find(caseSensitive, DatabaseObjectType.Table, "Orders").Status);
        Assert.Equal(DifferenceStatus.TargetOnly, Find(caseSensitive, DatabaseObjectType.Table, "ORDERS").Status);
    }

    [Fact]
    public void Same_name_with_different_object_kinds_is_incompatible()
    {
        var source = Database(InSchema("dbo", View("Thing", "CREATE VIEW dbo.Thing AS SELECT 1 AS A")));
        var target = Database(InSchema("dbo", Table("Thing", Column("A"))));

        var difference = Assert.Single(Run(source, target).Differences, item => item.ObjectId.ObjectName == "Thing");

        Assert.Equal(DifferenceStatus.Incompatible, difference.Status);
        Assert.Equal("The source object is a view and the target object is a table.", difference.Reason);
    }

    [Fact]
    public void Trigger_on_a_different_table_is_incompatible()
    {
        var source = Database(InSchema("dbo", Trigger("Audit", "Orders", "CREATE TRIGGER dbo.Audit ON dbo.Orders AFTER INSERT AS SELECT 1")));
        var target = Database(InSchema("dbo", Trigger("Audit", "Customers", "CREATE TRIGGER dbo.Audit ON dbo.Customers AFTER INSERT AS SELECT 1")));

        Assert.Equal(DifferenceStatus.Incompatible, Find(Run(source, target), DatabaseObjectType.Trigger, "Audit").Status);
    }

    [Fact]
    public void System_named_constraints_with_the_same_definition_match_despite_different_names()
    {
        static TableDefinition WithConstraints(string suffix) => Table("T", Column("Id"), Column("Qty")) with
        {
            PrimaryKey = PrimaryKey($"PK__T__{suffix}", systemNamed: true, "Id"),
            CheckConstraints = [new($"CK__T__Qty__{suffix}", true, "([Qty]>(0))", false, false)],
            DefaultConstraints = [new($"DF__T__Qty__{suffix}", true, "Qty", "((1))")],
        };

        var result = Run(Database(InSchema("dbo", WithConstraints("1A2B"))), Database(InSchema("dbo", WithConstraints("9F8E"))));

        Assert.False(result.HasDifferences);
        Assert.Equal("PK__T__1A2B", Find(result, DatabaseObjectType.PrimaryKey, "PK__T__1A2B").ObjectId.ObjectName);
    }

    [Fact]
    public void System_named_constraints_with_different_definitions_do_not_match()
    {
        static TableDefinition WithCheck(string name, string expression) =>
            Table("T", Column("Qty")) with { CheckConstraints = [new(name, true, expression, false, false)] };

        var result = Run(
            Database(InSchema("dbo", WithCheck("CK__T__1", "([Qty]>(0))"))),
            Database(InSchema("dbo", WithCheck("CK__T__2", "([Qty]>=(0))"))));

        Assert.Equal(DifferenceStatus.SourceOnly, Find(result, DatabaseObjectType.CheckConstraint, "CK__T__1").Status);
        Assert.Equal(DifferenceStatus.TargetOnly, Find(result, DatabaseObjectType.CheckConstraint, "CK__T__2").Status);
    }

    [Fact]
    public void Primary_key_name_and_key_order_differences_are_reported()
    {
        var source = Database(InSchema("dbo", Table("T", Column("A"), Column("B")) with { PrimaryKey = PrimaryKey("PK_T", false, "A", "B") }));
        var target = Database(InSchema("dbo", Table("T", Column("A"), Column("B")) with { PrimaryKey = PrimaryKey("PK_T_New", false, "B", "A") }));

        var key = Find(Run(source, target), DatabaseObjectType.PrimaryKey, "PK_T");

        Assert.Equal(DifferenceStatus.Modified, key.Status);
        Assert.Contains(new PropertyDifference("Name", "PK_T", "PK_T_New"), key.Properties);
        Assert.Contains(new PropertyDifference("KeyColumns", "A ASC, B ASC", "B ASC, A ASC"), key.Properties);
    }

    [Fact]
    public void Primary_key_on_one_side_only_is_reported_as_a_child()
    {
        var source = Database(InSchema("dbo", Table("T", Column("A")) with { PrimaryKey = PrimaryKey("PK_T", false, "A") }));
        var target = Database(InSchema("dbo", Table("T", Column("A"))));

        var result = Run(source, target);

        Assert.Equal(DifferenceStatus.SourceOnly, Find(result, DatabaseObjectType.PrimaryKey, "PK_T").Status);
        Assert.Equal(DifferenceStatus.Modified, Find(result, DatabaseObjectType.Table, "T").Status);
    }

    [Fact]
    public void Index_key_direction_matters_and_included_column_order_does_not()
    {
        static DatabaseSchema WithIndex(params IndexColumnDefinition[] columns) =>
            Database(InSchema("dbo", Table("T", Column("A"), Column("B"), Column("C")) with { Indexes = [Index("IX", columns)] }));

        var reordered = Run(
            WithIndex(Key("A", 1), Include("B"), Include("C")),
            WithIndex(Key("A", 1), Include("C"), Include("B")));
        Assert.False(reordered.HasDifferences);

        var descending = Find(
            Run(WithIndex(Key("A", 1)), WithIndex(Key("A", 1, descending: true))),
            DatabaseObjectType.Index,
            "IX");
        Assert.Equal([new("KeyColumns", "A ASC", "A DESC")], descending.Properties);
    }

    [Fact]
    public void Index_uniqueness_filter_and_options_are_compared()
    {
        var sourceIndex = Index("IX", Key("A", 1));
        var targetIndex = sourceIndex with
        {
            IsUnique = true,
            FilterDefinition = "([A]>(0))",
            Options = DefaultIndexOptions with { FillFactor = 80, DataCompression = "PAGE" },
        };

        var index = Find(
            Run(
                Database(InSchema("dbo", Table("T", Column("A")) with { Indexes = [sourceIndex] })),
                Database(InSchema("dbo", Table("T", Column("A")) with { Indexes = [targetIndex] }))),
            DatabaseObjectType.Index,
            "IX");

        Assert.Equal(["Unique", "Filter", "FillFactor", "DataCompression"], index.Properties.Select(property => property.PropertyName));
    }

    [Fact]
    public void Foreign_key_columns_and_referential_actions_are_compared()
    {
        static DatabaseSchema WithForeignKey(ReferentialAction onDelete, string referencedColumn) => Database(InSchema(
            "dbo",
            Table("Lines", Column("OrderId")) with
            {
                ForeignKeys = [new("FK_Lines_Orders", false, "dbo", "Orders", [new("OrderId", referencedColumn)], onDelete, ReferentialAction.NoAction, false, false)],
            }));

        var foreignKey = Find(
            Run(WithForeignKey(ReferentialAction.NoAction, "Id"), WithForeignKey(ReferentialAction.Cascade, "OrderId")),
            DatabaseObjectType.ForeignKey,
            "FK_Lines_Orders");

        Assert.Equal(
            [new("Columns", "OrderId -> Id", "OrderId -> OrderId"), new("OnDelete", "NO ACTION", "CASCADE")],
            foreignKey.Properties);
    }

    [Fact]
    public void Module_definitions_are_compared_after_normalization()
    {
        var source = Database(InSchema("dbo", Procedure("P", "CREATE PROCEDURE P AS SELECT 1")));
        var target = Database(InSchema("dbo", Procedure("P", "  CREATE PROCEDURE P AS SELECT 1\n")));
        var changed = Database(InSchema("dbo", Procedure("P", "CREATE PROCEDURE P AS SELECT 2")));

        Assert.False(Run(source, target).HasDifferences);
        var difference = Find(Run(source, changed), DatabaseObjectType.StoredProcedure, "P");
        Assert.Equal(DifferenceStatus.Modified, difference.Status);
        Assert.Equal([new("Definition", "CREATE PROCEDURE P AS SELECT 1", "CREATE PROCEDURE P AS SELECT 2")], difference.Properties);
    }

    [Fact]
    public void The_provider_normalizer_decides_which_text_differences_count()
    {
        var ignoreCase = new SchemaComparer(new UpperCaseNormalizer());
        var source = Database(InSchema("dbo", Procedure("P", "create procedure P as select 1")));
        var target = Database(InSchema("dbo", Procedure("P", "CREATE PROCEDURE P AS SELECT 1")));

        Assert.False(Run(source, target, ignoreCase).HasDifferences);
        Assert.True(Run(source, target).HasDifferences);
    }

    [Theory]
    [InlineData(true, "Source: The definition is encrypted (WITH ENCRYPTION).")]
    [InlineData(false, "Source: The definition is not visible to this login; it needs VIEW DEFINITION permission.")]
    public void Unreadable_definitions_are_unknown_never_equal(bool encrypted, string reason)
    {
        var source = Database(InSchema("dbo", Procedure("P", null, encrypted)));
        var target = Database(InSchema("dbo", Procedure("P", "CREATE PROCEDURE P AS SELECT 1")));

        var difference = Find(Run(source, target), DatabaseObjectType.StoredProcedure, "P");

        Assert.Equal(DifferenceStatus.Unknown, difference.Status);
        Assert.Equal(reason, difference.Reason);
        Assert.Empty(difference.Properties);
    }

    [Fact]
    public void Objects_missing_from_a_side_with_partial_metadata_are_unknown()
    {
        var source = Database(InSchema("dbo", Table("Visible", Column("Id")), Table("Hidden", Column("Id"))));
        var target = Database(InSchema("dbo", Table("Visible", Column("Id")))) with { HasCompleteMetadata = false };

        var hidden = Find(Run(source, target), DatabaseObjectType.Table, "Hidden");
        Assert.Equal(DifferenceStatus.Unknown, hidden.Status);
        Assert.Contains("target login cannot see every object", hidden.Reason, StringComparison.Ordinal);

        var reversed = Find(Run(target, source), DatabaseObjectType.Table, "Hidden");
        Assert.Equal(DifferenceStatus.Unknown, reversed.Status);
    }

    [Fact]
    public void Sequence_properties_are_compared()
    {
        var difference = Find(
            Run(Database(InSchema("dbo", Sequence("S"))), Database(InSchema("dbo", Sequence("S", start: 1000, increment: 10)))),
            DatabaseObjectType.Sequence,
            "S");

        Assert.Equal([new("StartValue", "1", "1000"), new("Increment", "1", "10")], difference.Properties);
    }

    [Fact]
    public void Results_list_each_object_followed_by_its_triggers_and_children()
    {
        var result = Run(Full(), Full());

        Assert.Equal(
            [
                "Schema dbo",
                "StoredProcedure dbo.GetOrders",
                "Sequence dbo.OrderNumbers",
                "View dbo.OrderView",
                "Table dbo.Orders",
                "Trigger dbo.Orders.Orders_Audit",
                "Column dbo.Orders.Customer",
                "Column dbo.Orders.Id",
                "PrimaryKey dbo.Orders.PK_Orders",
                "Index dbo.Orders.IX_Orders_Customer",
            ],
            result.Differences.Select(difference => difference.ObjectId.ToString()));
    }

    [Fact]
    public void Unsupported_objects_are_passed_through_from_both_sides()
    {
        var source = Full() with { UnsupportedObjects = [new("dbo", "Alias", "SYNONYM", "Synonyms are not compared.")] };
        var target = Full() with { UnsupportedObjects = [new("", "PF_Date", "PARTITION_FUNCTION", "Partitioning is not compared.")] };

        var result = Run(source, target);

        Assert.Equal(source.UnsupportedObjects, result.SourceUnsupportedObjects);
        Assert.Equal(target.UnsupportedObjects, result.TargetUnsupportedObjects);
        Assert.False(result.HasDifferences);
    }

    [Fact]
    public void Comparison_can_be_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => _comparer.Compare(Full(), Full(), cancellation.Token));
    }

    private sealed class UpperCaseNormalizer : ISqlDefinitionNormalizer
    {
        public string Normalize(string sql) => sql.ToUpperInvariant();
    }
}
