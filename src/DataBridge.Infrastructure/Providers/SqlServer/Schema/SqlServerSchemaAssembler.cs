using System.Globalization;
using System.Numerics;
using DataBridge.Core.Schema;

namespace DataBridge.Infrastructure.Providers.SqlServer.Schema;

/// <summary>
/// Turns catalog rows into the normalized schema model: SQL Server type facets, key constraints split from
/// indexes, triggers attached to their parent, and every object that cannot be compared listed with a reason.
/// </summary>
internal sealed class SqlServerSchemaAssembler(SqlServerCatalog catalog)
{
    private static readonly HashSet<string> _byteLengthTypes = new(["char", "varchar", "binary", "varbinary"], StringComparer.Ordinal);
    private static readonly HashSet<string> _unicodeLengthTypes = new(["nchar", "nvarchar"], StringComparer.Ordinal);
    private static readonly HashSet<string> _precisionTypes = new(["decimal", "numeric"], StringComparer.Ordinal);
    private static readonly HashSet<string> _scaleTypes = new(["decimal", "numeric", "time", "datetime2", "datetimeoffset"], StringComparer.Ordinal);

    private readonly Dictionary<int, ObjectRow> _objects = catalog.Objects.ToDictionary(row => row.Id);
    private readonly Dictionary<int, TableBuilder> _tables = [];
    private readonly Dictionary<int, ViewBuilder> _views = [];
    private readonly HashSet<int> _excluded = [];
    private readonly List<ProgrammableObjectDefinition> _modules = [];
    private readonly List<SequenceDefinition> _sequences = [];
    private readonly List<UnsupportedObject> _unsupported = [];

    public DatabaseSchema Build(string databaseName, DateTimeOffset capturedAtUtc)
    {
        foreach (var row in catalog.Objects)
        {
            AddObject(row);
        }

        AddColumns();
        AddIndexes();
        AddForeignKeys();
        AddConstraints();

        var unsupported = _unsupported.Concat(catalog.OtherUnsupported)
            .OrderBy(item => item.SchemaName, StringComparer.Ordinal)
            .ThenBy(item => item.ObjectName, StringComparer.Ordinal)
            .ToArray();

        return new DatabaseSchema(
            databaseName,
            BuildSchemas(),
            unsupported,
            IdentifiersAreCaseSensitive: (catalog.ComparisonStyle & 1) == 0,
            HasCompleteMetadata: catalog.HasViewDefinition && !HasHiddenDefinitions(),
            capturedAtUtc);
    }

    /// <summary>
    /// Database-level <c>VIEW DEFINITION</c> can be denied per schema or object, so hidden definitions found while
    /// reading also mean that objects may be hidden. Encrypted modules are excluded: they are visible, only unreadable.
    /// </summary>
    private bool HasHiddenDefinitions() =>
        catalog.Modules.Values.Any(row => row.Module.Text is null && !row.Module.IsEncrypted)
        || catalog.CheckConstraints.Any(row => row.Constraint.Expression is null)
        || catalog.DefaultConstraints.Any(row => row.Constraint.Expression is null)
        || catalog.Columns.Any(row => row.IsComputed && row.ComputedDefinition is null)
        || catalog.ForeignKeys.Any(row => row.ReferencedTable is null);

    private SchemaDefinition[] BuildSchemas()
    {
        static T[] InSchema<T>(IEnumerable<T> items, Func<T, DatabaseObjectId> id, string schema) =>
            items.Where(item => id(item).SchemaName == schema).OrderBy(item => id(item).ObjectName, StringComparer.Ordinal).ToArray();

        var tables = _tables.Values.Select(table => table.Build()).ToArray();
        var views = _views.Values.Select(view => view.Build()).ToArray();
        return catalog.Schemas.Values
            .Order(StringComparer.Ordinal)
            .Select(schema => new SchemaDefinition(
                schema,
                InSchema(tables, table => table.Id, schema),
                InSchema(views, view => view.Id, schema),
                InSchema(_modules, module => module.Id, schema),
                InSchema(_sequences, sequence => sequence.Id, schema)))
            .ToArray();
    }

