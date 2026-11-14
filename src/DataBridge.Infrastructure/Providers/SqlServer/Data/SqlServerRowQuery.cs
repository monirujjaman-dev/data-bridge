using System.Globalization;
using System.Text;
using DataBridge.Application.Abstractions;
using DataBridge.Core.Data;
using DataBridge.Core.Schema;

namespace DataBridge.Infrastructure.Providers.SqlServer.Data;

/// <summary>SQL Server identifier quoting (NFR-SEC-002): brackets, with <c>]</c> doubled.</summary>
internal static class SqlServerIdentifier
{
    public static string Quote(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Length > 128)
        {
            throw new ArgumentException("SQL Server identifiers are at most 128 characters.", nameof(name));
        }

        return $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    public static string Quote(DatabaseObjectId table) => $"{Quote(table.SchemaName)}.{Quote(table.ObjectName)}";
}

/// <summary>
/// Builds the ordered read of one table (ADR-0014): every key and compared column, ordered by the key. String keys
/// are ordered with <c>Latin1_General_100_BIN2</c>, a binary order the comparer's ordinal text order matches (the merge
/// verifies it), so the order does not depend on either database's collation; <c>char</c> and <c>varchar</c> keys are converted to Unicode first so the
/// order is by character, not by code-page byte. The query only reads and contains no values.
/// </summary>
internal static class SqlServerRowQuery
{
    public const string BinaryCollation = "Latin1_General_100_BIN2";

    public static string Build(TableReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.KeyColumns.Count == 0)
        {
            throw new ArgumentException("A key is required to read rows in order.", nameof(request));
        }

        var select = request.KeyColumns.Concat(request.ValueColumns).Select(column => SelectExpression(column.Column));
        var order = request.KeyColumns.Select(column => OrderExpression(column));
        return new StringBuilder()
            .Append("SELECT ").AppendJoin(", ", select)
            .Append(" FROM ").Append(SqlServerIdentifier.Quote(request.TableId))
            .Append(" ORDER BY ").AppendJoin(", ", order)
            .Append(';')
            .ToString();
    }

    /// <summary>Base type name in lower case: the alias type's base, or the column's own type.</summary>
    public static string TypeOf(ColumnDefinition column) => (column.ProviderBaseTypeName ?? column.SqlTypeName).ToLowerInvariant();

    private static string SelectExpression(ColumnDefinition column) => SqlServerIdentifier.Quote(column.Name);

    private static string OrderExpression(ReadColumn key)
    {
        var name = SqlServerIdentifier.Quote(key.Column.Name);
        if (key.Kind != ValueKind.Text)
        {
            return $"{name} ASC";
        }

        var type = TypeOf(key.Column);
        if (type is "nchar" or "nvarchar")
        {
            return $"{name} COLLATE {BinaryCollation} ASC";
        }

        var length = key.Column.IsMaxLength || key.Column.MaxLength is null or > 4000
            ? "max"
            : key.Column.MaxLength.Value.ToString(CultureInfo.InvariantCulture);
        return $"CAST({name} AS nvarchar({length})) COLLATE {BinaryCollation} ASC";
    }
}
