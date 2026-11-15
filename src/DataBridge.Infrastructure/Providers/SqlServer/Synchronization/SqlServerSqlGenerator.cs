using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DataBridge.Application.Abstractions;
using DataBridge.Core.Connections;
using DataBridge.Core.Schema;
using DataBridge.Core.Synchronization;
using DataBridge.Infrastructure.Providers.SqlServer.Data;
using DataBridge.Shared.Results;

namespace DataBridge.Infrastructure.Providers.SqlServer.Synchronization;

/// <summary>
/// T-SQL for planned changes (SQL Server 2017 and later, ADR-0010). Identifiers are always bracket-quoted; type,
/// collation and option names are only emitted when they match a strict pattern, so text read from one database can
/// never inject SQL into the other. Definitions the engine stores (module text, check, default, computed and filter
/// expressions) are copied as stored. DML is a parameterized template (<c>@k0</c> for key values, <c>@p0</c> for
/// column values) and never contains row values (FR-EXE-003).
/// </summary>
internal sealed partial class SqlServerSqlGenerator : ISqlGenerator
{
    public DatabaseProviderType ProviderType => DatabaseProviderType.SqlServer;

    public Result<IReadOnlyList<string>> Generate(PlannedChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        try
        {
            return Result.Success(change.Detail switch
            {
                CreateSchemaDetail schema => One($"CREATE SCHEMA {Quote(schema.Name)};"),
                DropSchemaDetail schema => One($"DROP SCHEMA {Quote(schema.Name)};"),
                CreateTableDetail table => One(CreateTable(table.Table)),
                DropTableDetail table => One($"DROP TABLE {Quote(table.Table)};"),
                AddColumnDetail column => One($"ALTER TABLE {Quote(column.Table)} ADD {ColumnDefinition(column.Column, column.Default)};"),
                AlterColumnDetail column => One(AlterColumn(column)),
                DropColumnDetail column => One($"ALTER TABLE {Quote(column.Table)} DROP COLUMN {Quote(column.ColumnName)};"),
                AddConstraintDetail constraint => One(AddConstraint(constraint)),
                DropConstraintDetail constraint => One($"ALTER TABLE {Quote(constraint.Table)} DROP CONSTRAINT {Quote(constraint.Name)};"),
                CreateIndexDetail index => One(CreateIndex(index.Parent, index.Index)),
                DropIndexDetail index => One($"DROP INDEX {Quote(index.Name)} ON {Quote(index.Parent)};"),
                CreateModuleDetail module => Module(module),
                DropModuleDetail module => One($"DROP {ModuleKeyword(module.Id.ObjectType)} {Quote(module.Id)};"),
                CreateSequenceDetail sequence => One(Sequence(sequence)),
                DropSequenceDetail sequence => One($"DROP SEQUENCE {Quote(sequence.Id)};"),
                RowsDetail rows => One(Rows(change.Type, rows)),
                _ => throw new UnsupportedChangeException("This kind of change has no SQL Server translation."),
            });
        }
        catch (UnsupportedChangeException exception)
        {
            return Result.Failure<IReadOnlyList<string>>(SynchronizationUseCaseErrors.Unsupported(exception.Message));
        }
    }