    private void AddObject(ObjectRow row)
    {
        var schema = SchemaOf(row);
        switch (row.Type)
        {
            case "U":
                AddTable(row, schema);
                break;
            case "V":
                _views[row.Id] = new ViewBuilder(new DatabaseObjectId(schema, row.Name, DatabaseObjectType.View), Module(row));
                break;
            case "P":
                AddModule(row, new DatabaseObjectId(schema, row.Name, DatabaseObjectType.StoredProcedure));
                break;
            case "FN":
                AddModule(row, new DatabaseObjectId(schema, row.Name, DatabaseObjectType.ScalarFunction));
                break;
            case "IF" or "TF":
                AddModule(row, new DatabaseObjectId(schema, row.Name, DatabaseObjectType.TableValuedFunction));
                break;
            case "TR":
                var parent = _objects.TryGetValue(row.ParentId, out var owner) ? owner.Name : throw new CatalogChangedException();
                var trigger = catalog.Triggers.TryGetValue(row.Id, out var options) ? options.Options : throw new CatalogChangedException();
                AddModule(row, new DatabaseObjectId(schema, row.Name, DatabaseObjectType.Trigger, parent), trigger);
                break;
            case "SO":
                _sequences.Add(Sequence(row, schema));
                break;
            default:
                _unsupported.Add(new UnsupportedObject(schema, row.Name, row.TypeDescription, $"Objects of type {row.TypeDescription} are not compared."));
                break;
        }
    }

    private void AddTable(ObjectRow row, string schema)
    {
        var kind = catalog.TableKinds.TryGetValue(row.Id, out var found) ? found : throw new CatalogChangedException();
        if (UnsupportedTableReason(kind) is { } reason)
        {
            _excluded.Add(row.Id);
            _unsupported.Add(new UnsupportedObject(schema, row.Name, row.TypeDescription, reason));
            return;
        }

        if (kind.IsPartitioned)
        {
            _unsupported.Add(new UnsupportedObject(schema, row.Name, "PARTITIONING", "Table partitioning is not compared; the rest of the table is."));
        }

        _tables[row.Id] = new TableBuilder(new DatabaseObjectId(schema, row.Name, DatabaseObjectType.Table)) { HeapDataCompression = kind.HeapDataCompression };
    }

    private static string? UnsupportedTableReason(TableKindRow kind) => kind switch
    {
        { TemporalType: 2 } => "System-versioned temporal tables are not compared.",
        { TemporalType: 1 } => "Temporal history tables are not compared.",
        { IsMemoryOptimized: true } => "Memory-optimized tables are not compared.",
        { IsNode: true } or { IsEdge: true } => "Graph tables are not compared.",
        { IsFileTable: true } => "FileTables are not compared.",
        _ => null,
    };

    private void AddModule(ObjectRow row, DatabaseObjectId id, TriggerOptions? trigger = null) =>
        _modules.Add(new ProgrammableObjectDefinition(id, Module(row), trigger));

    private ModuleDefinition Module(ObjectRow row)
    {
        var module = catalog.Modules.TryGetValue(row.Id, out var found) ? found.Module : throw new CatalogChangedException();
        if (module.UnreadableReason is { } reason)
        {
            _unsupported.Add(new UnsupportedObject(SchemaOf(row), row.Name, row.TypeDescription, reason));
        }

        return catalog.ModuleReferences.TryGetValue(row.Id, out var references)
            ? module with { References = [.. references.OrderBy(reference => reference.SchemaName, StringComparer.Ordinal).ThenBy(reference => reference.ObjectName, StringComparer.Ordinal)] }
            : module;
    }

