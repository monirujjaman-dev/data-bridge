using System.Text;
using DataBridge.Core.Schema;

namespace DataBridge.Infrastructure.Providers.SqlServer.Schema;

/// <summary>
/// Normalizes T-SQL text for comparison (FR-SCH-011) by scanning it with T-SQL's lexical rules:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>String literals (<c>'...'</c>, with <c>''</c> escapes), bracketed identifiers (<c>[...]</c>) and
/// double-quoted text (an identifier or, with <c>QUOTED_IDENTIFIER OFF</c>, a literal) are kept exactly.</item>
/// <item>Comment text is kept; only CRLF line endings inside block comments become LF.</item>
/// <item>Outside those, a run of whitespace that contains a line break becomes one line break, and any other
/// run becomes one space. A line break is kept, never turned into a space, because it ends a <c>--</c> comment.</item>
/// <item>Leading and trailing whitespace is removed.</item>
/// </list>
/// Keyword and identifier case is never changed: identifiers can be case-sensitive.
/// </remarks>
internal sealed class TSqlDefinitionNormalizer : ISqlDefinitionNormalizer
{
    public static readonly TSqlDefinitionNormalizer Instance = new();

    public string Normalize(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var output = new StringBuilder(sql.Length);
        var position = 0;
        while (position < sql.Length)
        {
            var current = sql[position];
            var next = position + 1 < sql.Length ? sql[position + 1] : '\0';
            position = current switch
            {
                '\'' => CopyQuoted(sql, position, '\'', output),
                '"' => CopyQuoted(sql, position, '"', output),
                '[' => CopyQuoted(sql, position, ']', output),
                '-' when next == '-' => CopyLineComment(sql, position, output),
                '/' when next == '*' => CopyBlockComment(sql, position, output),
                _ when char.IsWhiteSpace(current) => CollapseWhitespace(sql, position, output),
                _ => Copy(current, position, output),
            };
        }

        return output.ToString().Trim();
    }

    private static int Copy(char character, int position, StringBuilder output)
    {
        output.Append(character);
        return position + 1;
    }

    /// <summary>Copies a quoted token through its closing quote; a doubled closing quote is an escape.</summary>
    private static int CopyQuoted(string sql, int start, char closing, StringBuilder output)
    {
        output.Append(sql[start]);
        var position = start + 1;
        while (position < sql.Length)
        {
            var character = sql[position];
            output.Append(character);
            position++;
            if (character != closing)
            {
                continue;
            }

            if (position < sql.Length && sql[position] == closing)
            {
                output.Append(closing);
                position++;
                continue;
            }

            return position;
        }

        return position;
    }

    /// <summary>Copies a <c>--</c> comment up to, not including, the line break that ends it.</summary>
    private static int CopyLineComment(string sql, int start, StringBuilder output)
    {
        var end = sql.IndexOfAny(['\r', '\n'], start);
        end = end < 0 ? sql.Length : end;
        output.Append(sql, start, end - start);
        return end;
    }

    /// <summary>Copies a <c>/* */</c> comment, which T-SQL lets nest, with CRLF turned into LF.</summary>
    private static int CopyBlockComment(string sql, int start, StringBuilder output)
    {
        var depth = 0;
        var position = start;
        while (position < sql.Length)
        {
            if (sql[position] == '/' && position + 1 < sql.Length && sql[position + 1] == '*')
            {
                depth++;
                output.Append("/*");
                position += 2;
            }
            else if (sql[position] == '*' && position + 1 < sql.Length && sql[position + 1] == '/')
            {
                depth--;
                output.Append("*/");
                position += 2;
                if (depth == 0)
                {
                    return position;
                }
            }
            else if (sql[position] == '\r' && position + 1 < sql.Length && sql[position + 1] == '\n')
            {
                output.Append('\n');
                position += 2;
            }
            else
            {
                output.Append(sql[position]);
                position++;
            }
        }

        return position;
    }

    private static int CollapseWhitespace(string sql, int start, StringBuilder output)
    {
        var position = start;
        var hasLineBreak = false;
        while (position < sql.Length && char.IsWhiteSpace(sql[position]))
        {
            hasLineBreak |= sql[position] is '\r' or '\n';
            position++;
        }

        output.Append(hasLineBreak ? '\n' : ' ');
        return position;
    }
}