    public string BuildScript(SynchronizationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var script = new StringBuilder();
        Comment(script, "DataBridge synchronization plan");
        Comment(script, $"Plan:         {plan.Id}");
        Comment(script, $"Content hash: {plan.ContentHash}");
        Comment(script, $"Direction:    {plan.Direction}");
        Comment(script, $"Writes to:    {plan.WriteTargetDatabaseName}" + (plan.WriteTargetEnvironment == EnvironmentLabel.None ? "" : $" ({plan.WriteTargetEnvironment})"));
        Comment(script, $"Created:      {plan.CreatedAtUtc.ToString("u", CultureInfo.InvariantCulture)}");
        Comment(script, "Review only. Running this script yourself bypasses DataBridge's approval, revalidation and drift checks.");
        Comment(script, "There is no transaction around the whole script; a failure part-way leaves earlier steps applied.");
        script.AppendLine();
        script.AppendLine(CultureInfo.InvariantCulture, $"IF DB_NAME() <> {Literal(plan.WriteTargetDatabaseName)}");
        script.AppendLine("BEGIN");
        script.AppendLine(CultureInfo.InvariantCulture, $"    RAISERROR(N'This script must run in database %s.', 16, 1, {Literal(plan.WriteTargetDatabaseName)});");
        script.AppendLine("    SET NOEXEC ON;");
        script.AppendLine("END");
        script.AppendLine("GO");

        foreach (var operation in plan.Operations)
        {
            script.AppendLine();
            Comment(script, $"{operation.Order + 1}. {operation.Description}");
            Comment(script, $"   Risk: {operation.Risk}" + (operation.IsDestructive ? ", destructive" : "") + (operation.RequiresExplicitApproval ? ", needs individual approval" : ""));
            foreach (var warning in operation.Warnings)
            {
                Comment(script, $"   Warning: {warning}");
            }

            if (operation.IsDataOperation)
            {
                Comment(script, $"   Run by DataBridge once per row ({operation.EstimatedAffectedRows:N0} rows) with parameters, after re-checking each row:");
                foreach (var line in operation.SqlBatches.SelectMany(batch => batch.Split('\n')))
                {
                    Comment(script, "   " + line.TrimEnd('\r'));
                }

                continue;
            }

            foreach (var batch in operation.SqlBatches)
            {
                script.AppendLine(batch);
                script.AppendLine("GO");
            }
        }

        if (plan.NotIncluded.Count > 0)
        {
            script.AppendLine();
            Comment(script, "Selected but not included:");
            foreach (var issue in plan.NotIncluded)
            {
                Comment(script, "- " + issue.Message);
            }
        }

        return script.ToString();
    }

    // Tables and columns

    private static string CreateTable(TableDefinition table)
    {
        var lines = new List<string>();
        foreach (var column in table.Columns.OrderBy(column => column.OrdinalPosition))
        {
            var defaultConstraint = table.DefaultConstraints.FirstOrDefault(item => string.Equals(item.ColumnName, column.Name, StringComparison.Ordinal));
            lines.Add(ColumnDefinition(column, defaultConstraint));
        }

        if (table.PrimaryKey is { } primaryKey)
        {
            lines.Add($"{ConstraintName(primaryKey.Name, primaryKey.IsSystemNamed)}PRIMARY KEY {IndexType(primaryKey.IndexType)} {KeyColumns(primaryKey.Columns)}{IndexOptions(primaryKey.Options, unique: true)}");
        }

        foreach (var unique in table.UniqueConstraints)
        {
            lines.Add($"{ConstraintName(unique.Name, unique.IsSystemNamed)}UNIQUE {IndexType(unique.IndexType)} {KeyColumns(unique.Columns)}{IndexOptions(unique.Options, unique: true)}");
        }

        foreach (var check in table.CheckConstraints)
        {
            lines.Add($"{ConstraintName(check.Name, check.IsSystemNamed)}CHECK {(check.IsNotForReplication ? "NOT FOR REPLICATION " : "")}{Expression(check.Expression)}");
        }

        var sql = new StringBuilder();
        sql.Append(CultureInfo.InvariantCulture, $"CREATE TABLE {Quote(table.Id)} (\n    ");
        sql.AppendJoin(",\n    ", lines);
        sql.Append("\n)");
        if (table.HeapDataCompression is { } compression && !string.Equals(compression, "NONE", StringComparison.OrdinalIgnoreCase))
        {
            sql.Append(CultureInfo.InvariantCulture, $" WITH (DATA_COMPRESSION = {Option(compression)})");
        }

        return sql.Append(';').ToString();
    }