    private SequenceDefinition Sequence(ObjectRow row, string schema)
    {
        var sequence = catalog.Sequences.TryGetValue(row.Id, out var found) ? found : throw new CatalogChangedException();
        return new SequenceDefinition(
            new DatabaseObjectId(schema, row.Name, DatabaseObjectType.Sequence),
            sequence.TypeName,
            Integer(sequence.StartValue),
            Integer(sequence.Increment),
            Integer(sequence.MinimumValue),
            Integer(sequence.MaximumValue),
            sequence.IsCycling,
            sequence.IsCached,
            sequence.IsCached ? sequence.CacheSize : null);
    }

    private void AddColumns()
    {
        foreach (var row in catalog.Columns)
        {
            if (Table(row.ObjectId) is { } table)
            {
                table.Columns.Add(Column(row, table.Columns.Count + 1));
            }
        }
    }

    private static ColumnDefinition Column(ColumnRow row, int ordinal)
    {
        var baseType = row.BaseTypeName ?? row.TypeName;
        var isCharacterOrBinary = _byteLengthTypes.Contains(baseType) || _unicodeLengthTypes.Contains(baseType);
        var isMax = isCharacterOrBinary && row.MaxLength == -1;
        int? length = isCharacterOrBinary && !isMax
            ? _unicodeLengthTypes.Contains(baseType) ? row.MaxLength / 2 : row.MaxLength
            : null;

        return new ColumnDefinition(
            Name: row.Name,
            SqlTypeName: row.IsUserDefined ? $"{row.TypeSchema}.{row.TypeName}" : row.TypeName,
            MaxLength: length,
            IsMaxLength: isMax,
            Precision: _precisionTypes.Contains(baseType) ? row.Precision : null,
            Scale: _scaleTypes.Contains(baseType) ? row.Scale : null,
            IsNullable: row.IsNullable,
            Identity: row.IdentitySeed is { } seed && row.IdentityIncrement is { } increment
                ? new IdentitySpecification(Integer(seed), Integer(increment), row.IdentityNotForReplication)
                : null,
            Computed: row.IsComputed ? new ComputedSpecification(row.ComputedDefinition, row.IsPersisted) : null,
            CollationName: row.Collation,
            OrdinalPosition: ordinal,
            Options: row.Options == ColumnOptions.None ? null : row.Options,
            ProviderBaseTypeName: row.IsUserDefined ? row.BaseTypeName : null);
    }

    private void AddIndexes()
    {
        var columns = catalog.IndexColumns
            .GroupBy(row => (row.ObjectId, row.IndexId))
            .ToDictionary(group => group.Key, group => group.Select(row => row.Column).ToArray());

        foreach (var row in catalog.Indexes)
        {
            var keyColumns = columns.GetValueOrDefault((row.ObjectId, row.IndexId)) ?? [];
            if (_views.TryGetValue(row.ObjectId, out var view))
            {
                AddIndex(view.Id, view.Indexes, row, keyColumns);
            }
            else if (Table(row.ObjectId) is { } table)
            {
                if (row.IsPrimaryKey)
                {
                    table.PrimaryKey = new PrimaryKeyDefinition(row.Name, row.IsSystemNamed, row.TypeDescription, keyColumns, row.Options);
                }
                else if (row.IsUniqueConstraint)
                {
                    table.UniqueConstraints.Add(new UniqueConstraintDefinition(row.Name, row.IsSystemNamed, row.TypeDescription, keyColumns, row.Options));
                }
                else
                {
                    AddIndex(table.Id, table.Indexes, row, keyColumns);
                }
            }
        }
    }

    private void AddIndex(DatabaseObjectId owner, List<IndexDefinition> indexes, IndexRow row, IndexColumnDefinition[] columns)
    {
        // 3 = XML, 4 = spatial: their options and paths are not modeled.
        if (row.Type is 3 or 4)
        {
            _unsupported.Add(new UnsupportedObject(owner.SchemaName, $"{owner.ObjectName}.{row.Name}", row.TypeDescription, "XML and spatial indexes are not compared."));
            return;
        }

        indexes.Add(new IndexDefinition(row.Name, row.TypeDescription, row.IsUnique, columns, row.Filter, row.Options));
    }

