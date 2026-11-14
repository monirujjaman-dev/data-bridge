using DataBridge.Core.Comparison;
using DataBridge.Core.Schema;
using static DataBridge.Core.Tests.Comparison.SchemaFixtures;

namespace DataBridge.Core.Tests.Comparison;

/// <summary>
/// Cases where a difference could be hidden: invisible definitions, engine attributes, key options, untrusted
/// constraints and names that only collide under .NET case folding.
/// </summary>
public sealed class SchemaComparerReviewTests
{
    private static readonly SchemaComparer _comparer = new(ExactSqlDefinitionNormalizer.Instance);

    private static SchemaComparisonResult Run(DatabaseSchema source, DatabaseSchema target) =>
        _comparer.Compare(source, target, TestContext.Current.CancellationToken);

    private static SchemaObjectDifference Find(SchemaComparisonResult result, DatabaseObjectType type, string name) =>
        Assert.Single(result.Differences, difference => difference.ObjectId.ObjectType == type && difference.ObjectId.ObjectName == name);

    private static DatabaseSchema WithTable(TableDefinition table) => Database(InSchema("dbo", table));

    [Fact]
    public void Hidden_check_expressions_are_unknown_not_equal()
    {
        static DatabaseSchema WithCheck(string? expression) =>
            WithTable(Table("T", Column("Qty")) with { CheckConstraints = [new("CK_T_Qty", false, expression, false, false)] });

        var bothHidden = Find(Run(WithCheck(null), WithCheck(null)), DatabaseObjectType.CheckConstraint, "CK_T_Qty");
        var oneHidden = Find(Run(WithCheck("([Qty]>(0))"), WithCheck(null)), DatabaseObjectType.CheckConstraint, "CK_T_Qty");

        Assert.Equal(DifferenceStatus.Unknown, bothHidden.Status);
        Assert.Equal(DifferenceStatus.Unknown, oneHidden.Status);
        Assert.Contains("VIEW DEFINITION", oneHidden.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Hidden_system_named_checks_are_not_paired_with_each_other()
    {
        static DatabaseSchema WithCheck(string name) =>
            WithTable(Table("T", Column("Qty")) with { CheckConstraints = [new(name, true, null, false, false)] });

        var result = Run(WithCheck("CK__T__1"), WithCheck("CK__T__2"));

        Assert.Equal(DifferenceStatus.SourceOnly, Find(result, DatabaseObjectType.CheckConstraint, "CK__T__1").Status);
        Assert.Equal(DifferenceStatus.TargetOnly, Find(result, DatabaseObjectType.CheckConstraint, "CK__T__2").Status);
    }

    [Fact]
    public void Hidden_defaults_and_computed_expressions_are_unknown()
    {
        var hidden = WithTable(Table("T", Column("A") with { Computed = new(null, false) }) with
        {
            DefaultConstraints = [new("DF_T_A", false, "A", null)],
        });
        var visible = WithTable(Table("T", Column("A") with { Computed = new("((1))", false) }) with
        {
            DefaultConstraints = [new("DF_T_A", false, "A", "((0))")],
        });

        var result = Run(visible, hidden);

        Assert.Equal(DifferenceStatus.Unknown, Find(result, DatabaseObjectType.Column, "A").Status);
        Assert.Equal(DifferenceStatus.Unknown, Find(result, DatabaseObjectType.DefaultConstraint, "DF_T_A").Status);
        Assert.Equal(DifferenceStatus.Modified, Find(result, DatabaseObjectType.Table, "T").Status);
    }

    [Fact]
    public void Column_storage_and_security_attributes_are_compared()
    {
        var plain = WithTable(Table("T", Column("Ssn", "char")));
        var protectedColumn = WithTable(Table("T", Column("Ssn", "char") with
        {
            Options = ColumnOptions.None with { MaskingFunction = "default()", EncryptionType = "DETERMINISTIC", IsSparse = true },
        }));

        var column = Find(Run(plain, protectedColumn), DatabaseObjectType.Column, "Ssn");

        Assert.Equal(
            [new("Sparse", "No", "Yes"), new("Masking", null, "default()"), new("Encryption", null, "DETERMINISTIC")],
            column.Properties);
    }

    [Fact]
    public void Identity_not_for_replication_is_compared()
    {
        var source = WithTable(Table("T", Column("Id") with { Identity = new(1, 1) }));
        var target = WithTable(Table("T", Column("Id") with { Identity = new(1, 1, IsNotForReplication: true) }));

        Assert.Equal(
            [new("Identity", "IDENTITY(1, 1)", "IDENTITY(1, 1) NOT FOR REPLICATION")],
            Find(Run(source, target), DatabaseObjectType.Column, "Id").Properties);
    }

    [Fact]
    public void Key_constraint_options_are_compared()
    {
        var source = WithTable(Table("T", Column("Id")) with { PrimaryKey = PrimaryKey("PK_T", false, "Id") });
        var target = WithTable(Table("T", Column("Id")) with
        {
            PrimaryKey = PrimaryKey("PK_T", false, "Id") with { Options = DefaultIndexOptions with { DataCompression = "PAGE", FillFactor = 90 } },
        });

        Assert.Equal(
            ["FillFactor", "DataCompression"],
            Find(Run(source, target), DatabaseObjectType.PrimaryKey, "PK_T").Properties.Select(property => property.PropertyName));
    }

    [Fact]
    public void Heap_compression_is_a_table_property()
    {
        var table = Find(
            Run(WithTable(Table("T", Column("Id")) with { HeapDataCompression = "NONE" }), WithTable(Table("T", Column("Id")) with { HeapDataCompression = "PAGE" })),
            DatabaseObjectType.Table,
            "T");

        Assert.Equal(DifferenceStatus.Modified, table.Status);
        Assert.Equal([new("HeapDataCompression", "NONE", "PAGE")], table.Properties);
    }

    [Fact]
    public void Untrusted_constraints_differ_from_trusted_ones()
    {
        static DatabaseSchema With(bool notTrusted) => WithTable(Table("T", Column("Qty")) with
        {
            CheckConstraints = [new("CK", false, "([Qty]>(0))", false, false, notTrusted)],
            ForeignKeys = [new("FK", false, "dbo", "P", [new("Qty", "Id")], ReferentialAction.NoAction, ReferentialAction.NoAction, false, false, notTrusted)],
        });

        var result = Run(With(false), With(true));

        Assert.Equal([new("NotTrusted", "No", "Yes")], Find(result, DatabaseObjectType.CheckConstraint, "CK").Properties);
        Assert.Equal([new("NotTrusted", "No", "Yes")], Find(result, DatabaseObjectType.ForeignKey, "FK").Properties);
    }

    [Fact]
    public void Foreign_key_to_an_invisible_table_is_unknown()
    {
        static DatabaseSchema With(string? referencedTable) => WithTable(Table("T", Column("PId")) with
        {
            ForeignKeys = [new("FK", false, referencedTable is null ? null : "dbo", referencedTable, [new("PId", "Id")], ReferentialAction.NoAction, ReferentialAction.NoAction, false, false)],
        });

        Assert.Equal(DifferenceStatus.Unknown, Find(Run(With("P"), With(null)), DatabaseObjectType.ForeignKey, "FK").Status);
    }

    [Fact]
    public void Names_that_collide_only_under_dotnet_case_folding_fall_back_to_ordinal_matching()
    {
        // Distinct in the database, equal under OrdinalIgnoreCase.
        var source = Database(InSchema("dbo", Table("Orders", Column("Id")), Table("ORDERS", Column("Id"))));
        var target = Database(InSchema("dbo", Table("Orders", Column("Id"))));

        var result = Run(source, target);

        Assert.Equal(DifferenceStatus.Equal, Find(result, DatabaseObjectType.Table, "Orders").Status);
        Assert.Equal(DifferenceStatus.SourceOnly, Find(result, DatabaseObjectType.Table, "ORDERS").Status);
    }
}
