using DataBridge.Core.Schema;

namespace DataBridge.Core.Comparison;

/// <summary>
/// Compares two normalized schemas object by object (FR-SCH-001 to FR-SCH-012). Pure and provider-independent:
/// the provider supplies the SQL text normalization rules.
/// </summary>
/// <remarks>
/// <para>
/// Schema-scoped objects (tables, views, procedures, functions, triggers, sequences) share one namespace per schema,
/// so they are matched by schema and name; the same name with different object kinds is
/// <see cref="DifferenceStatus.Incompatible"/>. Table and view children are matched by name within their parent.
/// </para>
/// <para>
/// Names are compared case-insensitively only when both databases compare identifiers case-insensitively;
/// otherwise ordinally. Names are never lowercased.
/// </para>
/// <para>
/// Constraints whose names the engine generated (for example <c>PK__Orders__C3905BAF</c>) get different names in
/// every database. When no constraint of the same name exists, two system-named constraints with the same
/// definition (the same key columns, expression, column or reference) are matched and their names are not
/// compared. A table has at most one primary key, so primary keys are always matched to each other.
/// </para>
/// <para>
/// When one side could not see every object (<see cref="DatabaseSchema.HasCompleteMetadata"/>), an object missing
/// from that side is <see cref="DifferenceStatus.Unknown"/>, not source- or target-only. A module whose definition
/// cannot be read on either side is <see cref="DifferenceStatus.Unknown"/>, never equal.
/// </para>
/// </remarks>
public sealed class SchemaComparer(ISqlDefinitionNormalizer normalizer)
{
    private readonly ISqlDefinitionNormalizer _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));

    /// <summary>Compares <paramref name="source"/> with <paramref name="target"/>. Does not touch either database.</summary>
    public SchemaComparisonResult Compare(DatabaseSchema source, DatabaseSchema target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var differences = new SchemaComparisonRun(source, target, _normalizer, cancellationToken).Execute();
        return new SchemaComparisonResult(differences, source.UnsupportedObjects, target.UnsupportedObjects);
    }
}

/// <summary>The state of one comparison: the two schemas, the name comparer and the differences found so far.</summary>
internal sealed class SchemaComparisonRun
{
    private readonly DatabaseSchema _source;
    private readonly DatabaseSchema _target;
    private readonly ISqlDefinitionNormalizer _normalizer;
    private readonly CancellationToken _cancellationToken;
    private readonly StringComparer _names;
    private readonly List<SchemaObjectDifference> _differences = [];

    public SchemaComparisonRun(
        DatabaseSchema source,
        DatabaseSchema target,
        ISqlDefinitionNormalizer normalizer,
        CancellationToken cancellationToken)
    {
        _source = source;
        _target = target;
        _normalizer = normalizer;
        _cancellationToken = cancellationToken;
        _names = source.IdentifiersAreCaseSensitive || target.IdentifiersAreCaseSensitive
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
    }

    public IReadOnlyList<SchemaObjectDifference> Execute()
    {
        CompareSchemas();
        CompareSchemaObjects();
        return _differences.OrderBy(difference => difference.ObjectId, SchemaOrder.Instance).ToArray();
    }

    private void CompareSchemas()
    {
        var targetNames = new HashSet<string>(_target.Schemas.Select(schema => schema.Name), _names);
        var sourceNames = new HashSet<string>(_source.Schemas.Select(schema => schema.Name), _names);

        foreach (var schema in _source.Schemas)
        {
            if (targetNames.Contains(schema.Name))
            {
                _differences.Add(Equal(DatabaseObjectId.ForSchema(schema.Name)));
            }
            else
            {
                AddMissing(DatabaseObjectId.ForSchema(schema.Name), presentInSource: true);
            }
        }

        foreach (var schema in _target.Schemas.Where(schema => !sourceNames.Contains(schema.Name)))
        {
            AddMissing(DatabaseObjectId.ForSchema(schema.Name), presentInSource: false);
        }
    }

