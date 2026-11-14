using DataBridge.Core.Schema;

namespace DataBridge.Core.Tests.Comparison;

/// <summary>Concise builders for schema comparison tests.</summary>
internal static class SchemaFixtures
{
    public static DatabaseSchema Database(params SchemaDefinition[] schemas) =>
        new("Test", schemas, [], IdentifiersAreCaseSensitive: false, HasCompleteMetadata: true, DateTimeOffset.UnixEpoch);

    public static SchemaDefinition InSchema(string name, params object[] objects) => new(
        name,
        objects.OfType<TableDefinition>().ToArray(),
        objects.OfType<ViewDefinition>().ToArray(),
        objects.OfType<ProgrammableObjectDefinition>().ToArray(),
        objects.OfType<SequenceDefinition>().ToArray());

    public static TableDefinition Table(string name, params ColumnDefinition[] columns) =>
        new(new DatabaseObjectId("dbo", name, DatabaseObjectType.Table), Ordered(columns), null, [], [], [], [], []);

    public static ColumnDefinition Column(string name, string type = "int", bool nullable = false) =>
        new(name, type, null, false, null, null, nullable, null, null, null, 0);

    public static ColumnDefinition Text(string name, int length, bool nullable = true, string collation = "Latin1_General_CI_AS") =>
        new(name, "nvarchar", length, false, null, null, nullable, null, null, collation, 0);

    public static IndexColumnDefinition Key(string column, int ordinal, bool descending = false) => new(column, ordinal, descending, false);

    public static IndexColumnDefinition Include(string column) => new(column, 0, false, true);

    public static IndexOptions DefaultIndexOptions { get; } = new(false, 100, false, true, true, false, "NONE");

    public static IndexDefinition Index(string name, params IndexColumnDefinition[] columns) =>
        new(name, "NONCLUSTERED", false, columns, null, DefaultIndexOptions);

    public static PrimaryKeyDefinition PrimaryKey(string name, bool systemNamed, params string[] columns) =>
        new(name, systemNamed, "CLUSTERED", columns.Select((column, index) => Key(column, index + 1)).ToArray(), DefaultIndexOptions);

    public static ModuleDefinition Module(string? text, bool encrypted = false) => new(text, encrypted, false, true, true);

    public static ViewDefinition View(string name, string? text) =>
        new(new DatabaseObjectId("dbo", name, DatabaseObjectType.View), Module(text), []);

    public static ProgrammableObjectDefinition Procedure(string name, string? text, bool encrypted = false) =>
        new(new DatabaseObjectId("dbo", name, DatabaseObjectType.StoredProcedure), Module(text, encrypted));

    public static ProgrammableObjectDefinition Trigger(string name, string table, string text) =>
        new(new DatabaseObjectId("dbo", name, DatabaseObjectType.Trigger, table), Module(text), new TriggerOptions(false, false));

    public static SequenceDefinition Sequence(string name, long start = 1, long increment = 1) =>
        new(new DatabaseObjectId("dbo", name, DatabaseObjectType.Sequence), "bigint", start, increment, long.MinValue, long.MaxValue, false, true, null);

    private static ColumnDefinition[] Ordered(ColumnDefinition[] columns) =>
        columns.Select((column, index) => column with { OrdinalPosition = index + 1 }).ToArray();
}
