using System.Globalization;
using DataBridge.Core.Schema;
using Microsoft.Data.SqlClient;

namespace DataBridge.Infrastructure.Providers.SqlServer.Schema;

internal sealed record ObjectRow(int Id, int SchemaId, string Name, string Type, string TypeDescription, int ParentId);

internal sealed record TableKindRow(
    int Id,
    int TemporalType,
    bool IsMemoryOptimized,
    bool IsNode,
    bool IsEdge,
    bool IsFileTable,
    bool IsPartitioned,
    string? HeapDataCompression);

internal sealed record ColumnRow(
    int ObjectId,
    string Name,
    string TypeName,
    string TypeSchema,
    bool IsUserDefined,
    string? BaseTypeName,
    int MaxLength,
    byte Precision,
    byte Scale,
    bool IsNullable,
    string? Collation,
    string? IdentitySeed,
    string? IdentityIncrement,
    string? ComputedDefinition,
    bool IsPersisted,
    bool IsComputed,
    ColumnOptions Options,
    bool IdentityNotForReplication);

internal sealed record IndexRow(
    int ObjectId,
    int IndexId,
    string Name,
    int Type,
    string TypeDescription,
    bool IsUnique,
    bool IsPrimaryKey,
    bool IsUniqueConstraint,
    bool IsSystemNamed,
    IndexOptions Options,
    string? Filter);

internal sealed record IndexColumnRow(int ObjectId, int IndexId, IndexColumnDefinition Column);

internal sealed record ForeignKeyRow(
    int Id,
    int ParentId,
    string Name,
    bool IsSystemNamed,
    string? ReferencedSchema,
    string? ReferencedTable,
    int OnDelete,
    int OnUpdate,
    bool IsDisabled,
    bool IsNotForReplication,
    bool IsNotTrusted);

internal sealed record ForeignKeyColumnRow(int ConstraintId, ForeignKeyColumnMapping Mapping);

internal sealed record CheckRow(int ParentId, CheckConstraintDefinition Constraint);

internal sealed record DefaultRow(int ParentId, DefaultConstraintDefinition Constraint);

internal sealed record ModuleRow(int Id, ModuleDefinition Module);

internal sealed record TriggerRow(int Id, TriggerOptions Options);

internal sealed record SequenceRow(
    int Id,
    string TypeName,
    string StartValue,
    string Increment,
    string MinimumValue,
    string MaximumValue,
    bool IsCycling,
    bool IsCached,
    int? CacheSize);

/// <summary>The raw rows of <see cref="SqlServerCatalogQuery.Text"/>, read in result-set order.</summary>
internal sealed class SqlServerCatalog
{
    public int ComparisonStyle { get; private set; }

    public bool HasViewDefinition { get; private set; }

    public Dictionary<int, string> Schemas { get; } = [];

    public List<ObjectRow> Objects { get; } = [];

    public Dictionary<int, TableKindRow> TableKinds { get; } = [];

    public List<ColumnRow> Columns { get; } = [];

    public List<IndexRow> Indexes { get; } = [];

    public List<IndexColumnRow> IndexColumns { get; } = [];

    public List<ForeignKeyRow> ForeignKeys { get; } = [];

    public List<ForeignKeyColumnRow> ForeignKeyColumns { get; } = [];

    public List<CheckRow> CheckConstraints { get; } = [];

    public List<DefaultRow> DefaultConstraints { get; } = [];

    public Dictionary<int, ModuleRow> Modules { get; } = [];

    public Dictionary<int, TriggerRow> Triggers { get; } = [];

    public Dictionary<int, SequenceRow> Sequences { get; } = [];

    public List<UnsupportedObject> OtherUnsupported { get; } = [];

    public static async Task<SqlServerCatalog> ReadAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        var catalog = new SqlServerCatalog();

        if (await reader.ReadAsync(cancellationToken))
        {
            catalog.ComparisonStyle = Int(reader, 0);
            catalog.HasViewDefinition = Int(reader, 1) == 1;
        }