    private static string ColumnDefinition(ColumnDefinition column, DefaultConstraintDefinition? defaultConstraint)
    {
        if (column.Computed is { } computed)
        {
            return $"{Quote(column.Name)} AS {Expression(computed.Expression)}{(computed.IsPersisted ? " PERSISTED" : "")}{(computed.IsPersisted && !column.IsNullable ? " NOT NULL" : "")}";
        }

        var sql = new StringBuilder();
        sql.Append(Quote(column.Name)).Append(' ').Append(TypeName(column));
        if (column.CollationName is { } collation)
        {
            sql.Append(" COLLATE ").Append(Option(collation));
        }

        var options = column.EffectiveOptions;
        if (options.IsSparse)
        {
            sql.Append(" SPARSE");
        }

        if (defaultConstraint is not null)
        {
            sql.Append(' ').Append(ConstraintName(defaultConstraint.Name, defaultConstraint.IsSystemNamed)).Append("DEFAULT ").Append(Expression(defaultConstraint.Expression));
        }

        if (column.Identity is { } identity)
        {
            sql.Append(CultureInfo.InvariantCulture, $" IDENTITY({identity.Seed.ToString(CultureInfo.InvariantCulture)}, {identity.Increment.ToString(CultureInfo.InvariantCulture)})");
            if (identity.IsNotForReplication)
            {
                sql.Append(" NOT FOR REPLICATION");
            }
        }

        if (options.IsRowGuidColumn)
        {
            sql.Append(" ROWGUIDCOL");
        }

        return sql.Append(column.IsNullable ? " NULL" : " NOT NULL").ToString();
    }

    private static string AlterColumn(AlterColumnDetail detail)
    {
        var column = detail.Column;
        var existing = detail.Existing;
        if (column.Identity is not null || existing.Identity is not null || existing.EffectiveOptions.IsSparse || existing.EffectiveOptions.IsRowGuidColumn)
        {
            throw new UnsupportedChangeException("Identity, sparse and ROWGUIDCOL columns cannot be changed with ALTER COLUMN.");
        }

        var collation = column.CollationName is { } name ? $" COLLATE {Option(name)}" : "";
        return $"ALTER TABLE {Quote(detail.Table)} ALTER COLUMN {Quote(existing.Name)} {TypeName(column)}{collation}{(column.IsNullable ? " NULL" : " NOT NULL")};";
    }

    /// <summary>A column's type with its facets; alias types are schema-qualified and quoted.</summary>
    internal static string TypeName(ColumnDefinition column)
    {
        if (column.ProviderBaseTypeName is not null)
        {
            var separator = column.SqlTypeName.IndexOf('.', StringComparison.Ordinal);
            return separator > 0
                ? $"{Quote(column.SqlTypeName[..separator])}.{Quote(column.SqlTypeName[(separator + 1)..])}"
                : Quote(column.SqlTypeName);
        }

        var type = column.SqlTypeName.ToLowerInvariant();
        if (!SystemTypeName().IsMatch(type))
        {
            throw new UnsupportedChangeException($"The type name {column.SqlTypeName} is not a SQL Server system type.");
        }

        return type switch
        {
            "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary" =>
                column.IsMaxLength ? $"{type}(max)" : $"{type}({Number(column.MaxLength ?? 1)})",
            "decimal" or "numeric" => $"{type}({Number(column.Precision ?? 18)}, {Number(column.Scale ?? 0)})",
            "datetime2" or "time" or "datetimeoffset" => column.Scale is { } scale ? $"{type}({Number(scale)})" : type,
            _ => type,
        };
    }

    // Constraints and indexes

