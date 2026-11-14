namespace DataBridge.Core.Schema;

/// <summary>The kinds of database object that schema comparison supports (FR-SCH-001 to FR-SCH-008).</summary>
public enum DatabaseObjectType
{
    // Database-scoped
    Schema,

    // Schema-scoped objects
    Table,
    View,
    StoredProcedure,
    ScalarFunction,
    TableValuedFunction,
    Trigger,
    Sequence,

    // Table and view children
    Column,
    PrimaryKey,
    UniqueConstraint,
    ForeignKey,
    CheckConstraint,
    DefaultConstraint,
    Index,
}

/// <summary>
/// The identity of a database object (domain-model §5). Objects are matched across databases by this identity,
/// not by engine-assigned ids.
/// </summary>
/// <param name="SchemaName">The owning schema; for a <see cref="DatabaseObjectType.Schema"/>, the schema itself.</param>
/// <param name="ObjectName">The object's name; for a schema, the schema's name.</param>
/// <param name="ObjectType">The kind of object.</param>
/// <param name="ParentObjectName">
/// The owning table or view, required for table and view children and DML triggers: index and constraint
/// names are unique only within their table, so a child cannot be identified without it.
/// </param>
public readonly record struct DatabaseObjectId(
    string SchemaName,
    string ObjectName,
    DatabaseObjectType ObjectType,
    string? ParentObjectName = null)
{
    /// <summary>Whether this kind of object belongs to a table or view (columns, keys, constraints, indexes).</summary>
    public bool IsChild => IsChildType(ObjectType);

    /// <summary>A display name such as <c>dbo.Orders</c> or <c>dbo.Orders.IX_Orders_Date</c>. Not quoted SQL.</summary>
    public string DisplayName => ObjectType switch
    {
        DatabaseObjectType.Schema => SchemaName,
        _ when ParentObjectName is not null => $"{SchemaName}.{ParentObjectName}.{ObjectName}",
        _ => $"{SchemaName}.{ObjectName}",
    };

    public static DatabaseObjectId ForSchema(string name) => new(name, name, DatabaseObjectType.Schema);

    /// <summary>A child of the table or view <paramref name="parent"/>.</summary>
    public static DatabaseObjectId ForChild(DatabaseObjectId parent, string name, DatabaseObjectType type)
    {
        if (!IsChildType(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Not a table or view child type.");
        }

        return new(parent.SchemaName, name, type, parent.ObjectName);
    }

    public static bool IsChildType(DatabaseObjectType type) => type is DatabaseObjectType.Column
        or DatabaseObjectType.PrimaryKey
        or DatabaseObjectType.UniqueConstraint
        or DatabaseObjectType.ForeignKey
        or DatabaseObjectType.CheckConstraint
        or DatabaseObjectType.DefaultConstraint
        or DatabaseObjectType.Index;

    public override string ToString() => $"{ObjectType} {DisplayName}";
}