        await NextAsync(reader, cancellationToken);
        await ForEachAsync(reader, row => catalog.Schemas.Add(Int(row, 0), row.GetString(1)), cancellationToken);
        await ForEachAsync(reader, row => catalog.Objects.Add(ReadObject(row)), cancellationToken);
        await ForEachAsync(reader, row => catalog.TableKinds.Add(Int(row, 0), ReadTableKind(row)), cancellationToken);
        await ForEachAsync(reader, row => catalog.Columns.Add(ReadColumn(row)), cancellationToken);
        await ForEachAsync(reader, row => catalog.Indexes.Add(ReadIndex(row)), cancellationToken);
        await ForEachAsync(reader, row => catalog.IndexColumns.Add(ReadIndexColumn(row)), cancellationToken);
        await ForEachAsync(reader, row => catalog.ForeignKeys.Add(ReadForeignKey(row)), cancellationToken);
        await ForEachAsync(
            reader,
            row => catalog.ForeignKeyColumns.Add(new(Int(row, 0), new ForeignKeyColumnMapping(row.GetString(1), row.GetString(2)))),
            cancellationToken);
        await ForEachAsync(reader, row => catalog.CheckConstraints.Add(ReadCheck(row)), cancellationToken);
        await ForEachAsync(reader, row => catalog.DefaultConstraints.Add(ReadDefault(row)), cancellationToken);
        await ForEachAsync(reader, row => catalog.Modules.Add(Int(row, 0), ReadModule(row)), cancellationToken);
        await ForEachAsync(
            reader,
            row => catalog.Triggers.Add(Int(row, 0), new(Int(row, 0), new TriggerOptions(Bool(row, 1), Bool(row, 2)))),
            cancellationToken);
        await ForEachAsync(reader, row => catalog.Sequences.Add(Int(row, 0), ReadSequence(row)), cancellationToken);
        await ForEachAsync(
            reader,
            row => catalog.OtherUnsupported.Add(new(Text(row, 0) ?? "", row.GetString(1), row.GetString(2), row.GetString(3))),
            cancellationToken,
            last: true);

