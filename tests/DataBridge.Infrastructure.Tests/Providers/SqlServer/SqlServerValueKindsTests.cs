using DataBridge.Core.Data;
using DataBridge.Core.Schema;
using DataBridge.Infrastructure.Providers.SqlServer.Data;

namespace DataBridge.Infrastructure.Tests.Providers.SqlServer;

public sealed class SqlServerValueKindsTests
{
    private static ColumnDefinition Column(string type, int? length = null, string? baseType = null, ColumnOptions? options = null) =>
        new("C", type, length, false, null, null, true, null, null, null, 1, options, baseType);

    [Theory]
    [InlineData("int", "bigint", true)]
    [InlineData("decimal", "money", true)]
    [InlineData("varchar", "nvarchar", true)]
    [InlineData("char", "varchar", false)]
    [InlineData("nchar", "nvarchar", false)]
    [InlineData("binary", "varbinary", false)]
    [InlineData("datetime", "datetime2", false)]
    [InlineData("smalldatetime", "datetime", false)]
    [InlineData("real", "float", false)]
    [InlineData("datetime2", "datetime2", true)]
    [InlineData("int", "nvarchar", false)]
    public void Columns_are_comparable_only_when_their_stored_values_compare_exactly(string source, string target, bool comparable) =>
        Assert.Equal(comparable, SqlServerValueKinds.Instance.AreComparable(Column(source, 10), Column(target, 10)));

    [Fact]
    public void Fixed_length_columns_must_have_the_same_length()
    {
        Assert.True(SqlServerValueKinds.Instance.AreComparable(Column("char", 10), Column("nchar", 10)));
        Assert.False(SqlServerValueKinds.Instance.AreComparable(Column("char", 10), Column("char", 12)));
    }

    [Fact]
    public void Alias_types_use_their_base_type_and_encrypted_columns_are_unsupported()
    {
        Assert.Equal(ValueKind.Text, SqlServerValueKinds.Instance.Classify(Column("dbo.Phone", baseType: "varchar")));
        Assert.Equal(ValueKind.Unsupported, SqlServerValueKinds.Instance.Classify(Column("varchar", options: ColumnOptions.None with { EncryptionType = "DETERMINISTIC" })));
        Assert.Equal(ValueKind.RowVersion, SqlServerValueKinds.Instance.Classify(Column("timestamp")));
        Assert.Equal(ValueKind.Unsupported, SqlServerValueKinds.Instance.Classify(Column("sql_variant")));
    }

    [Fact]
    public void Text_keys_are_ordered_with_a_binary_collation_and_varchar_is_converted_first()
    {
        var request = new DataBridge.Application.Abstractions.TableReadRequest(
            new DatabaseObjectId("dbo", "Odd]Name", DatabaseObjectType.Table),
            [
                new(Column("nvarchar", 10) with { Name = "A" }, ValueKind.Text),
                new(Column("varchar", 10) with { Name = "B" }, ValueKind.Text),
                new(Column("int") with { Name = "C" }, ValueKind.WholeNumber),
            ],
            [],
            100);

        Assert.Equal(
            "SELECT [A], [B], [C] FROM [dbo].[Odd]]Name] ORDER BY [A] COLLATE Latin1_General_100_BIN2 ASC, CAST([B] AS nvarchar(10)) COLLATE Latin1_General_100_BIN2 ASC, [C] ASC;",
            SqlServerRowQuery.Build(request));
    }
}
