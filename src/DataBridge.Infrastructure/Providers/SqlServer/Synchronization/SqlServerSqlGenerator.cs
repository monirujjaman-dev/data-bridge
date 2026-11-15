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
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    /// <summary>Every sequence an editor may treat as a line break (CR LF first, so it counts once).</summary>
    private static readonly string[] _lineBreaks =
        ["\r\n", "\r", "\n", ((char)0x0B).ToString(), ((char)0x0C).ToString(), ((char)0x85).ToString(), LineSeparator.ToString(), ParagraphSeparator.ToString()];

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
        Comment(script, "It stops unless the database name, server name and database GUID match. A failover to another server stops it;");
        Comment(script, "a restore over the same database keeps the GUID (unless restored WITH NEW_BROKER) and does not.");
        Comment(script, "A stopped script leaves SET NOEXEC ON in the session; run SET NOEXEC OFF before using it for anything else.");
        script.AppendLine();
        var hazardous = plan.Operations
            .Where(operation => !operation.IsDataOperation && operation.SqlBatches.Any(batch => ScriptHazard(batch) is not null))
            .Select(operation => operation.Id)
            .ToHashSet();
        if (hazardous.Count > 0)
        {
            // A batch the tool would split or interpret is shown commented out, so the script as a whole must not run.
            script.AppendLine("RAISERROR(N'This script contains definitions that cannot be shown as runnable SQL; they are commented out below. Do not run it.', 16, 1);");
            script.AppendLine("SET NOEXEC ON;");
            script.AppendLine("GO");
        }

        // The database name, the server and (when known) the database GUID must all match the planned write target. The
        // names come from the server and can contain anything, including line breaks and "$(", which SSMS and sqlcmd
        // would act on, so they are written as hex that those tools cannot read into.
        script.AppendLine(CultureInfo.InvariantCulture, $"DECLARE @Database nvarchar(128) = {HexText(plan.WriteTargetDatabaseName, 128)},");
        script.AppendLine(CultureInfo.InvariantCulture, $"    @Server nvarchar(256) = {HexText(plan.WriteTargetServerName, 256)};");
        script.AppendLine("IF DB_NAME() COLLATE Latin1_General_BIN2 <> @Database COLLATE Latin1_General_BIN2");
        script.AppendLine("    OR UPPER(CONVERT(nvarchar(256), SERVERPROPERTY('ServerName'))) COLLATE Latin1_General_BIN2 <> UPPER(@Server) COLLATE Latin1_General_BIN2");
        if (plan.WriteTargetDatabaseGuid is { } guid)
        {
            script.AppendLine(CultureInfo.InvariantCulture, $"    OR NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND service_broker_guid = '{guid:D}')");
        }

        script.AppendLine("BEGIN");
        script.AppendLine("    RAISERROR(N'This script must run in database %s on server %s.', 16, 1, @Database, @Server);");
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

            if (hazardous.Contains(operation.Id))
            {
                var reason = operation.SqlBatches.Select(ScriptHazard).First(hazard => hazard is not null);
                Comment(script, $"   Not runnable from this script: {reason}. Its SQL, for review only:");
                foreach (var batch in operation.SqlBatches)
                {
                    Comment(script, "   " + batch);
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
        if (FixedInPlace(column) || FixedInPlace(existing))
        {
            throw new UnsupportedChangeException("Identity, sparse and ROWGUIDCOL columns cannot be changed with ALTER COLUMN.");
        }

        var collation = column.CollationName is { } name ? $" COLLATE {Option(name)}" : "";
        return $"ALTER TABLE {Quote(detail.Table)} ALTER COLUMN {Quote(existing.Name)} {TypeName(column)}{collation}{(column.IsNullable ? " NULL" : " NOT NULL")};";
    }

    private static bool FixedInPlace(ColumnDefinition column) =>
        column.Identity is not null || column.EffectiveOptions.IsSparse || column.EffectiveOptions.IsRowGuidColumn;

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
        var qualified = QualifyHeader(text, detail.Id);
        var statement = detail.IsAlter ? CreateOrAlter(qualified) : qualified.Trim();
        return
        [
            $"SET ANSI_NULLS {OnOff(detail.Module.UsesAnsiNulls)};\nSET QUOTED_IDENTIFIER {OnOff(detail.Module.UsesQuotedIdentifier)};",
            statement,
        ];
    }

    /// <summary>
    /// Makes sure a stored definition creates the object the plan names: its header must name <paramref name="id"/>
    /// (and, for a trigger, its table). A name without a schema is qualified, because it would otherwise resolve to the
    /// executing login's default schema; any other name, for example the old name of a module renamed with
    /// <c>sp_rename</c>, is refused.
    /// </summary>
    internal static string QualifyHeader(string definition, DatabaseObjectId id)
    {
        var position = SkipTrivia(definition, 0);
        if (!IsKeyword(definition, position, "CREATE"))
        {
            throw new UnsupportedChangeException("The definition does not start with CREATE.");
        }

        position = SkipTrivia(definition, position + "CREATE".Length);
        if (IsKeyword(definition, position, "OR"))
        {
            position = SkipTrivia(definition, position + "OR".Length);
            if (!IsKeyword(definition, position, "ALTER"))
            {
                throw new UnsupportedChangeException("The definition's header could not be read.");
            }

            position = SkipTrivia(definition, position + "ALTER".Length);
        }

        string[] keywords = id.ObjectType switch
        {
            DatabaseObjectType.View => ["VIEW"],
            DatabaseObjectType.StoredProcedure => ["PROCEDURE", "PROC"],
            DatabaseObjectType.ScalarFunction or DatabaseObjectType.TableValuedFunction => ["FUNCTION"],
            DatabaseObjectType.Trigger => ["TRIGGER"],
            _ => throw new UnsupportedChangeException($"{id.ObjectType} is not a module."),
        };
        var keyword = keywords.FirstOrDefault(word => IsKeyword(definition, position, word))
            ?? throw new UnsupportedChangeException($"The definition does not create a {keywords[0].ToLowerInvariant()}.");
        position = SkipTrivia(definition, position + keyword.Length);

        var result = Qualify(definition, ref position, id.SchemaName, id.ObjectName, "object", StringComparison.Ordinal);
        if (id.ObjectType == DatabaseObjectType.Trigger)
        {
            var offset = result.Length - definition.Length;
            position = SkipTrivia(result, position + offset);
            if (!IsKeyword(result, position, "ON"))
            {
                throw new UnsupportedChangeException("The trigger's header could not be read.");
            }

            position = SkipTrivia(result, position + "ON".Length);
            result = Qualify(result, ref position, id.SchemaName, id.ParentObjectName!, "table", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// Reads the one- or two-part name at <paramref name="position"/>, checks it and qualifies it if needed. The schema
    /// is compared ignoring case, since a header may spell an existing schema differently. The object's own name is
    /// compared exactly: the engine stores it as the header spelled it, so a header differing even in case names
    /// something else, for example before a rename to a case variant in a case-sensitive database. A trigger's table
    /// is compared ignoring case, since the header may spell an existing table differently.
    /// </summary>
    private static string Qualify(string text, ref int position, string schema, string name, string what, StringComparison nameComparison)
    {
        var start = position;
        var parts = new List<string>();
        while (true)
        {
            parts.Add(ReadIdentifier(text, ref position));
            var next = SkipTrivia(text, position);
            if (next < text.Length && text[next] == '.')
            {
                position = SkipTrivia(text, next + 1);
                continue;
            }

            break;
        }

        var sameName = string.Equals(parts[^1], name, nameComparison);
        switch (parts.Count)
        {
            case 2 when sameName && string.Equals(parts[0], schema, StringComparison.OrdinalIgnoreCase):
                return text;
            case 1 when sameName:
                return string.Concat(text.AsSpan(0, start), Quote(schema), ".", text.AsSpan(start));
            default:
                throw new UnsupportedChangeException(
                    $"The definition names the {what} {string.Join(".", parts)}, not {schema}.{name}; it may have been renamed. Change it manually.");
        }
    }

    private static string ReadIdentifier(string text, ref int position)
    {
        if (position >= text.Length)
        {
            throw new UnsupportedChangeException("The definition's header could not be read.");
        }

        var (open, close) = text[position] switch
        {
            '[' => ('[', ']'),
            '"' => ('"', '"'),
            _ => ('\0', '\0'),
        };
        if (open != '\0')
        {
            var builder = new StringBuilder();
            position++;
            while (position < text.Length)
            {
                if (text[position] == close)
                {
                    if (position + 1 < text.Length && text[position + 1] == close)
                    {
                        builder.Append(close);
                        position += 2;
                        continue;
                    }

                    position++;
                    return builder.ToString();
                }

                builder.Append(text[position++]);
            }

            throw new UnsupportedChangeException("The definition's header could not be read.");
        }

        var begin = position;
        while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] is '_' or '@' or '#' or '$'))
        {
            position++;
        }

        return position > begin ? text[begin..position] : throw new UnsupportedChangeException("The definition's header could not be read.");
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

    /// <summary>
    /// <paramref name="value"/> as <c>CAST(0x... AS nvarchar(length))</c> over its UTF-16LE bytes: an expression with
    /// no letters a script tool could take as a line, separator or variable, whatever the text contains.
    /// </summary>
    internal static string HexText(string value, int length) =>
        $"CAST(0x{Convert.ToHexString(Encoding.Unicode.GetBytes(value))} AS nvarchar({Number(length)}))";

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Comment(StringBuilder script, string text)
    {
        foreach (var line in text.Split('\n'))
        {
            // Any other line break (bare CR, NEL, line or paragraph separator) would end the comment in some tools.
            var safe = string.Create(line.Length, line, static (span, source) =>
            {
                for (var index = 0; index < source.Length; index++)
                {
                    var character = source[index];
                    span[index] = char.IsControl(character) || character is LineSeparator or ParagraphSeparator ? ' ' : character;
                }
            });
            // SQLCMD mode substitutes $(name) even inside comments, so the pair is broken up.
            script.Append("-- ").AppendLine(safe.Replace("$(", "$ (", StringComparison.Ordinal).TrimEnd());
        }
    }

    /// <summary>
    /// Why a batch cannot be written to a script as runnable SQL, or <see langword="null"/>. SSMS and sqlcmd read a
    /// script line by line before SQL Server sees it, inside brackets, strings and comments too: a line starting with
    /// <c>GO</c> ends the batch, and in SQLCMD mode lines starting with <c>:</c>, <c>!!</c>, <c>exit</c>, <c>quit</c>,
    /// <c>reset</c> or <c>ed</c> are commands and <c>$(</c> is a variable. Text copied from a database could use any of
    /// them to run statements the plan does not contain, so they are refused, broadly (a false match only means the
    /// operation is shown commented out). Every Unicode line break counts, because editors split lines on them.
    /// DataBridge itself sends each batch separately, so this applies only to the exported script.
    /// </summary>
    internal static string? ScriptHazard(string batch)
    {
        if (batch.Contains("$(", StringComparison.Ordinal))
        {
            return "it contains $(, which SQLCMD mode treats as a variable";
        }

        foreach (var line in batch.Split(_lineBreaks, StringSplitOptions.None))
        {
            var start = line.TrimStart();
            if (SeparatorLine().IsMatch(start))
            {
                return "a line in it starts with GO, which would end the batch";
            }

            if (start.StartsWith(':') || start.StartsWith("!!", StringComparison.Ordinal) || SqlcmdCommandLine().IsMatch(start))
            {
                return "a line in it looks like a sqlcmd command";
            }
        }

        return null;
    }

    [GeneratedRegex(@"^GO(?![A-Za-z0-9_@#$])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SeparatorLine();

    [GeneratedRegex(@"^(?:exit|quit|reset|ed)(?![A-Za-z0-9_@#$])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SqlcmdCommandLine();

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SystemTypeName();

    [GeneratedRegex("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainWord();

    private sealed class UnsupportedChangeException(string message) : Exception(message);
}
