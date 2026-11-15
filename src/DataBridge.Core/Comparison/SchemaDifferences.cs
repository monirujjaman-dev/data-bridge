using DataBridge.Core.Schema;

namespace DataBridge.Core.Comparison;

/// <summary>How an object or row on one side relates to the other side (FR-SCH-009).</summary>
public enum DifferenceStatus
{
    /// <summary>Present on both sides with no compared property differing.</summary>
    Equal,

    /// <summary>Present only in the source.</summary>
    SourceOnly,

    /// <summary>Present only in the target.</summary>
    TargetOnly,

    /// <summary>Present on both sides with at least one property differing.</summary>
    Modified,

    /// <summary>The same name on both sides, but objects of kinds that cannot be compared, such as a view and a table.</summary>
    Incompatible,

    /// <summary>Cannot be decided, for example because a definition is encrypted or hidden; never treated as equal.</summary>
    Unknown,
}

/// <summary>One compared property of an object whose values differ (FR-SCH-012).</summary>
/// <param name="PropertyName">The property, for example <c>Nullability</c> or <c>KeyColumns</c>.</param>
/// <param name="SourceValue">The source value as normalized metadata text; never row data.</param>
/// <param name="TargetValue">The target value as normalized metadata text; never row data.</param>
public sealed record PropertyDifference(string PropertyName, string? SourceValue, string? TargetValue);

/// <summary>The comparison outcome for one object.</summary>
/// <param name="ObjectId">The object; for a pair matched despite differing system-generated names, the source's identity.</param>
/// <param name="Status">How the two sides relate.</param>
/// <param name="Properties">The differing properties; empty unless <see cref="Status"/> is <see cref="DifferenceStatus.Modified"/>.</param>
/// <param name="Reason">Why the object is <see cref="DifferenceStatus.Incompatible"/> or <see cref="DifferenceStatus.Unknown"/>.</param>
/// <param name="TargetObjectName">
/// For a child paired despite a different system-generated name, the target's name; otherwise <see langword="null"/>.
/// </param>
public sealed record SchemaObjectDifference(
    DatabaseObjectId ObjectId,
    DifferenceStatus Status,
    IReadOnlyList<PropertyDifference> Properties,
    string? Reason = null,
    string? TargetObjectName = null);

/// <summary>
/// The result of comparing two schemas: one entry per supported object on either side, including equal ones, plus
/// the objects each side could not compare.
/// </summary>
/// <remarks>
/// A table or view with any differing child is <see cref="DifferenceStatus.Modified"/>; its children follow it with
/// their own entries. Children of a table present on one side only are not listed separately.
/// </remarks>
public sealed record SchemaComparisonResult(
    IReadOnlyList<SchemaObjectDifference> Differences,
    IReadOnlyList<UnsupportedObject> SourceUnsupportedObjects,
    IReadOnlyList<UnsupportedObject> TargetUnsupportedObjects)
{
    /// <summary>Whether any supported object is anything other than equal. Unsupported objects are listed separately.</summary>
    public bool HasDifferences => Differences.Any(difference => difference.Status != DifferenceStatus.Equal);

    public int Count(DifferenceStatus status) => Differences.Count(difference => difference.Status == status);
}