    private void CompareSchemaObjects()
    {
        var keyComparer = new QualifiedNameComparer(_names);
        var targetObjects = new Dictionary<(string Schema, string Name), SchemaObject>(keyComparer);
        foreach (var item in SchemaObject.All(_target))
        {
            targetObjects.Add((item.Id.SchemaName, item.Id.ObjectName), item);
        }

        var matchedTargets = new HashSet<(string Schema, string Name)>(keyComparer);
        foreach (var sourceObject in SchemaObject.All(_source))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var key = (sourceObject.Id.SchemaName, sourceObject.Id.ObjectName);
            if (targetObjects.TryGetValue(key, out var targetObject))
            {
                matchedTargets.Add(key);
                ComparePair(sourceObject, targetObject);
            }
            else
            {
                AddMissing(sourceObject.Id, presentInSource: true);
            }
        }

        foreach (var (key, targetObject) in targetObjects)
        {
            if (!matchedTargets.Contains(key))
            {
                AddMissing(targetObject.Id, presentInSource: false);
            }
        }
    }

    private void ComparePair(SchemaObject source, SchemaObject target)
    {
        if (source.Id.ObjectType != target.Id.ObjectType)
        {
            _differences.Add(new SchemaObjectDifference(
                source.Id,
                DifferenceStatus.Incompatible,
                [],
                $"The source object is a {Describe(source.Id.ObjectType)} and the target object is a {Describe(target.Id.ObjectType)}."));
            return;
        }

        if (source.Id.ObjectType == DatabaseObjectType.Trigger
            && !_names.Equals(source.Id.ParentObjectName, target.Id.ParentObjectName))
        {
            _differences.Add(new SchemaObjectDifference(
                source.Id,
                DifferenceStatus.Incompatible,
                [],
                $"The trigger belongs to {source.Id.ParentObjectName} in the source and to {target.Id.ParentObjectName} in the target."));
            return;
        }

        switch (source.Definition, target.Definition)
        {
            case (TableDefinition sourceTable, TableDefinition targetTable):
                CompareTable(sourceTable, targetTable);
                break;
            case (ViewDefinition sourceView, ViewDefinition targetView):
                CompareView(sourceView, targetView);
                break;
            case (ProgrammableObjectDefinition sourceModule, ProgrammableObjectDefinition targetModule):
                CompareProgrammableObject(sourceModule, targetModule);
                break;
            case (SequenceDefinition sourceSequence, SequenceDefinition targetSequence):
                CompareSequence(sourceSequence, targetSequence);
                break;
            default:
                throw new InvalidOperationException($"No comparison is defined for {source.Id.ObjectType}.");
        }
    }

    private void AddMissing(DatabaseObjectId id, bool presentInSource)
    {
        var otherSide = presentInSource ? _target : _source;
        if (otherSide.HasCompleteMetadata)
        {
            _differences.Add(new SchemaObjectDifference(id, presentInSource ? DifferenceStatus.SourceOnly : DifferenceStatus.TargetOnly, []));
            return;
        }

        var (found, hidden) = presentInSource ? ("source", "target") : ("target", "source");
        _differences.Add(new SchemaObjectDifference(
            id,
            DifferenceStatus.Unknown,
            [],
            $"Found only in the {found}, but the {hidden} login cannot see every object, so it may exist in the {hidden} too."));
    }

    private void AddWithChildren(
        DatabaseObjectId id,
        PropertyDifferenceBuilder? properties,
        string? unknownReason,
        List<SchemaObjectDifference> children)
    {
        var own = properties?.Build() ?? [];
        var status = unknownReason is not null
            ? DifferenceStatus.Unknown
            : own.Count > 0 || children.Exists(child => child.Status != DifferenceStatus.Equal)
                ? DifferenceStatus.Modified
                : DifferenceStatus.Equal;

        _differences.Add(new SchemaObjectDifference(id, status, unknownReason is null ? own : [], unknownReason));
        _differences.AddRange(children);
    }

    // Tables

    private void CompareTable(TableDefinition source, TableDefinition target)
    {
        var parent = source.Id;
        var children = new List<SchemaObjectDifference>();

        CompareChildren(children, parent, DatabaseObjectType.Column, source.Columns, target.Columns, column => column.Name, CompareColumn);
        ComparePrimaryKeys(children, parent, source.PrimaryKey, target.PrimaryKey);
        CompareChildren(
            children,
            parent,
            DatabaseObjectType.UniqueConstraint,
            source.UniqueConstraints,
            target.UniqueConstraints,
            constraint => constraint.Name,
            CompareUniqueConstraint,
            new SystemNameMatch<UniqueConstraintDefinition>(
                constraint => constraint.IsSystemNamed,
                (left, right) => SameKeyColumns(left.Columns, right.Columns)));
        CompareChildren(
            children,
            parent,
            DatabaseObjectType.ForeignKey,
            source.ForeignKeys,
            target.ForeignKeys,
            foreignKey => foreignKey.Name,
            CompareForeignKey,
            new SystemNameMatch<ForeignKeyDefinition>(foreignKey => foreignKey.IsSystemNamed, SameReference));
        CompareChildren(
            children,
            parent,
            DatabaseObjectType.CheckConstraint,
            source.CheckConstraints,
            target.CheckConstraints,
            constraint => constraint.Name,
            CompareCheckConstraint,
            new SystemNameMatch<CheckConstraintDefinition>(
                constraint => constraint.IsSystemNamed,
                (left, right) => SameSql(left.Expression, right.Expression)));
        CompareChildren(
            children,
            parent,
            DatabaseObjectType.DefaultConstraint,
            source.DefaultConstraints,
            target.DefaultConstraints,
            constraint => constraint.Name,
            CompareDefaultConstraint,
            new SystemNameMatch<DefaultConstraintDefinition>(
                constraint => constraint.IsSystemNamed,
                (left, right) => _names.Equals(left.ColumnName, right.ColumnName)));
        CompareChildren(children, parent, DatabaseObjectType.Index, source.Indexes, target.Indexes, index => index.Name, CompareIndex);

        AddWithChildren(parent, null, null, children);
    }

    private void CompareColumn(ColumnDefinition source, ColumnDefinition target, PropertyDifferenceBuilder properties)
    {
        properties.AddText("DataType", source.SqlTypeName, target.SqlTypeName, _names);
        properties.Add("Length", FormatLength(source), FormatLength(target), value => value);
        properties.Add("Precision", source.Precision, target.Precision, FormatNullable);
        properties.Add("Scale", source.Scale, target.Scale, FormatNullable);
        properties.Add("Nullable", source.IsNullable, target.IsNullable);
        properties.AddText("Collation", source.CollationName, target.CollationName, StringComparer.OrdinalIgnoreCase);
        properties.Add("Identity", source.Identity, target.Identity, FormatIdentity);
        properties.AddSql("ComputedExpression", source.Computed?.Expression, target.Computed?.Expression, _normalizer.Normalize);
        if (source.Computed is not null && target.Computed is not null)
        {
            properties.Add("Persisted", source.Computed.IsPersisted, target.Computed.IsPersisted);
        }

        properties.Add("OrdinalPosition", source.OrdinalPosition, target.OrdinalPosition);
    }

    private void ComparePrimaryKeys(
        List<SchemaObjectDifference> children,
        DatabaseObjectId parent,
        PrimaryKeyDefinition? source,
        PrimaryKeyDefinition? target)
    {
        switch (source, target)
        {
            case (null, null):
                return;
            case (not null, null):
                children.Add(new(DatabaseObjectId.ForChild(parent, source.Name, DatabaseObjectType.PrimaryKey), DifferenceStatus.SourceOnly, []));
                return;
            case (null, not null):
                children.Add(new(DatabaseObjectId.ForChild(parent, target.Name, DatabaseObjectType.PrimaryKey), DifferenceStatus.TargetOnly, []));
                return;
        }

        var properties = new PropertyDifferenceBuilder();
        if (!(source.IsSystemNamed && target.IsSystemNamed))
        {
            properties.AddText("Name", source.Name, target.Name, _names);
        }

        CompareKey(source.IndexType, source.Columns, target.IndexType, target.Columns, properties);
        children.Add(Child(DatabaseObjectId.ForChild(parent, source.Name, DatabaseObjectType.PrimaryKey), properties));
    }

    private void CompareUniqueConstraint(UniqueConstraintDefinition source, UniqueConstraintDefinition target, PropertyDifferenceBuilder properties) =>
        CompareKey(source.IndexType, source.Columns, target.IndexType, target.Columns, properties);

    private void CompareKey(
        string sourceIndexType,
        IReadOnlyList<IndexColumnDefinition> sourceColumns,
        string targetIndexType,
        IReadOnlyList<IndexColumnDefinition> targetColumns,
        PropertyDifferenceBuilder properties)
    {
        properties.AddText("IndexType", sourceIndexType, targetIndexType, StringComparer.OrdinalIgnoreCase);
        properties.AddList("KeyColumns", KeyColumns(sourceColumns), KeyColumns(targetColumns), SameIndexColumn, FormatIndexColumn);
    }

    private void CompareForeignKey(ForeignKeyDefinition source, ForeignKeyDefinition target, PropertyDifferenceBuilder properties)
    {
        properties.AddText("ReferencedTable", ReferencedTable(source), ReferencedTable(target), _names);
        properties.AddList("Columns", source.Columns, target.Columns, SameMapping, mapping => $"{mapping.ColumnName} -> {mapping.ReferencedColumnName}");
        properties.Add("OnDelete", source.OnDelete, target.OnDelete, FormatAction);
        properties.Add("OnUpdate", source.OnUpdate, target.OnUpdate, FormatAction);
        properties.Add("Disabled", source.IsDisabled, target.IsDisabled);
        properties.Add("NotForReplication", source.IsNotForReplication, target.IsNotForReplication);
    }

    private void CompareCheckConstraint(CheckConstraintDefinition source, CheckConstraintDefinition target, PropertyDifferenceBuilder properties)
    {
        properties.AddSql("Expression", source.Expression, target.Expression, _normalizer.Normalize);
        properties.Add("Disabled", source.IsDisabled, target.IsDisabled);
        properties.Add("NotForReplication", source.IsNotForReplication, target.IsNotForReplication);
    }

    private void CompareDefaultConstraint(DefaultConstraintDefinition source, DefaultConstraintDefinition target, PropertyDifferenceBuilder properties)
    {
        properties.AddText("Column", source.ColumnName, target.ColumnName, _names);
        properties.AddSql("Expression", source.Expression, target.Expression, _normalizer.Normalize);
    }

    private void CompareIndex(IndexDefinition source, IndexDefinition target, PropertyDifferenceBuilder properties)
    {
        CompareKey(source.IndexType, source.Columns, target.IndexType, target.Columns, properties);
        properties.Add("Unique", source.IsUnique, target.IsUnique);
        properties.AddList("IncludedColumns", IncludedColumns(source.Columns), IncludedColumns(target.Columns), _names.Equals, name => name);
        properties.AddSql("Filter", source.FilterDefinition, target.FilterDefinition, _normalizer.Normalize);

        var (sourceOptions, targetOptions) = (source.Options, target.Options);
        properties.Add("IgnoreDuplicateKey", sourceOptions.IgnoreDuplicateKey, targetOptions.IgnoreDuplicateKey);
        properties.Add("FillFactor", sourceOptions.FillFactor, targetOptions.FillFactor);
        properties.Add("PadIndex", sourceOptions.IsPadded, targetOptions.IsPadded);
        properties.Add("AllowRowLocks", sourceOptions.AllowRowLocks, targetOptions.AllowRowLocks);
        properties.Add("AllowPageLocks", sourceOptions.AllowPageLocks, targetOptions.AllowPageLocks);
        properties.Add("Disabled", sourceOptions.IsDisabled, targetOptions.IsDisabled);
        properties.AddText("DataCompression", sourceOptions.DataCompression, targetOptions.DataCompression, StringComparer.OrdinalIgnoreCase);
    }

    // Views, procedures, functions, triggers, sequences

    private void CompareView(ViewDefinition source, ViewDefinition target)
    {
        var children = new List<SchemaObjectDifference>();
        CompareChildren(children, source.Id, DatabaseObjectType.Index, source.Indexes, target.Indexes, index => index.Name, CompareIndex);

        var properties = new PropertyDifferenceBuilder();
        var unknownReason = UnreadableReason(source.Module, target.Module);
        if (unknownReason is null)
        {
            CompareModule(source.Module, target.Module, properties);
        }

        AddWithChildren(source.Id, properties, unknownReason, children);
    }

    private void CompareProgrammableObject(ProgrammableObjectDefinition source, ProgrammableObjectDefinition target)
    {
        var properties = new PropertyDifferenceBuilder();
        var unknownReason = UnreadableReason(source.Module, target.Module);
        if (unknownReason is null)
        {
            CompareModule(source.Module, target.Module, properties);
        }

        if (source.Trigger is { } sourceTrigger && target.Trigger is { } targetTrigger)
        {
            properties.Add("Disabled", sourceTrigger.IsDisabled, targetTrigger.IsDisabled);
            properties.Add("InsteadOf", sourceTrigger.IsInsteadOf, targetTrigger.IsInsteadOf);
        }

        AddWithChildren(source.Id, properties, unknownReason, []);
    }

    private void CompareModule(ModuleDefinition source, ModuleDefinition target, PropertyDifferenceBuilder properties)
    {
        properties.AddSql("Definition", source.Text, target.Text, _normalizer.Normalize);
        properties.Add("SchemaBinding", source.IsSchemaBound, target.IsSchemaBound);
        properties.Add("AnsiNulls", source.UsesAnsiNulls, target.UsesAnsiNulls);
        properties.Add("QuotedIdentifier", source.UsesQuotedIdentifier, target.UsesQuotedIdentifier);
    }

    private static string? UnreadableReason(ModuleDefinition source, ModuleDefinition target)
    {
        if (source.IsReadable && target.IsReadable)
        {
            return null;
        }

        var reasons = new List<string>(2);
        if (!source.IsReadable)
        {
            reasons.Add($"Source: {source.UnreadableReason}");
        }

        if (!target.IsReadable)
        {
            reasons.Add($"Target: {target.UnreadableReason}");
        }

        return string.Join(" ", reasons);
    }

    private void CompareSequence(SequenceDefinition source, SequenceDefinition target)
    {
        var properties = new PropertyDifferenceBuilder();
        properties.AddText("DataType", source.SqlTypeName, target.SqlTypeName, _names);
        properties.Add("StartValue", source.StartValue, target.StartValue, SqlText.Integer);
        properties.Add("Increment", source.Increment, target.Increment, SqlText.Integer);
        properties.Add("MinimumValue", source.MinimumValue, target.MinimumValue, SqlText.Integer);
        properties.Add("MaximumValue", source.MaximumValue, target.MaximumValue, SqlText.Integer);
        properties.Add("Cycle", source.IsCycling, target.IsCycling);
        properties.Add("Cache", (source.IsCached, source.CacheSize), (target.IsCached, target.CacheSize), FormatCache);
        AddWithChildren(source.Id, properties, null, []);
    }

    // Child matching

    /// <summary>How to pair system-named children left over after matching by name.</summary>
    private sealed record SystemNameMatch<T>(Func<T, bool> IsSystemNamed, Func<T, T, bool> SameDefinition);

    private void CompareChildren<T>(
        List<SchemaObjectDifference> output,
        DatabaseObjectId parent,
        DatabaseObjectType type,
        IReadOnlyList<T> source,
        IReadOnlyList<T> target,
        Func<T, string> nameOf,
        Action<T, T, PropertyDifferenceBuilder> compare,
        SystemNameMatch<T>? systemNameMatch = null)
        where T : class
    {
        var unmatchedTarget = new Dictionary<string, T>(target.Count, _names);
        foreach (var item in target)
        {
            unmatchedTarget.Add(nameOf(item), item);
        }

        var pairs = new List<(T Source, T Target)>();
        var unmatchedSource = new List<T>();
        foreach (var item in source)
        {
            if (unmatchedTarget.Remove(nameOf(item), out var match))
            {
                pairs.Add((item, match));
            }
            else
            {
                unmatchedSource.Add(item);
            }
        }

        if (systemNameMatch is not null)
        {
            PairSystemNamed(unmatchedSource, unmatchedTarget, systemNameMatch, nameOf, pairs);
        }

        foreach (var (sourceItem, targetItem) in pairs)
        {
            var properties = new PropertyDifferenceBuilder();
            compare(sourceItem, targetItem, properties);
            output.Add(Child(DatabaseObjectId.ForChild(parent, nameOf(sourceItem), type), properties));
        }

        output.AddRange(unmatchedSource.Select(item =>
            new SchemaObjectDifference(DatabaseObjectId.ForChild(parent, nameOf(item), type), DifferenceStatus.SourceOnly, [])));
        output.AddRange(unmatchedTarget.Values.Select(item =>
            new SchemaObjectDifference(DatabaseObjectId.ForChild(parent, nameOf(item), type), DifferenceStatus.TargetOnly, [])));
    }

    private static void PairSystemNamed<T>(
        List<T> unmatchedSource,
        Dictionary<string, T> unmatchedTarget,
        SystemNameMatch<T> match,
        Func<T, string> nameOf,
        List<(T Source, T Target)> pairs)
        where T : class
    {
        foreach (var sourceItem in unmatchedSource.Where(match.IsSystemNamed).ToArray())
        {
            var targetItem = unmatchedTarget.Values.FirstOrDefault(candidate =>
                match.IsSystemNamed(candidate) && match.SameDefinition(sourceItem, candidate));
            if (targetItem is null)
            {
                continue;
            }

            pairs.Add((sourceItem, targetItem));
            unmatchedSource.Remove(sourceItem);
            unmatchedTarget.Remove(nameOf(targetItem));
        }
    }

    private static SchemaObjectDifference Child(DatabaseObjectId id, PropertyDifferenceBuilder properties) =>
        new(id, properties.HasDifferences ? DifferenceStatus.Modified : DifferenceStatus.Equal, properties.Build());

    private static SchemaObjectDifference Equal(DatabaseObjectId id) => new(id, DifferenceStatus.Equal, []);

    // Definition equality used for matching

    private bool SameSql(string left, string right) =>
        string.Equals(_normalizer.Normalize(left), _normalizer.Normalize(right), StringComparison.Ordinal);

    private bool SameKeyColumns(IReadOnlyList<IndexColumnDefinition> left, IReadOnlyList<IndexColumnDefinition> right)
    {
        var (leftKey, rightKey) = (KeyColumns(left), KeyColumns(right));
        return leftKey.Length == rightKey.Length &&leftKey.Zip(rightKey).All(pair => SameIndexColumn(pair.First, pair.Second));
    }

    private bool SameReference(ForeignKeyDefinition left, ForeignKeyDefinition right) =>
        _names.Equals(ReferencedTable(left), ReferencedTable(right))
        && left.Columns.Count == right.Columns.Count
        && left.Columns.Zip(right.Columns).All(pair => SameMapping(pair.First, pair.Second));

    private bool SameIndexColumn(IndexColumnDefinition left, IndexColumnDefinition right) =>
        _names.Equals(left.ColumnName, right.ColumnName) && left.IsDescending == right.IsDescending;

    private bool SameMapping(ForeignKeyColumnMapping left, ForeignKeyColumnMapping right) =>
        _names.Equals(left.ColumnName, right.ColumnName) && _names.Equals(left.ReferencedColumnName, right.ReferencedColumnName);

    private static IndexColumnDefinition[] KeyColumns(IReadOnlyList<IndexColumnDefinition> columns) =>
        columns.Where(column => !column.IsIncluded).OrderBy(column => column.KeyOrdinal).ToArray();

    /// <summary>Included columns have no order, so they are compared as a sorted list.</summary>
    private string[] IncludedColumns(IReadOnlyList<IndexColumnDefinition> columns) =>
        columns.Where(column => column.IsIncluded).Select(column => column.ColumnName).Order(_names).ToArray();

    private static string ReferencedTable(ForeignKeyDefinition foreignKey) =>
        $"{foreignKey.ReferencedSchemaName}.{foreignKey.ReferencedTableName}";

    // Display formatting

    private static string FormatIndexColumn(IndexColumnDefinition column) =>
        $"{column.ColumnName} {(column.IsDescending ? "DESC" : "ASC")}";

    private static string? FormatLength(ColumnDefinition column) =>
        column.IsMaxLength ? "max" : column.MaxLength is { } length ? PropertyDifferenceBuilder.FormatInteger(length) : null;

    private static string? FormatNullable(byte? value) => value is { } number ? PropertyDifferenceBuilder.FormatInteger(number) : null;

    private static string FormatIdentity(IdentitySpecification? identity) =>
        identity is null ? "No" : $"IDENTITY({SqlText.Integer(identity.Seed)}, {SqlText.Integer(identity.Increment)})";

    private static string FormatAction(ReferentialAction action) => action switch
    {
        ReferentialAction.NoAction => "NO ACTION",
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    private static string FormatCache((bool IsCached, int? Size) cache) => cache switch
    {
        (false, _) => "NO CACHE",
        (true, null) => "CACHE (default)",
        (true, { } size) => $"CACHE {PropertyDifferenceBuilder.FormatInteger(size)}",
    };

    private static string Describe(DatabaseObjectType type) => type switch
    {
        DatabaseObjectType.StoredProcedure => "stored procedure",
        DatabaseObjectType.ScalarFunction => "scalar function",
        DatabaseObjectType.TableValuedFunction => "table-valued function",
        _ => type.ToString().ToLowerInvariant(),
    };

    private sealed class QualifiedNameComparer(StringComparer names) : IEqualityComparer<(string Schema, string Name)>
    {
        public bool Equals((string Schema, string Name) x, (string Schema, string Name) y) =>
            names.Equals(x.Schema, y.Schema) && names.Equals(x.Name, y.Name);

        public int GetHashCode((string Schema, string Name) obj) =>
            HashCode.Combine(names.GetHashCode(obj.Schema), names.GetHashCode(obj.Name));
    }
}