    private void AddForeignKeys()
    {
        var columns = catalog.ForeignKeyColumns
            .GroupBy(row => row.ConstraintId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.Mapping).ToArray());

        foreach (var row in catalog.ForeignKeys)
        {
            Table(row.ParentId)?.ForeignKeys.Add(new ForeignKeyDefinition(
                row.Name,
                row.IsSystemNamed,
                row.ReferencedSchema,
                row.ReferencedTable,
                columns.GetValueOrDefault(row.Id) ?? [],
                Action(row.OnDelete),
                Action(row.OnUpdate),
                row.IsDisabled,
                row.IsNotForReplication,
                row.IsNotTrusted));
        }
    }

    private void AddConstraints()
    {
        foreach (var row in catalog.CheckConstraints)
        {
            Table(row.ParentId)?.CheckConstraints.Add(row.Constraint);
        }

        foreach (var row in catalog.DefaultConstraints)
        {
            Table(row.ParentId)?.DefaultConstraints.Add(row.Constraint);
        }
    }

    /// <summary>The table a child belongs to; <see langword="null"/> for a table that is not compared.</summary>
    /// <exception cref="CatalogChangedException">The parent is unknown, so the schema changed during the read.</exception>
    private TableBuilder? Table(int objectId) =>
        _tables.TryGetValue(objectId, out var table)
            ? table
            : _excluded.Contains(objectId) ? null : throw new CatalogChangedException();

    private string SchemaOf(ObjectRow row) =>
        catalog.Schemas.TryGetValue(row.SchemaId, out var schema) ? schema : throw new CatalogChangedException();

    private static ReferentialAction Action(int value) => value switch
    {
        0 => ReferentialAction.NoAction,
        1 => ReferentialAction.Cascade,
        2 => ReferentialAction.SetNull,
        3 => ReferentialAction.SetDefault,
        _ => throw new InvalidOperationException($"Unknown referential action {value}."),
    };

    private static BigInteger Integer(string value) => BigInteger.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    private sealed class TableBuilder(DatabaseObjectId id)
    {
        public DatabaseObjectId Id { get; } = id;

        public List<ColumnDefinition> Columns { get; } = [];

        public PrimaryKeyDefinition? PrimaryKey { get; set; }

        public List<UniqueConstraintDefinition> UniqueConstraints { get; } = [];

        public List<ForeignKeyDefinition> ForeignKeys { get; } = [];

        public List<CheckConstraintDefinition> CheckConstraints { get; } = [];

        public List<DefaultConstraintDefinition> DefaultConstraints { get; } = [];

        public string? HeapDataCompression { get; init; }

        public List<IndexDefinition> Indexes { get; } = [];

        public TableDefinition Build() => new(
            Id,
            Columns.ToArray(),
            PrimaryKey,
            [.. UniqueConstraints.OrderBy(item => item.Name, StringComparer.Ordinal)],
            [.. ForeignKeys.OrderBy(item => item.Name, StringComparer.Ordinal)],
            [.. CheckConstraints.OrderBy(item => item.Name, StringComparer.Ordinal)],
            [.. DefaultConstraints.OrderBy(item => item.Name, StringComparer.Ordinal)],
            [.. Indexes.OrderBy(item => item.Name, StringComparer.Ordinal)],
            HeapDataCompression);
    }

    private sealed class ViewBuilder(DatabaseObjectId id, ModuleDefinition module)
    {
        public DatabaseObjectId Id { get; } = id;

        public List<IndexDefinition> Indexes { get; } = [];

        public ViewDefinition Build() => new(Id, module, [.. Indexes.OrderBy(item => item.Name, StringComparer.Ordinal)]);
    }
}