    private static string AddConstraint(AddConstraintDetail detail)
    {
        var table = Quote(detail.Table);
        var check = detail.WithNoCheck ? "WITH NOCHECK " : "WITH CHECK ";
        return detail.Constraint switch
        {
            PrimaryKeyDefinition key =>
                $"ALTER TABLE {table} ADD {ConstraintName(key.Name, key.IsSystemNamed)}PRIMARY KEY {IndexType(key.IndexType)} {KeyColumns(key.Columns)}{IndexOptions(key.Options, unique: true)};",
            UniqueConstraintDefinition key =>
                $"ALTER TABLE {table} ADD {ConstraintName(key.Name, key.IsSystemNamed)}UNIQUE {IndexType(key.IndexType)} {KeyColumns(key.Columns)}{IndexOptions(key.Options, unique: true)};",
            ForeignKeyDefinition key =>
                $"ALTER TABLE {table} {check}ADD {ConstraintName(key.Name, key.IsSystemNamed)}FOREIGN KEY ({string.Join(", ", key.Columns.Select(mapping => Quote(mapping.ColumnName)))}) "
                + $"REFERENCES {Quote(key.ReferencedSchemaName!)}.{Quote(key.ReferencedTableName!)} ({string.Join(", ", key.Columns.Select(mapping => Quote(mapping.ReferencedColumnName)))}) "
                + $"ON DELETE {Action(key.OnDelete)} ON UPDATE {Action(key.OnUpdate)}{(key.IsNotForReplication ? " NOT FOR REPLICATION" : "")};",
            CheckConstraintDefinition constraint =>
                $"ALTER TABLE {table} {check}ADD {ConstraintName(constraint.Name, constraint.IsSystemNamed)}CHECK {(constraint.IsNotForReplication ? "NOT FOR REPLICATION " : "")}{Expression(constraint.Expression)};",
            DefaultConstraintDefinition constraint =>
                $"ALTER TABLE {table} ADD {ConstraintName(constraint.Name, constraint.IsSystemNamed)}DEFAULT {Expression(constraint.Expression)} FOR {Quote(constraint.ColumnName)};",
            _ => throw new UnsupportedChangeException("Unknown constraint kind."),
        };
    }

    private static string CreateIndex(DatabaseObjectId parent, IndexDefinition index)
    {
        var keys = index.Columns.Where(column => !column.IsIncluded).OrderBy(column => column.KeyOrdinal).ToArray();
        var included = index.Columns.Where(column => column.IsIncluded).Select(column => Quote(column.ColumnName)).ToArray();
        var sql = new StringBuilder();
        sql.Append(CultureInfo.InvariantCulture, $"CREATE {(index.IsUnique ? "UNIQUE " : "")}{IndexType(index.IndexType)} INDEX {Quote(index.Name)} ON {Quote(parent)} {KeyColumns(keys)}");
        if (included.Length > 0)
        {
            sql.Append(" INCLUDE (").AppendJoin(", ", included).Append(')');
        }

        if (index.FilterDefinition is { } filter)
        {
            sql.Append(" WHERE ").Append(Expression(filter));
        }

        return sql.Append(IndexOptions(index.Options, index.IsUnique)).Append(';').ToString();
    }

    private static string KeyColumns(IEnumerable<IndexColumnDefinition> columns) =>
        $"({string.Join(", ", columns.Select(column => $"{Quote(column.ColumnName)} {(column.IsDescending ? "DESC" : "ASC")}"))})";

    private static string IndexOptions(IndexOptions options, bool unique)
    {
        var parts = new List<string> { $"PAD_INDEX = {OnOff(options.IsPadded)}" };
        if (options.FillFactor is > 0 and < 100)
        {
            parts.Add($"FILLFACTOR = {Number(options.FillFactor)}");
        }

        if (unique)
        {
            parts.Add($"IGNORE_DUP_KEY = {OnOff(options.IgnoreDuplicateKey)}");
        }

        parts.Add($"ALLOW_ROW_LOCKS = {OnOff(options.AllowRowLocks)}");
        parts.Add($"ALLOW_PAGE_LOCKS = {OnOff(options.AllowPageLocks)}");
        if (options.DataCompression is { } compression)
        {
            parts.Add($"DATA_COMPRESSION = {Option(compression)}");
        }

        return $" WITH ({string.Join(", ", parts)})";
    }