        return catalog;
    }

    private static async Task ForEachAsync(SqlDataReader reader, Action<SqlDataReader> read, CancellationToken cancellationToken, bool last = false)
    {
        while (await reader.ReadAsync(cancellationToken))
        {
            read(reader);
        }

        if (!last)
        {
            await NextAsync(reader, cancellationToken);
        }
    }

    private static async Task NextAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        if (!await reader.NextResultAsync(cancellationToken))
        {
            throw new InvalidOperationException("The catalog query returned fewer result sets than expected.");
        }
    }

    private static ObjectRow ReadObject(SqlDataReader row) =>
        new(Int(row, 0), Int(row, 1), row.GetString(2), row.GetString(3), row.GetString(4), Int(row, 5));

    private static TableKindRow ReadTableKind(SqlDataReader row) =>
        new(Int(row, 0), Int(row, 1), Bool(row, 2), Bool(row, 3), Bool(row, 4), Bool(row, 5), Bool(row, 6), Text(row, 7));

    private static ColumnRow ReadColumn(SqlDataReader row) => new(
        ObjectId: Int(row, 0),
        Name: row.GetString(1),
        TypeName: row.GetString(2),
        TypeSchema: row.GetString(3),
        IsUserDefined: Bool(row, 4),
        BaseTypeName: Text(row, 5),
        MaxLength: Int(row, 6),
        Precision: (byte)Int(row, 7),
        Scale: (byte)Int(row, 8),
        IsNullable: Bool(row, 9),
        Collation: Text(row, 10),
        IdentitySeed: Text(row, 11),
        IdentityIncrement: Text(row, 12),
        ComputedDefinition: Text(row, 13),
        IsPersisted: Bool(row, 14),
        IsComputed: Bool(row, 15),
        Options: new ColumnOptions(
            IsSparse: Bool(row, 17),
            IsColumnSet: Bool(row, 18),
            IsRowGuidColumn: Bool(row, 16),
            IsFileStream: Bool(row, 19),
            XmlSchemaCollection: Text(row, 20),
            MaskingFunction: Text(row, 21),
            EncryptionType: Text(row, 22),
            GeneratedAlwaysType: Text(row, 23),
            IsHidden: Bool(row, 24)),
        IdentityNotForReplication: Bool(row, 25));

    private static IndexRow ReadIndex(SqlDataReader row)
    {
        var fillFactor = Int(row, 10);
        return new IndexRow(
            ObjectId: Int(row, 0),
            IndexId: Int(row, 1),
            Name: row.GetString(2),
            Type: Int(row, 3),
            TypeDescription: row.GetString(4),
            IsUnique: Bool(row, 5),
            IsPrimaryKey: Bool(row, 6),
            IsUniqueConstraint: Bool(row, 7),
            IsSystemNamed: Bool(row, 8),
            Options: new IndexOptions(
                IgnoreDuplicateKey: Bool(row, 9),
                FillFactor: fillFactor == 0 ? 100 : fillFactor,
                IsPadded: Bool(row, 11),
                AllowRowLocks: Bool(row, 12),
                AllowPageLocks: Bool(row, 13),
                IsDisabled: Bool(row, 14),
                DataCompression: Text(row, 16)),
            Filter: Text(row, 15));
    }

    private static IndexColumnRow ReadIndexColumn(SqlDataReader row)
    {
        var keyOrdinal = Int(row, 3);

        // Columnstore indexes list their columns with key ordinal 0: they have no key order.
        var isIncluded = Bool(row, 5) || keyOrdinal == 0;
        return new IndexColumnRow(
            Int(row, 0),
            Int(row, 1),
            new IndexColumnDefinition(row.GetString(2), isIncluded ? 0 : keyOrdinal, !isIncluded && Bool(row, 4), isIncluded));
    }

    private static ForeignKeyRow ReadForeignKey(SqlDataReader row) => new(
        Int(row, 0), Int(row, 1), row.GetString(2), Bool(row, 3), Text(row, 4), Text(row, 5),
        Int(row, 6), Int(row, 7), Bool(row, 8), Bool(row, 9), Bool(row, 10));

    // A definition is NULL when the login cannot see it: kept as NULL so the comparison reports it as unknown.
    private static CheckRow ReadCheck(SqlDataReader row) =>
        new(Int(row, 0), new CheckConstraintDefinition(row.GetString(1), Bool(row, 2), Text(row, 3), Bool(row, 4), Bool(row, 5), Bool(row, 6)));

    private static DefaultRow ReadDefault(SqlDataReader row) =>
        new(Int(row, 0), new DefaultConstraintDefinition(row.GetString(1), Bool(row, 2), row.GetString(3), Text(row, 4)));

    private static ModuleRow ReadModule(SqlDataReader row) =>
        new(Int(row, 0), new ModuleDefinition(Text(row, 1), Bool(row, 2), Bool(row, 3), Bool(row, 4), Bool(row, 5)));

    private static SequenceRow ReadSequence(SqlDataReader row) => new(
        Int(row, 0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4), row.GetString(5),
        Bool(row, 6), Bool(row, 7), row.IsDBNull(8) ? null : Int(row, 8));

    /// <summary>Catalog integers come as tinyint, smallint or int; read them without assuming the width.</summary>
    private static int Int(SqlDataReader row, int ordinal) =>
        Convert.ToInt32(row.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static bool Bool(SqlDataReader row, int ordinal) => !row.IsDBNull(ordinal) && row.GetBoolean(ordinal);

    private static string? Text(SqlDataReader row, int ordinal) => row.IsDBNull(ordinal) ? null : row.GetString(ordinal);
}
