using DataBridge.Core.Comparison;
using DataBridge.Core.Schema;

namespace DataBridge.Core.Synchronization;

/// <summary>What the schema planner needs: both schemas, their comparison, the direction and the user's selection.</summary>
/// <param name="Source">The source schema as compared.</param>
/// <param name="Target">The target schema as compared.</param>
/// <param name="Comparison">The comparison of the two.</param>
/// <param name="Direction">Which side is copied and which is written.</param>
/// <param name="Selected">
/// The differences to synchronize, by their comparison identity. Selecting a modified table or view selects all its
/// differing children.
/// </param>
/// <param name="AllowDestructiveOperations">Whether drops and lossy alters may be planned.</param>
public sealed record SchemaPlanningInput(
    DatabaseSchema Source,
    DatabaseSchema Target,
    SchemaComparisonResult Comparison,
    SynchronizationDirection Direction,
    IReadOnlyCollection<DatabaseObjectId> Selected,
    bool AllowDestructiveOperations);

/// <summary>
/// Turns selected schema differences into changes that make the write target match the copied side (FR-PLN-003,
/// FR-PLN-005, FR-PLN-007). Never touches a database.
/// </summary>
/// <remarks>
/// <para>
/// Each change gets a risk from the architecture §10 baseline and dependencies on the changes that must run first:
/// schemas before their objects, tables before their columns, columns before the constraints and indexes that use
/// them, referenced tables and keys before foreign keys, referenced objects before the views, functions and triggers
/// that use them, and the reverse for drops. Changing a constraint or index is planned as a drop and a create, so
/// column changes can run between them.
/// </para>
/// <para>
/// When a selected drop cannot run without removing something else that the copied side also lacks (a column's
/// default constraint, a foreign key that references a dropped table), that drop is added and says why. Anything
/// that cannot be synchronized safely, such as changes that need a table rebuild, disabled or hidden definitions,
/// or destructive changes when they are not allowed, is reported in <see cref="PlanDraft.Issues"/> instead of
/// planned.
/// </para>
/// </remarks>
public sealed class SchemaSynchronizationPlanner
{
    internal const int RankDropForeignKey = 0;
    internal const int RankDropModule = 1;
    internal const int RankDropChild = 2;
    internal const int RankChangeColumn = 3;
    internal const int RankDropTable = 4;
    internal const int RankDropSequence = 5;
    internal const int RankDropSchema = 6;
    internal const int RankCreateSchema = 10;
    internal const int RankCreateSequence = 11;
    internal const int RankCreateTable = 12;
    internal const int RankAddColumn = 13;
    internal const int RankCreateChild = 14;
    internal const int RankCreateForeignKey = 15;
    internal const int RankFunction = 16;
    internal const int RankView = 17;
    internal const int RankViewIndex = 18;
    internal const int RankProcedure = 19;
    internal const int RankTrigger = 20;

    private static readonly HashSet<string> _rebuildProperties = new(StringComparer.Ordinal)
    {
        "Identity", "Computed", "ComputedExpression", "Persisted", "Sparse", "ColumnSet", "RowGuidCol", "FileStream",
        "XmlSchemaCollection", "Masking", "Encryption", "GeneratedAlways", "Hidden",
    };

    private static readonly HashSet<string> _alterableProperties = new(StringComparer.Ordinal)
    {
        "DataType", "Length", "Precision", "Scale", "Nullable", "Collation",
    };

    private static readonly HashSet<string> _stateOnlyProperties = new(StringComparer.Ordinal) { "Disabled", "NotTrusted" };

    private readonly SchemaPlanningInput _input;
    private readonly PlanDraft _draft;
    private readonly DatabaseSchema _copy;
    private readonly DatabaseSchema _write;
    private readonly StringComparer _names;
    private readonly ObjectKeyComparer _keys;
    private readonly Dictionary<ObjectKey, object> _copyObjects;
    private readonly Dictionary<ObjectKey, object> _writeObjects;
    private readonly Dictionary<int, Fact> _facts = [];
    private readonly Dictionary<ChildKey, int> _childDrops;
    private readonly Dictionary<ChildKey, int> _childCreates;
    private readonly Dictionary<ObjectKey, int> _objectDrops;
    private readonly Dictionary<ObjectKey, int> _objectCreates;
    private readonly Dictionary<string, int> _schemaCreates;
    private readonly HashSet<ChildKey> _inlinedDefaults;
    private readonly List<(SchemaObjectDifference Difference, ForeignKeyDefinition Definition, DatabaseObjectId Table, int? AfterDrop)> _foreignKeys = [];

    private SchemaSynchronizationPlanner(SchemaPlanningInput input, PlanDraft draft)
    {
        _input = input;
        _draft = draft;
        (_copy, _write) = input.Direction == SynchronizationDirection.SourceToTarget
            ? (input.Source, input.Target)
            : (input.Target, input.Source);
        _names = input.Source.IdentifiersAreCaseSensitive || input.Target.IdentifiersAreCaseSensitive
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        _keys = new ObjectKeyComparer(_names);
        _copyObjects = Objects(_copy);
        _writeObjects = Objects(_write);
        var childKeys = new ChildKeyComparer(_names);
        _childDrops = new(childKeys);
        _childCreates = new(childKeys);
        _inlinedDefaults = new(childKeys);
        _objectDrops = new(_keys);
        _objectCreates = new(_keys);
        _schemaCreates = new(_names);
    }

    private enum ChangeKind
    {
        CreateSchema,
        DropSchema,
        CreateTable,
        DropTable,
        AddColumn,
        AlterColumn,
        DropColumn,
        CreateChild,
        DropChild,
        CreateForeignKey,
        DropForeignKey,
        CreateModule,
        DropModule,
        CreateSequence,
        DropSequence,
    }