    private static string IndexType(string type) =>
        type.ToUpperInvariant() is "CLUSTERED" or "NONCLUSTERED"
            ? type.ToUpperInvariant()
            : throw new UnsupportedChangeException($"{type} indexes are not synchronized.");

    private static string Action(ReferentialAction action) => action switch
    {
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        _ => "NO ACTION",
    };

    // Modules and sequences

    private static IReadOnlyList<string> Module(CreateModuleDetail detail)
    {
        var text = detail.Module.Text ?? throw new UnsupportedChangeException("The definition is not visible.");
        var statement = detail.IsAlter ? CreateOrAlter(text) : text.Trim();
        return
        [
            $"SET ANSI_NULLS {OnOff(detail.Module.UsesAnsiNulls)};\nSET QUOTED_IDENTIFIER {OnOff(detail.Module.UsesQuotedIdentifier)};",
            statement,
        ];
    }

    /// <summary>
    /// Turns a stored <c>CREATE ...</c> definition into <c>CREATE OR ALTER ...</c>, so permissions on the object are kept.
    /// Skips leading whitespace and comments; any other start is refused rather than guessed at.
    /// </summary>
    internal static string CreateOrAlter(string definition)
    {
        var position = SkipTrivia(definition, 0);
        if (!IsKeyword(definition, position, "CREATE"))
        {
            throw new UnsupportedChangeException("The definition does not start with CREATE, so it cannot be turned into CREATE OR ALTER.");
        }

        var afterCreate = position + "CREATE".Length;
        var next = SkipTrivia(definition, afterCreate);
        if (IsKeyword(definition, next, "OR"))
        {
            return definition.Trim();
        }

        return (definition[..afterCreate] + " OR ALTER" + definition[afterCreate..]).Trim();
    }

