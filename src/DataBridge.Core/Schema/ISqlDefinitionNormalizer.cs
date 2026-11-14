namespace DataBridge.Core.Schema;

/// <summary>
/// Normalizes SQL text (module definitions and stored expressions) before comparison, so that differences with
/// no effect on meaning, such as line endings and indentation, are not reported (FR-SCH-011). The rules are
/// provider-specific; implementations must never change string literals, quoted identifiers or where a line
/// comment ends.
/// </summary>
public interface ISqlDefinitionNormalizer
{
    string Normalize(string sql);
}

/// <summary>Compares SQL text exactly, apart from leading and trailing whitespace.</summary>
public sealed class ExactSqlDefinitionNormalizer : ISqlDefinitionNormalizer
{
    public static readonly ExactSqlDefinitionNormalizer Instance = new();

    public string Normalize(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return sql.Trim();
    }
}
