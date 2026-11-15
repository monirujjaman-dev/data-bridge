using System.Data;
using System.Globalization;
using System.Text;
using DataBridge.Application.Abstractions;
using DataBridge.Core.Connections;
using DataBridge.Core.Schema;
using DataBridge.Core.Synchronization;
using DataBridge.Infrastructure.Providers.SqlServer.Data;
using DataBridge.Shared.Results;
using Microsoft.Data.SqlClient;

namespace DataBridge.Infrastructure.Providers.SqlServer.Synchronization;

/// <summary>
/// Validates a plan against the write target as it is now (FR-PLN-008): the login's permissions for every operation
/// (<c>HAS_PERMS_BY_NAME</c>), and probes for existing rows that would make an operation fail. Read-only: every
/// statement is a <c>SELECT</c>, probes stop at the first offending row (<c>TOP (1)</c>) and return no row values,
/// and only quoted identifiers are put into SQL text; securable names are parameters.
/// </summary>
/// <remarks>
/// Probes: rows in a table getting a NOT NULL column without a default; NULLs in a column becoming NOT NULL; duplicate
/// or NULL keys where a primary key, unique constraint or unique index is added; rows a new foreign key would reject.
/// Check constraints and filtered unique indexes are not probed, because that would mean running expression text
/// read from the other database; the plan already warns about them.
/// </remarks>
internal sealed class SqlServerTargetInspector : ISynchronizationTargetInspector
{
    private const int CommandTimeoutSeconds = 300;
    private const int PermissionChecksPerQuery = 200;

    public DatabaseProviderType ProviderType => DatabaseProviderType.SqlServer;

    public async Task<Result<IReadOnlyList<ValidationFinding>>> InspectAsync(
        DatabaseConnectionRequest writeTarget,
        AssembledPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeTarget);
        ArgumentNullException.ThrowIfNull(plan);

        var opened = false;
        try
        {
            await using var connection = SqlServerConnectionFactory.Create(writeTarget);
            await connection.OpenAsync(cancellationToken);
            opened = true;

            var created = CreatedObjects(plan);
            var findings = new List<ValidationFinding>();
            await CheckPermissionsAsync(connection, plan, created, findings, cancellationToken);
            foreach (var operation in plan.Plan.Operations)
            {
                foreach (var probe in Probes(plan.Details[operation.Id], created))
                {
                    if (await ExistsAsync(connection, probe.Sql, cancellationToken))
                    {
                        findings.Add(new ValidationFinding(operation.Id, ValidationSeverity.Blocking, probe.Message));
                    }
                }
            }

            return Result.Success<IReadOnlyList<ValidationFinding>>(findings);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
        {
            throw new OperationCanceledException("Validation was cancelled.", exception, cancellationToken);
        }
        catch (SqlException exception) when (opened)
        {
            return Result.Failure<IReadOnlyList<ValidationFinding>>(
                SynchronizationUseCaseErrors.Unsupported($"The server returned error {exception.Number} while validating the plan."));
        }
        catch (Exception exception) when (!opened
            && exception is not OperationCanceledException
            && SqlServerErrorTranslator.Translate(exception, writeTarget.Profile) is { } error)
        {
            return Result.Failure<IReadOnlyList<ValidationFinding>>(error);
        }
    }

    // Permissions

    private sealed record Permission(string? Securable, string Class, string Name, string Text);

    /// <summary>What an operation needs; satisfied when any alternative holds.</summary>
    private sealed record Requirement(Guid OperationId, string Operation, IReadOnlyList<Permission> Alternatives);

