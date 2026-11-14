using System.Data.SqlTypes;
using DataBridge.Core.Comparison;
using DataBridge.Core.Data;
using DataBridge.Core.Schema;

namespace DataBridge.Infrastructure.Providers.SqlServer.Data;

/// <summary>
/// Maps SQL Server column types to comparison families. Alias types use their base type. Types whose values cannot
/// be read and compared exactly are <see cref="ValueKind.Unsupported"/>: <c>sql_variant</c>, CLR and spatial types,
/// <c>hierarchyid</c>, Always Encrypted columns (their values are ciphertext) and column sets.
/// </summary>
internal sealed class SqlServerValueKinds : IValueKindClassifier
{
    public static readonly SqlServerValueKinds Instance = new();

    public ValueKind Classify(ColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.EffectiveOptions.EncryptionType is not null || column.EffectiveOptions.IsColumnSet)
        {
            return ValueKind.Unsupported;
        }

        return KindOf(column.ProviderBaseTypeName ?? column.SqlTypeName);
    }

    public static ValueKind KindOf(string typeName) => typeName.ToLowerInvariant() switch
    {
        "tinyint" or "smallint" or "int" or "bigint" => ValueKind.WholeNumber,
        "decimal" or "numeric" or "money" or "smallmoney" => ValueKind.ExactNumeric,
        "float" or "real" => ValueKind.ApproximateNumeric,
        "bit" => ValueKind.Boolean,
        "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" => ValueKind.Text,
        "binary" or "varbinary" or "image" => ValueKind.Binary,
        "uniqueidentifier" => ValueKind.UniqueIdentifier,
        "date" => ValueKind.Date,
        "time" => ValueKind.Time,
        "datetime" or "datetime2" or "smalldatetime" => ValueKind.DateTime,
        "datetimeoffset" => ValueKind.DateTimeOffset,
        "xml" => ValueKind.Xml,
        "timestamp" or "rowversion" => ValueKind.RowVersion,
        _ => ValueKind.Unsupported,
    };
}

/// <summary>
/// SQL Server's key order. <c>uniqueidentifier</c> sorts by byte groups, last group first, which
/// <see cref="SqlGuid.CompareTo(SqlGuid)"/> reproduces. Text keys are ordered with a binary collation in the query, so
/// the default ordinal text order applies; the merge verifies that both agree.
/// </summary>
internal sealed class SqlServerKeyOrdering : DefaultKeyOrdering
{
    public static new readonly SqlServerKeyOrdering Instance = new();

    protected override int CompareGuids(Guid x, Guid y) => new SqlGuid(x).CompareTo(new SqlGuid(y));
}