    private static int SkipTrivia(string text, int position)
    {
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position]))
            {
                position++;
            }
            else if (text.AsSpan(position).StartsWith("--", StringComparison.Ordinal))
            {
                var end = text.IndexOf('\n', position);
                position = end < 0 ? text.Length : end + 1;
            }
            else if (text.AsSpan(position).StartsWith("/*", StringComparison.Ordinal))
            {
                var depth = 0;
                do
                {
                    if (text.AsSpan(position).StartsWith("/*", StringComparison.Ordinal))
                    {
                        depth++;
                        position += 2;
                    }
                    else if (text.AsSpan(position).StartsWith("*/", StringComparison.Ordinal))
                    {
                        depth--;
                        position += 2;
                    }
                    else
                    {
                        position++;
                    }
                }
                while (depth > 0 && position < text.Length);
            }
            else
            {
                break;
            }
        }

        return position;
    }

    private static bool IsKeyword(string text, int position, string keyword) =>
        position + keyword.Length <= text.Length
        && string.Compare(text, position, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0
        && (position + keyword.Length == text.Length || !char.IsLetterOrDigit(text[position + keyword.Length]) && text[position + keyword.Length] != '_');

    private static string ModuleKeyword(DatabaseObjectType type) => type switch
    {
        DatabaseObjectType.View => "VIEW",
        DatabaseObjectType.StoredProcedure => "PROCEDURE",
        DatabaseObjectType.ScalarFunction or DatabaseObjectType.TableValuedFunction => "FUNCTION",
        DatabaseObjectType.Trigger => "TRIGGER",
        _ => throw new UnsupportedChangeException($"{type} is not a module."),
    };

    private static string Sequence(CreateSequenceDetail detail)
    {
        var sequence = detail.Sequence;
        var options = new StringBuilder();
        options.Append(CultureInfo.InvariantCulture, $" INCREMENT BY {sequence.Increment.ToString(CultureInfo.InvariantCulture)}");
        options.Append(CultureInfo.InvariantCulture, $" MINVALUE {sequence.MinimumValue.ToString(CultureInfo.InvariantCulture)}");
        options.Append(CultureInfo.InvariantCulture, $" MAXVALUE {sequence.MaximumValue.ToString(CultureInfo.InvariantCulture)}");
        options.Append(sequence.IsCycling ? " CYCLE" : " NO CYCLE");
        options.Append(sequence.IsCached
            ? sequence.CacheSize is { } size ? $" CACHE {Number(size)}" : " CACHE"
            : " NO CACHE");

        if (detail.IsAlter)
        {
            return $"ALTER SEQUENCE {Quote(sequence.Id)}{options};";
        }

        var type = sequence.SqlTypeName.ToLowerInvariant();
        if (!SystemTypeName().IsMatch(type))
        {
            throw new UnsupportedChangeException($"The sequence type {sequence.SqlTypeName} is not a SQL Server system type.");
        }

        return $"CREATE SEQUENCE {Quote(sequence.Id)} AS {type} START WITH {sequence.StartValue.ToString(CultureInfo.InvariantCulture)}{options};";
    }

    // Data

    /// <summary>The per-row template for a data operation; values are bound as parameters at execution.</summary>
    private static string Rows(SynchronizationOperationType type, RowsDetail detail)
    {
        var table = Quote(detail.Table.Id);
        var rows = detail.Rows;
        var where = string.Join(" AND ", rows.KeyColumns.Select((column, index) => $"{Quote(column)} = @k{Number(index)}"));
        return type switch
        {
            SynchronizationOperationType.DeleteRows => $"DELETE FROM {table} WHERE {where};",
            SynchronizationOperationType.UpdateRows =>
                $"UPDATE {table} SET {string.Join(", ", rows.Columns.Select((column, index) => $"{Quote(column)} = @p{Number(index)}"))} WHERE {where};",
            SynchronizationOperationType.InsertRows when detail.UsesIdentityInsert =>
                $"SET IDENTITY_INSERT {table} ON;\n{Insert(table, rows)}\nSET IDENTITY_INSERT {table} OFF;",
            SynchronizationOperationType.InsertRows => Insert(table, rows),
            _ => throw new UnsupportedChangeException("Not a data operation."),
        };
    }

    private static string Insert(string table, RowChangeSet rows) =>
        $"INSERT INTO {table} ({string.Join(", ", rows.Columns.Select(Quote))}) VALUES ({string.Join(", ", rows.Columns.Select((_, index) => $"@p{Number(index)}"))});";

    // Text helpers

    private static IReadOnlyList<string> One(string sql) => [sql];

    private static string Quote(string name) => SqlServerIdentifier.Quote(name);

    private static string Quote(DatabaseObjectId id) => $"{Quote(id.SchemaName)}.{Quote(id.ObjectName)}";

    private static string ConstraintName(string name, bool isSystemNamed) => isSystemNamed ? "" : $"CONSTRAINT {Quote(name)} ";

    private static string Expression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new UnsupportedChangeException("The definition is not visible.");
        }

        var trimmed = expression.Trim();
        return trimmed.StartsWith('(') && trimmed.EndsWith(')') ? trimmed : $"({trimmed})";
    }

    /// <summary>A collation or option keyword, only if it is a plain word.</summary>
    private static string Option(string value) =>
        PlainWord().IsMatch(value) ? value : throw new UnsupportedChangeException($"{value} is not a valid option name.");

    private static string Literal(string value) => $"N'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Comment(StringBuilder script, string text)
    {
        foreach (var line in text.Split('\n'))
        {
            script.Append("-- ").AppendLine(line.TrimEnd('\r'));
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SystemTypeName();

    [GeneratedRegex("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainWord();

    private sealed class UnsupportedChangeException(string message) : Exception(message);
}