    private static async Task CheckPermissionsAsync(
        SqlConnection connection,
        AssembledPlan plan,
        HashSet<string> created,
        List<ValidationFinding> findings,
        CancellationToken cancellationToken)
    {
        var requirements = plan.Plan.Operations
            .SelectMany(operation => Requirements(operation, plan.Details[operation.Id]))
            .ToArray();
        var checks = requirements.SelectMany(requirement => requirement.Alternatives).Distinct().ToArray();
        var granted = new Dictionary<Permission, bool?>();
        foreach (var chunk in checks.Chunk(PermissionChecksPerQuery))
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = CommandTimeoutSeconds;
            var select = new StringBuilder("SELECT ");
            for (var index = 0; index < chunk.Length; index++)
            {
                select.Append(index == 0 ? "" : ", ")
                    .Append(CultureInfo.InvariantCulture, $"HAS_PERMS_BY_NAME(ISNULL(@s{index}, DB_NAME()), @c{index}, @p{index})");
                command.Parameters.Add(new SqlParameter($"@s{index}", SqlDbType.NVarChar, 600) { Value = (object?)chunk[index].Securable ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter($"@c{index}", SqlDbType.NVarChar, 60) { Value = chunk[index].Class });
                command.Parameters.Add(new SqlParameter($"@p{index}", SqlDbType.NVarChar, 128) { Value = chunk[index].Name });
            }

            command.CommandText = select.Append(';').ToString();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                for (var index = 0; index < chunk.Length; index++)
                {
                    granted[chunk[index]] = reader.IsDBNull(index) ? null : reader.GetInt32(index) == 1;
                }
            }
        }

