namespace DataBridge.Core.Schema;

/// <summary>
/// The normalized schema of one database as read at <see cref="CapturedAtUtc"/> (domain-model §4). An observation,
/// not a lock: the database may change afterwards.
/// </summary>
/// <param name="DatabaseName">The database the schema was read from.</param>
/// <param name="Schemas">The schemas and their supported objects.</param>
/// <param name="UnsupportedObjects">Objects that exist but are not compared, with the reason (FR-SCH-010).</param>
/// <param name="IdentifiersAreCaseSensitive">Whether the database compares object names case-sensitively.</param>
/// <param name="HasCompleteMetadata">
/// Whether the reader could see every object. Without it (for SQL Server, no <c>VIEW DEFINITION</c>), objects can be
/// hidden, so an object missing from this side is reported as unknown rather than as missing.
/// </param>
/// <param name="CapturedAtUtc">When the metadata was read.</param>
public sealed record DatabaseSchema(
    string DatabaseName,
    IReadOnlyList<SchemaDefinition> Schemas,
    IReadOnlyList<UnsupportedObject> UnsupportedObjects,
    bool IdentifiersAreCaseSensitive,
    bool HasCompleteMetadata,
    DateTimeOffset CapturedAtUtc);

/// <summary>A schema and the supported objects it owns.</summary>
public sealed record SchemaDefinition(
    string Name,
    IReadOnlyList<TableDefinition> Tables,
    IReadOnlyList<ViewDefinition> Views,
    IReadOnlyList<ProgrammableObjectDefinition> ProgrammableObjects,
    IReadOnlyList<SequenceDefinition> Sequences)
{
    public static SchemaDefinition Empty(string name) => new(name, [], [], [], []);
}

/// <summary>
/// An object that exists but is not compared: an unsupported type, or one whose definition cannot be read
/// (encrypted, or no permission). Reported, never silently skipped.
/// </summary>
/// <param name="SchemaName">The owning schema; empty for database-level objects such as partition functions.</param>
/// <param name="ObjectName">The object's name; for a child, prefixed with its parent (<c>Table.Index</c>).</param>
/// <param name="ObjectTypeDescription">What the object is, as the engine describes it.</param>
/// <param name="Reason">Why it is not compared.</param>
public sealed record UnsupportedObject(
    string SchemaName,
    string ObjectName,
    string ObjectTypeDescription,
    string Reason)
{
    public string DisplayName => SchemaName.Length == 0 ? ObjectName : $"{SchemaName}.{ObjectName}";
}