    /// <summary>Adds the changes for <paramref name="input"/>'s selection to <paramref name="draft"/>.</summary>
    public static void Plan(SchemaPlanningInput input, PlanDraft draft)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(draft);
        new SchemaSynchronizationPlanner(input, draft).Run();
    }

    private DatabaseSchema WriteSchema => _write;

    private DifferenceStatus CopyOnly => _input.Direction.OnlyInCopySide();

    private DifferenceStatus WriteOnly => _input.Direction.OnlyInWriteTarget();

    private void Run()
    {
        var selected = Expand();
        var schemaDrops = new List<SchemaObjectDifference>();
        foreach (var difference in selected.OrderBy(Phase))
        {
            if (difference.ObjectId.ObjectType == DatabaseObjectType.Schema && difference.Status == WriteOnly)
            {
                schemaDrops.Add(difference);
                continue;
            }

            Plan(difference);
        }

        foreach (var (difference, definition, table, afterDrop) in _foreignKeys)
        {
            AddForeignKey(difference.ObjectId, definition, table, afterDrop);
        }

        foreach (var difference in schemaDrops)
        {
            DropSchema(difference.ObjectId.SchemaName);
        }

        AddDependencies();
    }

    /// <summary>The selected differences, with a modified table's or view's differing children when it is selected itself.</summary>
    private List<SchemaObjectDifference> Expand()
    {
        var selected = new HashSet<DatabaseObjectId>(_input.Selected, new ObjectIdComparer(_names));
        var parents = new HashSet<ObjectKey>(_keys);
        foreach (var difference in _input.Comparison.Differences)
        {
            if (selected.Contains(difference.ObjectId)
                && difference.Status == DifferenceStatus.Modified
                && difference.ObjectId.ObjectType is DatabaseObjectType.Table or DatabaseObjectType.View)
            {
                parents.Add(new ObjectKey(difference.ObjectId.SchemaName, difference.ObjectId.ObjectName));
            }
        }

        var result = new List<SchemaObjectDifference>();
        foreach (var difference in _input.Comparison.Differences)
        {
            var id = difference.ObjectId;
            var viaParent = id.IsChild
                && difference.Status != DifferenceStatus.Equal
                && parents.Contains(new ObjectKey(id.SchemaName, id.ParentObjectName!));
            if (selected.Contains(id) || viaParent)
            {
                result.Add(difference);
            }
        }

        return result;
    }

    private static int Phase(SchemaObjectDifference difference) => difference.ObjectId.ObjectType switch
    {
        DatabaseObjectType.Schema => 0,
        DatabaseObjectType.Sequence or DatabaseObjectType.Table => 1,
        _ when difference.ObjectId.IsChild => 2,
        _ => 3,
    };

    private void Plan(SchemaObjectDifference difference)
    {
        var id = difference.ObjectId;
        switch (difference.Status)
        {
            case DifferenceStatus.Equal:
                return;
            case DifferenceStatus.Unknown or DifferenceStatus.Incompatible:
                _draft.Report(id, $"{id.DisplayName} cannot be synchronized: {difference.Reason}");
                return;
        }

        var action = difference.Status == CopyOnly ? Action.Create : difference.Status == WriteOnly ? Action.Drop : Action.Alter;
        switch (id.ObjectType)
        {
            case DatabaseObjectType.Schema:
                EnsureSchema(id.SchemaName, explicitlySelected: true);
                break;
            case DatabaseObjectType.Table:
                PlanTable(difference, action);
                break;
            case DatabaseObjectType.Sequence:
                PlanSequence(difference, action);
                break;
            case DatabaseObjectType.View or DatabaseObjectType.StoredProcedure or DatabaseObjectType.ScalarFunction
                or DatabaseObjectType.TableValuedFunction or DatabaseObjectType.Trigger:
                PlanModule(difference, action);
                break;
            case DatabaseObjectType.Column:
                PlanColumn(difference, action);
                break;
            default:
                PlanChild(difference, action);
                break;
        }
    }

    private enum Action
    {
        Create,
        Alter,
        Drop,
    }

    // Schemas

    private int? EnsureSchema(string name, bool explicitlySelected = false)
    {
        if (_schemaCreates.TryGetValue(name, out var existing))
        {
            return existing;
        }

        if (WriteSchema.Schemas.Any(schema => _names.Equals(schema.Name, name)))
        {
            return null;
        }

        var description = explicitlySelected
            ? $"Create schema {name}"
            : $"Create schema {name}, which the objects being created need";
        var index = Add(
            DatabaseObjectId.ForSchema(name), SynchronizationOperationType.CreateObject, OperationRisk.Low, false, description, [],
            RankCreateSchema, new CreateSchemaDetail(name), new Fact(ChangeKind.CreateSchema, name, null, name, DatabaseObjectType.Schema));
        _schemaCreates[name] = index;
        return index;
    }

    private void DropSchema(string name)
    {
        var id = DatabaseObjectId.ForSchema(name);
        if (!_input.AllowDestructiveOperations)
        {
            ReportDestructive(id);
            return;
        }

        var schema = WriteSchema.Schemas.First(item => _names.Equals(item.Name, name));
        var remaining = ObjectsIn(schema).Where(objectId => !_objectDrops.ContainsKey(Key(objectId))).Select(objectId => objectId.DisplayName).ToArray();
        if (remaining.Length > 0)
        {
            _draft.Report(id, $"Schema {name} is not dropped: it still contains {string.Join(", ", remaining)}. Select those objects too.");
            return;
        }

        if (WriteSchema.UnsupportedObjects.Any(item => _names.Equals(item.SchemaName, name)))
        {
            _draft.Report(id, $"Schema {name} is not dropped: it contains objects DataBridge does not compare.");
            return;
        }

        var index = Add(
            id, SynchronizationOperationType.DropObject, OperationRisk.Critical, true, $"Drop schema {name}", [], RankDropSchema,
            new DropSchemaDetail(name), new Fact(ChangeKind.DropSchema, name, null, name, DatabaseObjectType.Schema));
        foreach (var (key, drop) in _objectDrops.Where(pair => _names.Equals(pair.Key.Schema, name)))
        {
            _draft.DependOn(index, drop);
        }
    }

    private static IEnumerable<DatabaseObjectId> ObjectsIn(SchemaDefinition schema) =>
        schema.Tables.Select(table => table.Id)
            .Concat(schema.Views.Select(view => view.Id))
            .Concat(schema.ProgrammableObjects.Where(module => module.Id.ObjectType != DatabaseObjectType.Trigger).Select(module => module.Id))
            .Concat(schema.Sequences.Select(sequence => sequence.Id));

    // Tables

    private void PlanTable(SchemaObjectDifference difference, Action action)
    {
        var id = difference.ObjectId;
        switch (action)
        {
            case Action.Create:
                CreateTable((TableDefinition)_copyObjects[Key(id)]);
                break;
            case Action.Drop:
                DropTable((TableDefinition)_writeObjects[Key(id)]);
                break;
            default:
                if (difference.Properties.Count > 0)
                {
                    _draft.Report(id, $"{id.DisplayName}: changing {string.Join(", ", difference.Properties.Select(property => property.PropertyName))} needs the table to be rebuilt, which DataBridge does not do.");
                }

                break;
        }
    }

    private void CreateTable(TableDefinition table)
    {
        var id = table.Id;
        if (UnsupportedColumns(table) is { } reason)
        {
            _draft.Report(id, $"{id.DisplayName} is not created: {reason}");
            return;
        }

        if (HiddenConstraint(table) is { } hidden)
        {
            _draft.Report(id, $"{id.DisplayName} is not created: the definition of {hidden} is not visible.");
            return;
        }

        var included = table with
        {
            CheckConstraints = [.. table.CheckConstraints.Where(check => !check.IsDisabled)],
            ForeignKeys = [],
            Indexes = [],
        };
        var warnings = new List<string>();
        foreach (var check in table.CheckConstraints.Where(check => check.IsDisabled))
        {
            warnings.Add($"Check constraint {check.Name} is disabled in the copied database and is not created; DataBridge does not create disabled constraints.");
        }

        EnsureSchema(id.SchemaName);
        var index = Add(
            id, SynchronizationOperationType.CreateObject, OperationRisk.Low, false, $"Create table {id.DisplayName}", warnings,
            RankCreateTable, new CreateTableDetail(included),
            new Fact(ChangeKind.CreateTable, id.SchemaName, id.ObjectName, id.ObjectName, DatabaseObjectType.Table));
        _objectCreates[Key(id)] = index;

        foreach (var foreignKey in table.ForeignKeys)
        {
            var childId = DatabaseObjectId.ForChild(id, foreignKey.Name, DatabaseObjectType.ForeignKey);
            _foreignKeys.Add((new SchemaObjectDifference(childId, CopyOnly, []), foreignKey, id, null));
        }

        foreach (var tableIndex in table.Indexes)
        {
            CreateIndex(id, tableIndex, null, parentCreated: true);
        }
    }

    private void DropTable(TableDefinition table)
    {
        var id = table.Id;
        if (!_input.AllowDestructiveOperations)
        {
            ReportDestructive(id);
            return;
        }

        var warnings = new List<string>();
        var referencing = WriteSchema.Schemas.SelectMany(schema => schema.Tables)
            .Where(other => !SameObject(other.Id, id))
            .SelectMany(other => other.ForeignKeys.Where(foreignKey => References(foreignKey, id)).Select(foreignKey => (Table: other, ForeignKey: foreignKey)))
            .ToArray();
        foreach (var (other, foreignKey) in referencing)
        {
            if (!InCopy(other.Id, foreignKey.Name, DatabaseObjectType.ForeignKey) || TableBeingDropped(other.Id))
            {
                continue;
            }

            _draft.Report(id, $"{id.DisplayName} is not dropped: foreign key {foreignKey.Name} on {other.Id.DisplayName} references it and exists in the copied database too.");
            return;
        }

        foreach (var module in ModulesReferencing(id))
        {
            warnings.Add(module.Module.IsSchemaBound
                ? $"{module.Id.DisplayName} is schema-bound to this table; the drop fails unless it is dropped first."
                : $"{module.Id.DisplayName} references this table and will stop working.");
        }

        var index = Add(
            id, SynchronizationOperationType.DropObject, OperationRisk.Critical, true,
            $"Drop table {id.DisplayName} and all its rows", warnings, RankDropTable, new DropTableDetail(id),
            new Fact(ChangeKind.DropTable, id.SchemaName, id.ObjectName, id.ObjectName, DatabaseObjectType.Table));
        _objectDrops[Key(id)] = index;

        foreach (var (other, foreignKey) in referencing)
        {
            if (TableBeingDropped(other.Id))
            {
                continue;
            }

            var drop = DropChild(
                other.Id, foreignKey.Name, DatabaseObjectType.ForeignKey, foreignKey,
                $"because it references {id.DisplayName}, which is dropped");
            if (drop is { } dropIndex)
            {
                _draft.DependOn(index, dropIndex);
            }
        }
    }

    private bool TableBeingDropped(DatabaseObjectId table) =>
        _objectDrops.ContainsKey(Key(table))
        || _input.Comparison.Differences.Any(difference =>
            difference.Status == WriteOnly
            && difference.ObjectId.ObjectType == DatabaseObjectType.Table
            && SameObject(difference.ObjectId, table)
            && _input.Selected.Any(selected => SameObject(selected, table) && selected.ObjectType == DatabaseObjectType.Table));

    // Columns

    private void PlanColumn(SchemaObjectDifference difference, Action action)
    {
        var id = difference.ObjectId;
        var tableKey = new ObjectKey(id.SchemaName, id.ParentObjectName!);
        var copyTable = (TableDefinition)_copyObjects[tableKey];
        var writeTable = (TableDefinition)_writeObjects[tableKey];
        var (copyName, writeName) = Names(difference);
        switch (action)
        {
            case Action.Create:
                AddColumn(writeTable, copyTable, copyTable.Columns.First(column => _names.Equals(column.Name, copyName)));
                break;
            case Action.Drop:
                DropColumn(writeTable, writeTable.Columns.First(column => _names.Equals(column.Name, writeName)));
                break;
            default:
                AlterColumn(
                    writeTable,
                    copyTable.Columns.First(column => _names.Equals(column.Name, copyName)),
                    writeTable.Columns.First(column => _names.Equals(column.Name, writeName)),
                    difference);
                break;
        }
    }

    private void AddColumn(TableDefinition writeTable, TableDefinition copyTable, ColumnDefinition column)
    {
        var id = DatabaseObjectId.ForChild(writeTable.Id, column.Name, DatabaseObjectType.Column);
        if (UnsupportedColumn(column) is { } reason)
        {
            _draft.Report(id, $"Column {id.DisplayName} is not added: {reason}");
            return;
        }

        var defaultConstraint = copyTable.DefaultConstraints.FirstOrDefault(item => _names.Equals(item.ColumnName, column.Name));
        if (defaultConstraint is { Expression: null })
        {
            _draft.Report(id, $"Column {id.DisplayName} is not added: the definition of its default {defaultConstraint.Name} is not visible.");
            return;
        }

        var warnings = new List<string>();
        var risk = OperationRisk.Medium;
        if (!column.IsNullable && defaultConstraint is null && column.Identity is null && column.Computed is null)
        {
            risk = OperationRisk.High;
            warnings.Add("The column does not allow NULL and has no default, so adding it fails if the table has rows.");
        }

        if (defaultConstraint is not null)
        {
            _inlinedDefaults.Add(new ChildKey(copyTable.Id.SchemaName, copyTable.Id.ObjectName, DatabaseObjectType.DefaultConstraint, defaultConstraint.Name));
        }

        if (column.OrdinalPosition <= writeTable.Columns.Count)
        {
            warnings.Add("The column is added at the end of the table; DataBridge does not reorder columns.");
        }

        var index = Add(
            id, SynchronizationOperationType.CreateObject, risk, false,
            $"Add column {id.DisplayName}" + (defaultConstraint is null ? "" : $" with default {defaultConstraint.Name}"),
            warnings, RankAddColumn, new AddColumnDetail(writeTable.Id, column, defaultConstraint),
            new Fact(ChangeKind.AddColumn, id.SchemaName, writeTable.Id.ObjectName, column.Name, DatabaseObjectType.Column) { Columns = [column.Name] });
        _childCreates[ChildKeyOf(id)] = index;
    }

    private void DropColumn(TableDefinition writeTable, ColumnDefinition column)
    {
        var id = DatabaseObjectId.ForChild(writeTable.Id, column.Name, DatabaseObjectType.Column);
        if (!_input.AllowDestructiveOperations)
        {
            ReportDestructive(id);
            return;
        }

        var prerequisites = new List<int>();
        foreach (var (name, type, definition) in ChildrenUsing(writeTable, column.Name))
        {
            if (InCopy(writeTable.Id, name, type))
            {
                _draft.Report(id, $"Column {id.DisplayName} is not dropped: {name} uses it and exists in the copied database too. Synchronize {name} first.");
                return;
            }

            if (DropChild(writeTable.Id, name, type, definition, $"because it uses column {column.Name}, which is dropped") is not { } drop)
            {
                _draft.Report(id, $"Column {id.DisplayName} is not dropped because {name}, which uses it, cannot be dropped.");
                return;
            }

            prerequisites.Add(drop);
        }

        var index = Add(
            id, SynchronizationOperationType.DropObject, OperationRisk.Critical, true,
            $"Drop column {id.DisplayName} and its values",
            ["Check constraints and computed columns that use the column make the drop fail."],
            RankChangeColumn, new DropColumnDetail(writeTable.Id, column.Name),
            new Fact(ChangeKind.DropColumn, id.SchemaName, writeTable.Id.ObjectName, column.Name, DatabaseObjectType.Column) { Columns = [column.Name] });
        _childDrops[ChildKeyOf(id)] = index;
        foreach (var prerequisite in prerequisites)
        {
            _draft.DependOn(index, prerequisite);
        }
    }

    private void AlterColumn(TableDefinition writeTable, ColumnDefinition column, ColumnDefinition existing, SchemaObjectDifference difference)
    {
        var id = DatabaseObjectId.ForChild(writeTable.Id, existing.Name, DatabaseObjectType.Column);
        var properties = difference.Properties.Select(property => property.PropertyName).ToHashSet(StringComparer.Ordinal);
        var rebuild = properties.Where(_rebuildProperties.Contains).ToArray();
        if (rebuild.Length > 0 || column.Computed is not null || existing.Computed is not null)
        {
            var what = rebuild.Length > 0 ? string.Join(", ", rebuild) : "a computed column";
            _draft.Report(id, $"Column {id.DisplayName}: changing {what} needs the column or table to be rebuilt, which DataBridge does not do.");
            return;
        }

        if (!properties.Any(_alterableProperties.Contains))
        {
            if (properties.Contains("OrdinalPosition"))
            {
                _draft.Report(id, $"Column {id.DisplayName} is in a different position; DataBridge does not reorder columns.");
            }

            return;
        }

        if (UnsupportedColumn(column) is { } reason)
        {
            _draft.Report(id, $"Column {id.DisplayName} is not changed: {reason}");
            return;
        }

        var lossy = IsLossy(existing, column);
        if (lossy && !_input.AllowDestructiveOperations)
        {
            _draft.Report(id, $"Column {id.DisplayName} is not changed: the change can lose data ({Describe(existing)} to {Describe(column)}), and destructive operations are not allowed. Allow them to include it.");
            return;
        }

        var warnings = new List<string>();
        var risk = OperationRisk.Medium;
        if (lossy)
        {
            risk = OperationRisk.High;
            warnings.Add("The new type can lose data, or fail if existing values do not fit.");
        }

        if (existing.IsNullable && !column.IsNullable)
        {
            risk = OperationRisk.High;
            warnings.Add("The column will not allow NULL; the change fails if any row has NULL in it.");
        }

        if (properties.Contains("Collation"))
        {
            risk = OperationRisk.High;
            warnings.Add("Changing the collation can change sort order and comparisons, and can lose characters the new code page lacks.");
        }

        var users = ChildrenUsing(writeTable, existing.Name).Select(child => child.Name).ToArray();
        if (users.Length > 0)
        {
            warnings.Add($"{string.Join(", ", users)} use the column; the change fails unless they are dropped and recreated in this plan.");
        }

        if (properties.Contains("OrdinalPosition"))
        {
            warnings.Add("The column's position is not changed; DataBridge does not reorder columns.");
        }

        var index = Add(
            id, SynchronizationOperationType.AlterObject, risk, lossy,
            $"Change column {id.DisplayName} from {Describe(existing)} to {Describe(column)}",
            warnings, RankChangeColumn, new AlterColumnDetail(writeTable.Id, column with { Name = existing.Name }, existing),
            new Fact(ChangeKind.AlterColumn, id.SchemaName, writeTable.Id.ObjectName, existing.Name, DatabaseObjectType.Column) { Columns = [existing.Name] });
        _childCreates[ChildKeyOf(id)] = index;
    }

    /// <summary>Whether changing <paramref name="from"/> to <paramref name="to"/> can lose or reject existing values.</summary>
    public static bool IsLossy(ColumnDefinition from, ColumnDefinition to)
    {
        var fromType = from.ProviderBaseTypeName ?? from.SqlTypeName;
        var toType = to.ProviderBaseTypeName ?? to.SqlTypeName;
        if (!string.Equals(fromType, toType, StringComparison.OrdinalIgnoreCase) && !IsWidening(fromType, toType))
        {
            return true;
        }

        if (from.IsMaxLength && !to.IsMaxLength)
        {
            return true;
        }

        if (!to.IsMaxLength && from.MaxLength is { } fromLength && to.MaxLength is { } toLength && toLength < fromLength)
        {
            return true;
        }

        if (from.Precision is { } fromPrecision && to.Precision is { } toPrecision)
        {
            var fromScale = from.Scale ?? 0;
            var toScale = to.Scale ?? 0;
            return toScale < fromScale || toPrecision - toScale < fromPrecision - fromScale;
        }

        return from.Scale is { } fromFraction && to.Scale is { } toFraction && toFraction < fromFraction;
    }

    private static bool IsWidening(string from, string to)
    {
        string[][] chains =
        [
            ["tinyint", "smallint", "int", "bigint"],
            ["real", "float"],
            ["char", "varchar"],
            ["nchar", "nvarchar"],
            ["char", "nchar"],
            ["varchar", "nvarchar"],
            ["char", "nvarchar"],
            ["binary", "varbinary"],
            ["smalldatetime", "datetime", "datetime2"],
            ["date", "datetime2"],
            ["smallmoney", "money"],
        ];
        return chains.Any(chain =>
        {
            var fromIndex = Array.FindIndex(chain, name => string.Equals(name, from, StringComparison.OrdinalIgnoreCase));
            var toIndex = Array.FindIndex(chain, name => string.Equals(name, to, StringComparison.OrdinalIgnoreCase));
            return fromIndex >= 0 && toIndex > fromIndex;
        });
    }

    // Constraints and indexes

    private void PlanChild(SchemaObjectDifference difference, Action action)
    {
        var id = difference.ObjectId;
        var parentKey = new ObjectKey(id.SchemaName, id.ParentObjectName!);
        var copyParent = _copyObjects[parentKey];
        var writeParent = _writeObjects[parentKey];
        var writeParentId = IdOf(writeParent);
        var (copyName, writeName) = Names(difference);

        if (id.ObjectType == DatabaseObjectType.DefaultConstraint
            && _inlinedDefaults.Contains(new ChildKey(IdOf(copyParent).SchemaName, IdOf(copyParent).ObjectName, DatabaseObjectType.DefaultConstraint, copyName)))
        {
            return;
        }

        if (action == Action.Alter)
        {
            var properties = difference.Properties.Select(property => property.PropertyName).ToArray();
            if (properties.All(_stateOnlyProperties.Contains))
            {
                _draft.Report(id, $"{id.DisplayName} differs only in whether it is enabled or trusted; DataBridge does not enable, disable or re-check constraints and indexes.");
                return;
            }
        }

        var copyDefinition = action == Action.Drop ? null : FindChild(copyParent, copyName, id.ObjectType);
        var writeDefinition = action == Action.Create ? null : FindChild(writeParent, writeName, id.ObjectType);

        int? dropped = null;
        if (writeDefinition is not null)
        {
            var recreating = action == Action.Alter;
            if (recreating && CreateProblem(copyDefinition!, copyParent) is { } problem)
            {
                _draft.Report(id, $"{id.DisplayName} is not changed: {problem}");
                return;
            }

            dropped = DropChild(writeParentId, writeName, id.ObjectType, writeDefinition, reason: null, recreating);
            if (dropped is null || !recreating)
            {
                return;
            }
        }

        switch (copyDefinition)
        {
            case ForeignKeyDefinition foreignKey:
                _foreignKeys.Add((difference, foreignKey, writeParentId, dropped));
                break;
            case IndexDefinition index:
                CreateIndex(writeParentId, index, dropped, parentCreated: false);
                break;
            case not null:
                CreateConstraint(writeParentId, copyDefinition, dropped);
                break;
        }
    }

    private object? FindChild(object parent, string name, DatabaseObjectType type)
    {
        var indexes = parent is ViewDefinition view ? view.Indexes : ((TableDefinition)parent).Indexes;
        if (type == DatabaseObjectType.Index)
        {
            return indexes.FirstOrDefault(index => _names.Equals(index.Name, name));
        }

        var table = (TableDefinition)parent;
        return type switch
        {
            DatabaseObjectType.PrimaryKey => table.PrimaryKey,
            DatabaseObjectType.UniqueConstraint => table.UniqueConstraints.FirstOrDefault(item => _names.Equals(item.Name, name)),
            DatabaseObjectType.ForeignKey => table.ForeignKeys.FirstOrDefault(item => _names.Equals(item.Name, name)),
            DatabaseObjectType.CheckConstraint => table.CheckConstraints.FirstOrDefault(item => _names.Equals(item.Name, name)),
            DatabaseObjectType.DefaultConstraint => table.DefaultConstraints.FirstOrDefault(item => _names.Equals(item.Name, name)),
            _ => null,
        };
    }

    /// <summary>Why a copied constraint or index cannot be created, or <see langword="null"/>.</summary>
    private static string? CreateProblem(object definition, object copyParent) => definition switch
    {
        CheckConstraintDefinition { Expression: null } => "its definition is not visible.",
        CheckConstraintDefinition { IsDisabled: true } => "it is disabled in the copied database; DataBridge does not create disabled constraints.",
        DefaultConstraintDefinition { Expression: null } => "its definition is not visible.",
        ForeignKeyDefinition { ReferencedTableName: null } => "the table it references is not visible.",
        ForeignKeyDefinition { IsDisabled: true } => "it is disabled in the copied database; DataBridge does not create disabled constraints.",
        IndexDefinition { Options.IsDisabled: true } => "it is disabled in the copied database; DataBridge does not create disabled indexes.",
        IndexDefinition index when !IsSupportedIndexType(index.IndexType) => $"{index.IndexType} indexes are not synchronized.",
        IndexDefinition when copyParent is ViewDefinition { Module.IsSchemaBound: false } => "the view is not schema-bound.",
        _ => null,
    };

    private static bool IsSupportedIndexType(string type) =>
        string.Equals(type, "CLUSTERED", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "NONCLUSTERED", StringComparison.OrdinalIgnoreCase);

    private void CreateConstraint(DatabaseObjectId table, object definition, int? afterDrop)
    {
        var (name, type, columns) = Describe(definition);
        var id = DatabaseObjectId.ForChild(table, name, type);
        if (CreateProblem(definition, _copyObjects[Key(table)]) is { } problem)
        {
            _draft.Report(id, $"{id.DisplayName} is not created: {problem}");
            return;
        }

        var newTable = _objectCreates.ContainsKey(Key(table));
        var (risk, warning) = definition switch
        {
            DefaultConstraintDefinition => (OperationRisk.Medium, (string?)null),
            _ when newTable => (OperationRisk.Low, null),
            CheckConstraintDefinition { IsNotTrusted: true } => (OperationRisk.High, "Added WITH NOCHECK, as in the copied database: existing rows are not checked and the constraint is not trusted."),
            CheckConstraintDefinition => (OperationRisk.High, "Adding the constraint fails if existing rows violate it."),
            _ => (OperationRisk.High, "Adding the key fails if existing rows have duplicate or NULL values in its columns."),
        };

        var index = Add(
            id, afterDrop is null ? SynchronizationOperationType.CreateObject : SynchronizationOperationType.AlterObject, risk, false,
            (afterDrop is null ? "Add " : "Recreate ") + $"{Describe(type)} {id.DisplayName}", warning is null ? [] : [warning],
            RankCreateChild, new AddConstraintDetail(table, definition, definition is CheckConstraintDefinition { IsNotTrusted: true }),
            new Fact(ChangeKind.CreateChild, table.SchemaName, table.ObjectName, name, type) { Columns = columns });
        _childCreates[ChildKeyOf(id)] = index;
        if (afterDrop is { } drop)
        {
            _draft.DependOn(index, drop);
        }
    }

    private void CreateIndex(DatabaseObjectId parent, IndexDefinition definition, int? afterDrop, bool parentCreated)
    {
        var id = DatabaseObjectId.ForChild(parent, definition.Name, DatabaseObjectType.Index);
        if (CreateProblem(definition, _copyObjects[Key(parent)]) is { } problem)
        {
            _draft.Report(id, $"{id.DisplayName} is not created: {problem}");
            return;
        }

        var isView = _copyObjects[Key(parent)] is ViewDefinition;
        var risk = parentCreated ? OperationRisk.Low : definition.IsUnique ? OperationRisk.High : OperationRisk.Medium;
        var warnings = definition.IsUnique && !parentCreated ? new[] { "Creating the index fails if existing rows have duplicate keys." } : [];
        var index = Add(
            id, afterDrop is null ? SynchronizationOperationType.CreateObject : SynchronizationOperationType.AlterObject, risk, false,
            (afterDrop is null ? "Create index " : "Recreate index ") + id.DisplayName, warnings,
            isView ? RankViewIndex : RankCreateChild, new CreateIndexDetail(parent, definition),
            new Fact(ChangeKind.CreateChild, parent.SchemaName, parent.ObjectName, definition.Name, DatabaseObjectType.Index)
            {
                Columns = [.. definition.Columns.Select(column => column.ColumnName)],
            });
        _childCreates[ChildKeyOf(id)] = index;
        if (afterDrop is { } drop)
        {
            _draft.DependOn(index, drop);
        }
    }

    private void AddForeignKey(DatabaseObjectId id, ForeignKeyDefinition definition, DatabaseObjectId table, int? afterDrop)
    {
        var childId = DatabaseObjectId.ForChild(table, definition.Name, DatabaseObjectType.ForeignKey);
        if (CreateProblem(definition, _copyObjects[Key(table)]) is { } problem)
        {
            _draft.Report(childId, $"{childId.DisplayName} is not created: {problem}");
            return;
        }

        var referenced = new ObjectKey(definition.ReferencedSchemaName!, definition.ReferencedTableName!);
        if (!_writeObjects.ContainsKey(referenced) && !_objectCreates.ContainsKey(referenced))
        {
            _draft.Report(childId, $"{childId.DisplayName} is not created: it references {referenced.Schema}.{referenced.Name}, which the write target does not have. Select that table too.");
            return;
        }

        var newTable = _objectCreates.ContainsKey(Key(table));
        var (risk, warning) = newTable
            ? (OperationRisk.Low, (string?)null)
            : definition.IsNotTrusted
                ? (OperationRisk.High, "Added WITH NOCHECK, as in the copied database: existing rows are not checked and the key is not trusted.")
                : (OperationRisk.High, "Adding the foreign key fails if existing rows reference missing rows.");
        var index = Add(
            childId, afterDrop is null ? SynchronizationOperationType.CreateObject : SynchronizationOperationType.AlterObject, risk, false,
            (afterDrop is null ? "Add foreign key " : "Recreate foreign key ") + childId.DisplayName, warning is null ? [] : [warning],
            RankCreateForeignKey, new AddConstraintDetail(table, definition, definition.IsNotTrusted && !newTable),
            new Fact(ChangeKind.CreateForeignKey, table.SchemaName, table.ObjectName, definition.Name, DatabaseObjectType.ForeignKey)
            {
                Columns = [.. definition.Columns.Select(mapping => mapping.ColumnName)],
                Referenced = referenced,
            });
        _childCreates[ChildKeyOf(childId)] = index;
        if (afterDrop is { } drop)
        {
            _draft.DependOn(index, drop);
        }
    }

    /// <summary>
    /// Plans dropping a write-target constraint or index: for real (<paramref name="recreating"/> false, destructive) or as
    /// the first half of changing it. Returns the change, or <see langword="null"/> when it cannot be dropped.
    /// </summary>
    private int? DropChild(DatabaseObjectId table, string name, DatabaseObjectType type, object definition, string? reason, bool recreating = false)
    {
        var id = DatabaseObjectId.ForChild(table, name, type);
        if (_childDrops.TryGetValue(ChildKeyOf(id), out var existing))
        {
            return existing;
        }

        if (!recreating && !_input.AllowDestructiveOperations)
        {
            ReportDestructive(id);
            return null;
        }

        var prerequisites = new List<int>();
        if (type is DatabaseObjectType.PrimaryKey or DatabaseObjectType.UniqueConstraint or DatabaseObjectType.Index)
        {
            var columns = Describe(definition).Columns;
            foreach (var (other, foreignKey) in ForeignKeysOnKey(table, columns))
            {
                if (recreating || InCopy(other.Id, foreignKey.Name, DatabaseObjectType.ForeignKey))
                {
                    _draft.Report(id, $"{id.DisplayName} is not {(recreating ? "changed" : "dropped")}: foreign key {foreignKey.Name} on {other.Id.DisplayName} depends on it. DataBridge does not drop and recreate dependent foreign keys for you.");
                    return null;
                }

                if (DropChild(other.Id, foreignKey.Name, DatabaseObjectType.ForeignKey, foreignKey, $"because it depends on {name}, which is dropped") is not { } drop)
                {
                    return null;
                }

                prerequisites.Add(drop);
            }
        }

        var description = recreating
            ? $"Drop {Describe(type)} {id.DisplayName} so it can be recreated with its new definition"
            : $"Drop {Describe(type)} {id.DisplayName}" + (reason is null ? "" : $" {reason}");
        var warnings = recreating ? new[] { $"If the plan stops after this step, {name} is missing until it is recreated." } : [];
        SchemaChangeDetail detail = type == DatabaseObjectType.Index ? new DropIndexDetail(table, name) : new DropConstraintDetail(table, name);
        var kind = type == DatabaseObjectType.ForeignKey ? ChangeKind.DropForeignKey : ChangeKind.DropChild;
        var fact = new Fact(kind, table.SchemaName, table.ObjectName, name, type)
        {
            Columns = Describe(definition).Columns,
            Referenced = definition is ForeignKeyDefinition { ReferencedTableName: { } referencedName } foreignKeyDefinition
                ? new ObjectKey(foreignKeyDefinition.ReferencedSchemaName!, referencedName)
                : null,
        };
        var index = Add(
            id, recreating ? SynchronizationOperationType.AlterObject : SynchronizationOperationType.DropObject,
            recreating ? OperationRisk.High : OperationRisk.Critical, !recreating, description, warnings,
            type == DatabaseObjectType.ForeignKey ? RankDropForeignKey : RankDropChild, detail, fact);
        _childDrops[ChildKeyOf(id)] = index;
        foreach (var prerequisite in prerequisites)
        {
            _draft.DependOn(index, prerequisite);
        }

        return index;
    }

    private IEnumerable<(TableDefinition Table, ForeignKeyDefinition ForeignKey)> ForeignKeysOnKey(DatabaseObjectId table, IReadOnlyList<string> columns) =>
        WriteSchema.Schemas.SelectMany(schema => schema.Tables)
            .SelectMany(other => other.ForeignKeys.Select(foreignKey => (Table: other, ForeignKey: foreignKey)))
            .Where(pair => References(pair.ForeignKey, table)
                && pair.ForeignKey.Columns.Count == columns.Count
                && pair.ForeignKey.Columns.All(mapping => columns.Contains(mapping.ReferencedColumnName, _names)));

    /// <summary>The write-target constraints and indexes that use a column.</summary>
    private IEnumerable<(string Name, DatabaseObjectType Type, object Definition)> ChildrenUsing(TableDefinition table, string column)
    {
        if (table.PrimaryKey is { } primaryKey && primaryKey.Columns.Any(item => _names.Equals(item.ColumnName, column)))
        {
            yield return (primaryKey.Name, DatabaseObjectType.PrimaryKey, primaryKey);
        }

        foreach (var unique in table.UniqueConstraints.Where(item => item.Columns.Any(key => _names.Equals(key.ColumnName, column))))
        {
            yield return (unique.Name, DatabaseObjectType.UniqueConstraint, unique);
        }

        foreach (var foreignKey in table.ForeignKeys.Where(item => item.Columns.Any(mapping => _names.Equals(mapping.ColumnName, column))))
        {
            yield return (foreignKey.Name, DatabaseObjectType.ForeignKey, foreignKey);
        }

        foreach (var defaultConstraint in table.DefaultConstraints.Where(item => _names.Equals(item.ColumnName, column)))
        {
            yield return (defaultConstraint.Name, DatabaseObjectType.DefaultConstraint, defaultConstraint);
        }

        foreach (var index in table.Indexes.Where(item => item.Columns.Any(key => _names.Equals(key.ColumnName, column))))
        {
            yield return (index.Name, DatabaseObjectType.Index, index);
        }
    }

    // Views, procedures, functions and triggers

    private void PlanModule(SchemaObjectDifference difference, Action action)
    {
        var id = difference.ObjectId;
        switch (action)
        {
            case Action.Drop:
                DropModule(IdOf(_writeObjects[Key(id)]), ModuleOf(_writeObjects[Key(id)]));
                return;
            case Action.Alter when difference.Properties.Count == 0:
                return;
            case Action.Alter when difference.Properties.All(property => property.PropertyName == "Disabled"):
                _draft.Report(id, $"{id.DisplayName} differs only in whether it is enabled; DataBridge does not enable or disable triggers.");
                return;
        }

        var copy = _copyObjects[Key(id)];
        var copyId = IdOf(copy);
        var module = ModuleOf(copy);
        if (module.Text is null)
        {
            _draft.Report(id, $"{id.DisplayName} cannot be synchronized: {module.UnreadableReason}");
            return;
        }

        if (copy is ProgrammableObjectDefinition { Trigger.IsDisabled: true })
        {
            _draft.Report(id, $"{id.DisplayName} is disabled in the copied database; DataBridge does not create or change disabled triggers.");
            return;
        }

        if (copyId.ObjectType == DatabaseObjectType.Trigger)
        {
            var parent = new ObjectKey(copyId.SchemaName, copyId.ParentObjectName!);
            if (!_writeObjects.ContainsKey(parent) && !_objectCreates.ContainsKey(parent))
            {
                _draft.Report(id, $"{id.DisplayName} is not created: its table {parent.Schema}.{parent.Name} is not in the write target. Select the table too.");
                return;
            }
        }

        var isAlter = action == Action.Alter;
        if (isAlter && _writeObjects[Key(id)] is ViewDefinition { Indexes.Count: > 0 })
        {
            _draft.Report(id, $"{id.DisplayName} is not changed: altering an indexed view drops its indexes, which DataBridge does not do for you.");
            return;
        }

        var warnings = new List<string>();
        if (isAlter && WriteModulesReferencing(copyId).Any(other => other.Module.IsSchemaBound))
        {
            warnings.Add("Schema-bound objects reference it; the change fails if it affects them.");
        }

        if (copyId.ObjectType == DatabaseObjectType.Trigger)
        {
            warnings.Add("The trigger runs on every matching write to its table from now on.");
        }

        if (!isAlter)
        {
            EnsureSchema(copyId.SchemaName);
        }

        var risk = isAlter || copyId.ObjectType == DatabaseObjectType.Trigger ? OperationRisk.Medium : OperationRisk.Low;
        var index = Add(
            copyId, isAlter ? SynchronizationOperationType.AlterObject : SynchronizationOperationType.CreateObject, risk, false,
            (isAlter ? "Alter " : "Create ") + $"{Describe(copyId.ObjectType)} {copyId.DisplayName}", warnings, RankOf(copyId.ObjectType),
            new CreateModuleDetail(copyId, module, isAlter),
            new Fact(ChangeKind.CreateModule, copyId.SchemaName, copyId.ParentObjectName ?? copyId.ObjectName, copyId.ObjectName, copyId.ObjectType)
            {
                References = module.EffectiveReferences,
            });
        _objectCreates[Key(copyId)] = index;

        if (!isAlter && copy is ViewDefinition view)
        {
            foreach (var viewIndex in view.Indexes)
            {
                CreateIndex(copyId, viewIndex, null, parentCreated: true);
            }
        }
    }

    private void DropModule(DatabaseObjectId id, ModuleDefinition module)
    {
        if (!_input.AllowDestructiveOperations)
        {
            ReportDestructive(id);
            return;
        }

        var warnings = WriteModulesReferencing(id)
            .Select(other => $"{other.Id.DisplayName} references it" + (other.Module.IsSchemaBound ? " and is schema-bound, so the drop fails unless it is dropped first." : " and will stop working."))
            .ToArray();
        var index = Add(
            id, SynchronizationOperationType.DropObject, OperationRisk.Critical, true, $"Drop {Describe(id.ObjectType)} {id.DisplayName}", warnings,
            RankDropModule, new DropModuleDetail(id),
            new Fact(ChangeKind.DropModule, id.SchemaName, id.ParentObjectName ?? id.ObjectName, id.ObjectName, id.ObjectType)
            {
                References = module.EffectiveReferences,
            });
        _objectDrops[Key(id)] = index;
    }

    private static int RankOf(DatabaseObjectType type) => type switch
    {
        DatabaseObjectType.ScalarFunction or DatabaseObjectType.TableValuedFunction => RankFunction,
        DatabaseObjectType.View => RankView,
        DatabaseObjectType.Trigger => RankTrigger,
        _ => RankProcedure,
    };

    private IEnumerable<(DatabaseObjectId Id, ModuleDefinition Module)> ModulesReferencing(DatabaseObjectId table) => WriteModulesReferencing(table);

    private IEnumerable<(DatabaseObjectId Id, ModuleDefinition Module)> WriteModulesReferencing(DatabaseObjectId target) =>
        WriteSchema.Schemas
            .SelectMany(schema => schema.Views.Select(view => (view.Id, view.Module))
                .Concat(schema.ProgrammableObjects.Select(module => (module.Id, module.Module))))
            .Where(pair => !SameObject(pair.Id, target)
                && pair.Module.EffectiveReferences.Any(reference => _names.Equals(reference.SchemaName, target.SchemaName) && _names.Equals(reference.ObjectName, target.ObjectName)));

    // Sequences

    private void PlanSequence(SchemaObjectDifference difference, Action action)
    {
        var id = difference.ObjectId;
        if (action == Action.Drop)
        {
            if (!_input.AllowDestructiveOperations)
            {
                ReportDestructive(id);
                return;
            }

            var writeId = IdOf(_writeObjects[Key(id)]);
            _objectDrops[Key(id)] = Add(
                writeId, SynchronizationOperationType.DropObject, OperationRisk.Critical, true, $"Drop sequence {writeId.DisplayName}",
                ["Defaults and code that use the sequence stop working."], RankDropSequence, new DropSequenceDetail(writeId),
                new Fact(ChangeKind.DropSequence, writeId.SchemaName, writeId.ObjectName, writeId.ObjectName, DatabaseObjectType.Sequence));
            return;
        }

        var sequence = (SequenceDefinition)_copyObjects[Key(id)];
        var warnings = new List<string>();
        if (action == Action.Alter)
        {
            var properties = difference.Properties.Select(property => property.PropertyName).ToHashSet(StringComparer.Ordinal);
            if (properties.Contains("DataType"))
            {
                _draft.Report(id, $"Sequence {id.DisplayName}: changing its type needs it to be recreated, which DataBridge does not do.");
                return;
            }

            properties.Remove("StartValue");
            if (properties.Count == 0)
            {
                _draft.Report(id, $"Sequence {id.DisplayName} differs only in its start value, which applies only when a sequence is created.");
                return;
            }

            if (difference.Properties.Any(property => property.PropertyName == "StartValue"))
            {
                warnings.Add("The start value is not changed; it applies only when a sequence is created.");
            }
        }
        else
        {
            EnsureSchema(id.SchemaName);
        }

        var isAlter = action == Action.Alter;
        _objectCreates[Key(id)] = Add(
            sequence.Id, isAlter ? SynchronizationOperationType.AlterObject : SynchronizationOperationType.CreateObject,
            isAlter ? OperationRisk.Medium : OperationRisk.Low, false, (isAlter ? "Alter sequence " : "Create sequence ") + sequence.Id.DisplayName,
            warnings, RankCreateSequence, new CreateSequenceDetail(sequence, isAlter),
            new Fact(ChangeKind.CreateSequence, id.SchemaName, id.ObjectName, id.ObjectName, DatabaseObjectType.Sequence));
    }

    // Dependencies

    /// <summary>Links every change to the changes that must run before it (see the class remarks).</summary>
    private void AddDependencies()
    {
        var facts = _facts.ToArray();
        IEnumerable<int> Where(Func<Fact, bool> predicate) => facts.Where(pair => predicate(pair.Value)).Select(pair => pair.Key);
        bool OnTable(Fact fact, string schema, string? table) =>
            fact.Table is not null && table is not null && _names.Equals(fact.Schema, schema) && _names.Equals(fact.Table, table);
        bool Is(Fact fact, ObjectKey key) => _names.Equals(fact.Schema, key.Schema) && _names.Equals(fact.Name, key.Name);

        foreach (var (index, fact) in facts)
        {
            IEnumerable<int> dependencies = [];
            if (IsCreate(fact.Kind) && fact.Kind != ChangeKind.CreateSchema && _schemaCreates.TryGetValue(fact.Schema, out var schemaCreate))
            {
                dependencies = dependencies.Append(schemaCreate);
            }

            switch (fact.Kind)
            {
                case ChangeKind.AddColumn or ChangeKind.AlterColumn or ChangeKind.CreateChild or ChangeKind.CreateForeignKey:
                case ChangeKind.CreateModule when fact.ObjectType == DatabaseObjectType.Trigger:
                    dependencies = dependencies.Concat(Where(other =>
                        other.Kind is ChangeKind.CreateTable or ChangeKind.CreateModule
                        && other.ObjectType is DatabaseObjectType.Table or DatabaseObjectType.View
                        && OnTable(other, fact.Schema, fact.Table)));
                    break;
            }

            if (fact.Kind is ChangeKind.CreateChild or ChangeKind.CreateForeignKey)
            {
                dependencies = dependencies.Concat(Where(other =>
                    other.Kind is ChangeKind.AddColumn or ChangeKind.AlterColumn && OnTable(other, fact.Schema, fact.Table)));
            }

            if (fact.Kind is ChangeKind.AlterColumn or ChangeKind.DropColumn)
            {
                dependencies = dependencies.Concat(Where(other =>
                    other.Kind is ChangeKind.DropChild or ChangeKind.DropForeignKey && OnTable(other, fact.Schema, fact.Table)));
            }

            if (fact is { Kind: ChangeKind.CreateForeignKey, Referenced: { } referenced })
            {
                dependencies = dependencies.Concat(Where(other =>
                    (other.Kind == ChangeKind.CreateTable && Is(other, referenced))
                    || (other.Kind is ChangeKind.CreateChild or ChangeKind.AddColumn or ChangeKind.AlterColumn
                        && OnTable(other, referenced.Schema, referenced.Name))));
            }

            if (fact.Kind == ChangeKind.DropChild && fact.ObjectType is DatabaseObjectType.PrimaryKey or DatabaseObjectType.UniqueConstraint or DatabaseObjectType.Index)
            {
                var table = new ObjectKey(fact.Schema, fact.Table!);
                dependencies = dependencies.Concat(Where(other => other is { Kind: ChangeKind.DropForeignKey, Referenced: { } target } && _keys.Equals(target, table)));
            }

            if (fact.Kind is ChangeKind.DropTable or ChangeKind.DropModule or ChangeKind.DropSequence)
            {
                var self = new ObjectKey(fact.Schema, fact.Name);
                dependencies = dependencies.Concat(Where(other =>
                    (other is { Kind: ChangeKind.DropForeignKey, Referenced: { } target } && _keys.Equals(target, self))
                    || (other.Kind == ChangeKind.DropModule && other.References.Any(reference => Is(fact, new ObjectKey(reference.SchemaName, reference.ObjectName))))));
            }

            if (fact.Kind == ChangeKind.CreateModule)
            {
                foreach (var reference in fact.References)
                {
                    var key = new ObjectKey(reference.SchemaName, reference.ObjectName);
                    dependencies = dependencies.Concat(Where(other =>
                        (other.Kind is ChangeKind.CreateTable or ChangeKind.CreateModule or ChangeKind.CreateSequence
                            && Is(other, key)
                            && other.ObjectType != DatabaseObjectType.Trigger
                            && !(fact.ObjectType == DatabaseObjectType.StoredProcedure && other.ObjectType == DatabaseObjectType.StoredProcedure))
                        || (other.Kind is ChangeKind.AddColumn or ChangeKind.AlterColumn && OnTable(other, key.Schema, key.Name))));
                }
            }

            foreach (var dependency in dependencies)
            {
                _draft.DependOn(index, dependency);
            }
        }
    }

    private static bool IsCreate(ChangeKind kind) => kind is ChangeKind.CreateSchema or ChangeKind.CreateTable or ChangeKind.AddColumn
        or ChangeKind.CreateChild or ChangeKind.CreateForeignKey or ChangeKind.CreateModule or ChangeKind.CreateSequence;

    // Helpers

    private int Add(
        DatabaseObjectId id,
        SynchronizationOperationType type,
        OperationRisk risk,
        bool destructive,
        string description,
        IReadOnlyList<string> warnings,
        int rank,
        SchemaChangeDetail detail,
        Fact fact)
    {
        var index = _draft.Add(new PlannedChange(id, type, risk, destructive, description, warnings, rank, detail));
        _facts[index] = fact;
        return index;
    }

    private void ReportDestructive(DatabaseObjectId id) =>
        _draft.Report(id, $"{id.DisplayName} is not dropped: destructive operations are not allowed. Allow them to include it.");

    /// <summary>The copied side's and the write target's name for a difference's object.</summary>
    private (string Copy, string Write) Names(SchemaObjectDifference difference)
    {
        var source = difference.ObjectId.ObjectName;
        var target = difference.TargetObjectName ?? source;
        return _input.Direction == SynchronizationDirection.SourceToTarget ? (source, target) : (target, source);
    }

    private bool InCopy(DatabaseObjectId table, string name, DatabaseObjectType type) =>
        _copyObjects.TryGetValue(Key(table), out var parent) && FindChild(parent, name, type) is not null;

    private bool References(ForeignKeyDefinition foreignKey, DatabaseObjectId table) =>
        foreignKey.ReferencedTableName is not null
        && _names.Equals(foreignKey.ReferencedSchemaName, table.SchemaName)
        && _names.Equals(foreignKey.ReferencedTableName, table.ObjectName);

    private bool SameObject(DatabaseObjectId left, DatabaseObjectId right) =>
        _names.Equals(left.SchemaName, right.SchemaName) && _names.Equals(left.ObjectName, right.ObjectName);

    private string? UnsupportedColumns(TableDefinition table) => table.Columns.Select(column => UnsupportedColumn(column) is { } reason ? $"column {column.Name}: {reason}" : null).FirstOrDefault(reason => reason is not null);

    /// <summary>Why a copied column cannot be created as it is, or <see langword="null"/>.</summary>
    private string? UnsupportedColumn(ColumnDefinition column)
    {
        var options = column.EffectiveOptions;
        if (options.IsFileStream || options.IsColumnSet || options.XmlSchemaCollection is not null || options.MaskingFunction is not null
            || options.EncryptionType is not null || options.GeneratedAlwaysType is not null || options.IsHidden)
        {
            return "FILESTREAM, column sets, typed XML, masking, Always Encrypted and generated-always columns are not synchronized.";
        }

        if (column.Computed is { Expression: null })
        {
            return "its computed expression is not visible.";
        }

        if (column.ProviderBaseTypeName is not null
            && !WriteSchema.UnsupportedObjects.Any(item => item.ObjectTypeDescription == "ALIAS_TYPE" && _names.Equals($"{item.SchemaName}.{item.ObjectName}", column.SqlTypeName)))
        {
            return $"it uses type {column.SqlTypeName}, which the write target does not have.";
        }

        return null;
    }

    private static string? HiddenConstraint(TableDefinition table) =>
        table.CheckConstraints.FirstOrDefault(check => check.Expression is null)?.Name
        ?? table.DefaultConstraints.FirstOrDefault(item => item.Expression is null)?.Name;

    private static (string Name, DatabaseObjectType Type, IReadOnlyList<string> Columns) Describe(object definition) => definition switch
    {
        PrimaryKeyDefinition key => (key.Name, DatabaseObjectType.PrimaryKey, [.. key.Columns.Select(column => column.ColumnName)]),
        UniqueConstraintDefinition key => (key.Name, DatabaseObjectType.UniqueConstraint, [.. key.Columns.Select(column => column.ColumnName)]),
        ForeignKeyDefinition key => (key.Name, DatabaseObjectType.ForeignKey, [.. key.Columns.Select(mapping => mapping.ColumnName)]),
        CheckConstraintDefinition check => (check.Name, DatabaseObjectType.CheckConstraint, []),
        DefaultConstraintDefinition defaultConstraint => (defaultConstraint.Name, DatabaseObjectType.DefaultConstraint, [defaultConstraint.ColumnName]),
        IndexDefinition index => (index.Name, DatabaseObjectType.Index, [.. index.Columns.Select(column => column.ColumnName)]),
        _ => throw new ArgumentException("Not a constraint or index.", nameof(definition)),
    };

    internal static string Describe(DatabaseObjectType type) => type switch
    {
        DatabaseObjectType.PrimaryKey => "primary key",
        DatabaseObjectType.UniqueConstraint => "unique constraint",
        DatabaseObjectType.ForeignKey => "foreign key",
        DatabaseObjectType.CheckConstraint => "check constraint",
        DatabaseObjectType.DefaultConstraint => "default constraint",
        DatabaseObjectType.Index => "index",
        DatabaseObjectType.View => "view",
        DatabaseObjectType.StoredProcedure => "procedure",
        DatabaseObjectType.ScalarFunction or DatabaseObjectType.TableValuedFunction => "function",
        DatabaseObjectType.Trigger => "trigger",
        _ => type.ToString().ToLowerInvariant(),
    };

    /// <summary>A column's type as SQL text, for descriptions only.</summary>
    internal static string Describe(ColumnDefinition column)
    {
        var facets = column switch
        {
            { IsMaxLength: true } => "(max)",
            { Precision: { } precision } => $"({precision},{column.Scale ?? 0})",
            { MaxLength: { } length } => $"({length})",
            { Scale: { } scale } => $"({scale})",
            _ => "",
        };
        return $"{column.SqlTypeName}{facets} {(column.IsNullable ? "NULL" : "NOT NULL")}";
    }

    private static DatabaseObjectId IdOf(object definition) => definition switch
    {
        TableDefinition table => table.Id,
        ViewDefinition view => view.Id,
        ProgrammableObjectDefinition module => module.Id,
        SequenceDefinition sequence => sequence.Id,
        _ => throw new ArgumentException("Not a schema object.", nameof(definition)),
    };

    private static ModuleDefinition ModuleOf(object definition) => definition switch
    {
        ViewDefinition view => view.Module,
        ProgrammableObjectDefinition module => module.Module,
        _ => throw new ArgumentException("Not a module.", nameof(definition)),
    };

    private static ObjectKey Key(DatabaseObjectId id) => new(id.SchemaName, id.ObjectName);

    private static ChildKey ChildKeyOf(DatabaseObjectId id) => new(id.SchemaName, id.ParentObjectName!, id.ObjectType, id.ObjectName);

    private Dictionary<ObjectKey, object> Objects(DatabaseSchema schema)
    {
        var objects = new Dictionary<ObjectKey, object>(_keys);
        foreach (var definition in schema.Schemas)
        {
            foreach (var item in definition.Tables)
            {
                objects[Key(item.Id)] = item;
            }

            foreach (var item in definition.Views)
            {
                objects[Key(item.Id)] = item;
            }

            foreach (var item in definition.ProgrammableObjects)
            {
                objects[Key(item.Id)] = item;
            }

            foreach (var item in definition.Sequences)
            {
                objects[Key(item.Id)] = item;
            }
        }

        return objects;
    }

    private sealed record Fact(ChangeKind Kind, string Schema, string? Table, string Name, DatabaseObjectType ObjectType)
    {
        public IReadOnlyList<string> Columns { get; init; } = [];

        public ObjectKey? Referenced { get; init; }

        public IReadOnlyList<ModuleReference> References { get; init; } = [];
    }

    private readonly record struct ObjectKey(string Schema, string Name);

    private readonly record struct ChildKey(string Schema, string Parent, DatabaseObjectType Type, string Name);

    private sealed class ObjectKeyComparer(StringComparer names) : IEqualityComparer<ObjectKey>
    {
        public bool Equals(ObjectKey x, ObjectKey y) => names.Equals(x.Schema, y.Schema) && names.Equals(x.Name, y.Name);

        public int GetHashCode(ObjectKey obj) => HashCode.Combine(names.GetHashCode(obj.Schema), names.GetHashCode(obj.Name));

        public bool Equals(ObjectKey? x, ObjectKey y) => x is { } value && Equals(value, y);
    }

    private sealed class ChildKeyComparer(StringComparer names) : IEqualityComparer<ChildKey>
    {
        public bool Equals(ChildKey x, ChildKey y) =>
            x.Type == y.Type && names.Equals(x.Schema, y.Schema) && names.Equals(x.Parent, y.Parent) && names.Equals(x.Name, y.Name);

        public int GetHashCode(ChildKey obj) =>
            HashCode.Combine(obj.Type, names.GetHashCode(obj.Schema), names.GetHashCode(obj.Parent), names.GetHashCode(obj.Name));
    }

    private sealed class ObjectIdComparer(StringComparer names) : IEqualityComparer<DatabaseObjectId>
    {
        public bool Equals(DatabaseObjectId x, DatabaseObjectId y) =>
            x.ObjectType == y.ObjectType
            && names.Equals(x.SchemaName, y.SchemaName)
            && names.Equals(x.ObjectName, y.ObjectName)
            && names.Equals(x.ParentObjectName ?? "", y.ParentObjectName ?? "");

        public int GetHashCode(DatabaseObjectId obj) =>
            HashCode.Combine(obj.ObjectType, names.GetHashCode(obj.SchemaName), names.GetHashCode(obj.ObjectName));
    }
}