/// <summary>A schema-scoped object of any supported kind, for matching by schema and name.</summary>
internal sealed record SchemaObject(DatabaseObjectId Id, object Definition)
{
    public static IEnumerable<SchemaObject> All(DatabaseSchema schema) =>
        schema.Schemas.SelectMany(definition => definition.Tables.Select(table => new SchemaObject(table.Id, table))
            .Concat(definition.Views.Select(view => new SchemaObject(view.Id, view)))
            .Concat(definition.ProgrammableObjects.Select(module => new SchemaObject(module.Id, module)))
            .Concat(definition.Sequences.Select(sequence => new SchemaObject(sequence.Id, sequence))));
}

/// <summary>
/// The order of comparison results: by schema, the schema itself first, then each object followed by its
/// triggers and children.
/// </summary>
internal sealed class SchemaOrder : IComparer<DatabaseObjectId>
{
    public static readonly SchemaOrder Instance = new();

    public int Compare(DatabaseObjectId x, DatabaseObjectId y)
    {
        var result = string.CompareOrdinal(x.SchemaName, y.SchemaName);
        if (result != 0)
        {
            return result;
        }

        result = IsSchema(x).CompareTo(IsSchema(y));
        if (result != 0)
        {
            return -result;
        }

        result = string.CompareOrdinal(x.ParentObjectName ?? x.ObjectName, y.ParentObjectName ?? y.ObjectName);
        if (result != 0)
        {
            return result;
        }

        result = (x.ParentObjectName is not null).CompareTo(y.ParentObjectName is not null);
        if (result != 0)
        {
            return result;
        }

        result = x.ObjectType.CompareTo(y.ObjectType);
        return result != 0 ? result : string.CompareOrdinal(x.ObjectName, y.ObjectName);
    }

    private static bool IsSchema(DatabaseObjectId id) => id.ObjectType == DatabaseObjectType.Schema;
}
