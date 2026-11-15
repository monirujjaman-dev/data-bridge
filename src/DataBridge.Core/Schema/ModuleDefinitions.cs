namespace DataBridge.Core.Schema;

/// <summary>
/// The SQL text and options of a view, procedure, function or trigger. <see cref="Text"/> is
/// <see langword="null"/> when it cannot be read; such modules are compared as unknown, never as equal.
/// </summary>
/// <param name="Text">The definition exactly as the engine stores it, or <see langword="null"/>.</param>
/// <param name="IsEncrypted">Whether the module was created <c>WITH ENCRYPTION</c>.</param>
/// <param name="IsSchemaBound">Whether the module is schema-bound.</param>
/// <param name="UsesAnsiNulls">The <c>ANSI_NULLS</c> setting the module was created with.</param>
/// <param name="UsesQuotedIdentifier">The <c>QUOTED_IDENTIFIER</c> setting the module was created with.</param>
/// <param name="References">
/// The objects in the same database the module references by name, as the engine resolved them; used to order
/// synchronization, not compared. Empty when unknown.
/// </param>
public sealed record ModuleDefinition(
    string? Text,
    bool IsEncrypted,
    bool IsSchemaBound,
    bool UsesAnsiNulls,
    bool UsesQuotedIdentifier,
    IReadOnlyList<ModuleReference>? References = null)
{
    public IReadOnlyList<ModuleReference> EffectiveReferences => References ?? [];

    public bool IsReadable => Text is not null;

    /// <summary>Why <see cref="Text"/> is missing, or <see langword="null"/> when it is readable.</summary>
    public string? UnreadableReason => Text is not null
        ? null
        : IsEncrypted
            ? "The definition is encrypted (WITH ENCRYPTION)."
            : "The definition is not visible to this login; it needs VIEW DEFINITION permission.";
}

/// <summary>A schema-scoped object a module references, such as a table or another view.</summary>
public sealed record ModuleReference(string SchemaName, string ObjectName);

/// <summary>A view and its indexes.</summary>
public sealed record ViewDefinition(
    DatabaseObjectId Id,
    ModuleDefinition Module,
    IReadOnlyList<IndexDefinition> Indexes);

/// <summary>Trigger-only properties.</summary>
public sealed record TriggerOptions(bool IsDisabled, bool IsInsteadOf);

/// <summary>
/// A stored procedure, function or DML trigger. A trigger's <see cref="DatabaseObjectId.ParentObjectName"/> is
/// its table or view, and <see cref="Trigger"/> is set.
/// </summary>
public sealed record ProgrammableObjectDefinition(
    DatabaseObjectId Id,
    ModuleDefinition Module,
    TriggerOptions? Trigger = null);