        foreach (var requirement in requirements)
        {
            var results = requirement.Alternatives.Select(permission => granted.GetValueOrDefault(permission)).ToArray();
            if (results.Any(result => result == true))
            {
                continue;
            }

            // NULL means the securable does not exist yet; an object the plan creates is owned by the creator.
            var unknown = requirement.Alternatives.Where((permission, index) => results[index] is null).ToArray();
            if (unknown.Length > 0 && unknown.All(permission => permission.Securable is { } name && created.Contains(name)))
            {
                continue;
            }

            var needed = string.Join(" or ", requirement.Alternatives.Select(permission => permission.Text));
            findings.Add(new ValidationFinding(
                requirement.OperationId,
                results.All(result => result is null) ? ValidationSeverity.Warning : ValidationSeverity.Blocking,
                results.All(result => result is null)
                    ? $"Could not check the permission {needed} for \"{requirement.Operation}\"."
                    : $"The login lacks {needed}, which \"{requirement.Operation}\" needs."));
        }
    }

    private static IEnumerable<Requirement> Requirements(SynchronizationOperation operation, SchemaChangeDetail detail)
    {
        Requirement Need(params Permission[] alternatives) => new(operation.Id, operation.Description, alternatives);
        Permission Database(string name) => new(null, "DATABASE", name, $"{name} in the database");
        Permission Schema(string schema, string name) => new(Quote(schema), "SCHEMA", name, $"{name} on schema {schema}");
        Permission Object(DatabaseObjectId id, string name) => new(Quote(id), "OBJECT", name, $"{name} on {id.DisplayName}");
        DatabaseObjectId Parent(DatabaseObjectId id) => new(id.SchemaName, id.ParentObjectName!, DatabaseObjectType.Table);

        switch (detail)
        {
            case CreateSchemaDetail:
                yield return Need(Database("CREATE SCHEMA"));
                break;
            case DropSchemaDetail schema:
                yield return Need(Schema(schema.Name, "CONTROL"));
                break;
            case CreateTableDetail table:
                yield return Need(Database("CREATE TABLE"));
                yield return Need(Schema(table.Table.Id.SchemaName, "ALTER"));
                break;
            case DropTableDetail table:
                yield return Need(Object(table.Table, "CONTROL"), Schema(table.Table.SchemaName, "ALTER"));
                break;
            case AddColumnDetail column:
                yield return Need(Object(column.Table, "ALTER"));
                break;
            case AlterColumnDetail column:
                yield return Need(Object(column.Table, "ALTER"));
                break;
            case DropColumnDetail column:
                yield return Need(Object(column.Table, "ALTER"));
                break;
            case AddConstraintDetail constraint:
                yield return Need(Object(constraint.Table, "ALTER"));
                if (constraint.Constraint is ForeignKeyDefinition { ReferencedTableName: { } referenced } foreignKey)
                {
                    yield return Need(Object(new DatabaseObjectId(foreignKey.ReferencedSchemaName!, referenced, DatabaseObjectType.Table), "REFERENCES"));
                }

                break;
            case DropConstraintDetail constraint:
                yield return Need(Object(constraint.Table, "ALTER"));
                break;
            case CreateIndexDetail index:
                yield return Need(Object(index.Parent, "ALTER"));
                break;
            case DropIndexDetail index:
                yield return Need(Object(index.Parent, "ALTER"));
                break;
            case CreateModuleDetail { Id.ObjectType: DatabaseObjectType.Trigger } trigger:
                yield return Need(Object(Parent(trigger.Id), "ALTER"));
                break;
            case CreateModuleDetail { IsAlter: true } module:
                yield return Need(Object(module.Id, "ALTER"));
                break;
            case CreateModuleDetail module:
                yield return Need(Database(module.Id.ObjectType switch
                {
                    DatabaseObjectType.View => "CREATE VIEW",
                    DatabaseObjectType.StoredProcedure => "CREATE PROCEDURE",
                    _ => "CREATE FUNCTION",
                }));
                yield return Need(Schema(module.Id.SchemaName, "ALTER"));
                break;
            case DropModuleDetail { Id.ObjectType: DatabaseObjectType.Trigger } trigger:
                yield return Need(Object(trigger.Id, "CONTROL"), Object(Parent(trigger.Id), "ALTER"));
                break;
            case DropModuleDetail module:
                yield return Need(Object(module.Id, "CONTROL"), Schema(module.Id.SchemaName, "ALTER"));
                break;
            case CreateSequenceDetail { IsAlter: true } sequence:
                yield return Need(Object(sequence.Sequence.Id, "ALTER"));
                break;
            case CreateSequenceDetail sequence:
                yield return Need(Database("CREATE SEQUENCE"));
                yield return Need(Schema(sequence.Sequence.Id.SchemaName, "ALTER"));
                break;
            case DropSequenceDetail sequence:
                yield return Need(Object(sequence.Id, "CONTROL"), Schema(sequence.Id.SchemaName, "ALTER"));
                break;
            case RowsDetail rows:
                var written = rows.Table.Id;
                yield return Need(Object(written, "SELECT"));
                yield return Need(Object(written, operation.Type switch
                {
                    SynchronizationOperationType.InsertRows => "INSERT",
                    SynchronizationOperationType.UpdateRows => "UPDATE",
                    _ => "DELETE",
                }));
                if (rows.UsesIdentityInsert)
                {
                    yield return Need(Object(written, "ALTER"));
                }

                break;
        }
    }

    // Row probes

    private sealed record Probe(string Sql, string Message);

    private static IEnumerable<Probe> Probes(SchemaChangeDetail detail, HashSet<string> created)
    {
        bool IsNew(DatabaseObjectId table) => created.Contains(Quote(table));

        switch (detail)
        {
            case AddColumnDetail { Column: { IsNullable: false, Identity: null, Computed: null }, Default: null } column when !IsNew(column.Table):
                yield return new Probe(
                    $"SELECT TOP (1) 1 FROM {Quote(column.Table)};",
                    $"{column.Table.DisplayName} has rows, so adding NOT NULL column {column.Column.Name} without a default would fail.");
                break;

            case AlterColumnDetail { Column.IsNullable: false, Existing.IsNullable: true } column:
                yield return new Probe(
                    $"SELECT TOP (1) 1 FROM {Quote(column.Table)} WHERE {Quote(column.Existing.Name)} IS NULL;",
                    $"{column.Table.DisplayName}.{column.Existing.Name} has NULLs, so making it NOT NULL would fail.");
                break;

            case AddConstraintDetail { Constraint: PrimaryKeyDefinition key } constraint when !IsNew(constraint.Table):
                var keyColumns = key.Columns.Select(column => column.ColumnName).ToArray();
                yield return Duplicates(constraint.Table, keyColumns, $"primary key {key.Name}");
                yield return new Probe(
                    $"SELECT TOP (1) 1 FROM {Quote(constraint.Table)} WHERE {string.Join(" OR ", keyColumns.Select(column => $"{Quote(column)} IS NULL"))};",
                    $"{constraint.Table.DisplayName} has rows with NULL in the columns of primary key {key.Name}, so adding it would fail.");
                break;

            case AddConstraintDetail { Constraint: UniqueConstraintDefinition key } constraint when !IsNew(constraint.Table):
                yield return Duplicates(constraint.Table, [.. key.Columns.Select(column => column.ColumnName)], $"unique constraint {key.Name}");
                break;

            case CreateIndexDetail { Index: { IsUnique: true, FilterDefinition: null } index } create when !IsNew(create.Parent):
                yield return Duplicates(create.Parent, [.. index.Columns.Where(column => !column.IsIncluded).Select(column => column.ColumnName)], $"unique index {index.Name}");
                break;

            case AddConstraintDetail { Constraint: ForeignKeyDefinition { ReferencedTableName: { } referencedName } foreignKey, WithNoCheck: false } constraint
                when !IsNew(constraint.Table):
                var referenced = new DatabaseObjectId(foreignKey.ReferencedSchemaName!, referencedName, DatabaseObjectType.Table);
                var notNull = string.Join(" AND ", foreignKey.Columns.Select(mapping => $"c.{Quote(mapping.ColumnName)} IS NOT NULL"));
                var sql = IsNew(referenced)
                    ? $"SELECT TOP (1) 1 FROM {Quote(constraint.Table)} AS c WHERE {notNull};"
                    : $"SELECT TOP (1) 1 FROM {Quote(constraint.Table)} AS c WHERE {notNull} AND NOT EXISTS (SELECT 1 FROM {Quote(referenced)} AS p WHERE "
                        + string.Join(" AND ", foreignKey.Columns.Select(mapping => $"p.{Quote(mapping.ReferencedColumnName)} = c.{Quote(mapping.ColumnName)}"))
                        + ");";
                yield return new Probe(sql, $"{constraint.Table.DisplayName} has rows that foreign key {foreignKey.Name} would reject, so adding it would fail.");
                break;
        }
    }

    private static Probe Duplicates(DatabaseObjectId table, IReadOnlyList<string> columns, string what)
    {
        var list = string.Join(", ", columns.Select(Quote));
        return new Probe(
            $"SELECT TOP (1) 1 FROM {Quote(table)} GROUP BY {list} HAVING COUNT_BIG(*) > 1;",
            $"{table.DisplayName} has duplicate values in the columns of {what}, so adding it would fail.");
    }

    private static async Task<bool> ExistsAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        return await command.ExecuteScalarAsync(cancellationToken) is not null and not DBNull;
    }

    /// <summary>Quoted names of the tables, views and other objects the plan creates.</summary>
    private static HashSet<string> CreatedObjects(AssembledPlan plan)
    {
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var detail in plan.Details.Values)
        {
            switch (detail)
            {
                case CreateTableDetail table:
                    created.Add(Quote(table.Table.Id));
                    break;
                case CreateModuleDetail { IsAlter: false } module:
                    created.Add(Quote(module.Id));
                    break;
                case CreateSequenceDetail { IsAlter: false } sequence:
                    created.Add(Quote(sequence.Sequence.Id));
                    break;
                case CreateSchemaDetail schema:
                    created.Add(Quote(schema.Name));
                    break;
            }
        }

        return created;
    }

    private static string Quote(string name) => SqlServerIdentifier.Quote(name);

    private static string Quote(DatabaseObjectId id) => $"{Quote(id.SchemaName)}.{Quote(id.ObjectName)}";
}
