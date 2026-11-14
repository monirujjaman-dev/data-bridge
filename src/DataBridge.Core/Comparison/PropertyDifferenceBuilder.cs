using System.Globalization;

namespace DataBridge.Core.Comparison;

/// <summary>
/// Collects differing properties of one object pair. Values are compared typed; text is produced only for
/// display, after a difference is found.
/// </summary>
internal sealed class PropertyDifferenceBuilder
{
    private readonly List<PropertyDifference> _differences = [];

    public bool HasDifferences => _differences.Count > 0;

    /// <summary>Why the pair cannot be compared, for example because a definition is hidden; makes it Unknown.</summary>
    public string? UnknownReason { get; private set; }

    public void MarkUnknown(string reason) => UnknownReason ??= reason;

    /// <summary>
    /// Like <see cref="AddSql"/>, but text that the login cannot see (<see langword="null"/>) on either side makes the
    /// pair Unknown: two hidden definitions are never treated as equal.
    /// </summary>
    public void AddHiddenSql(string property, string? source, string? target, Func<string, string> normalize)
    {
        if (source is null || target is null)
        {
            MarkUnknown($"The {property} is not visible to the login on one side; it needs VIEW DEFINITION permission.");
            return;
        }

        AddSql(property, source, target, normalize);
    }

    public IReadOnlyList<PropertyDifference> Build() => _differences.ToArray();

    public void Add<T>(string property, T source, T target, Func<T, string?> format)
    {
        if (!EqualityComparer<T>.Default.Equals(source, target))
        {
            _differences.Add(new PropertyDifference(property, format(source), format(target)));
        }
    }

    public void Add(string property, bool source, bool target) => Add(property, source, target, FormatBoolean);

    public void Add(string property, int source, int target) => Add(property, source, target, FormatInteger);

    /// <summary>Compares text with <paramref name="comparer"/> and shows the original values.</summary>
    public void AddText(string property, string? source, string? target, StringComparer comparer)
    {
        if (!comparer.Equals(source, target))
        {
            _differences.Add(new PropertyDifference(property, source, target));
        }
    }

    /// <summary>Compares the normalized forms of SQL text and shows the original text.</summary>
    public void AddSql(string property, string? source, string? target, Func<string, string> normalize)
    {
        var normalizedSource = source is null ? null : normalize(source);
        var normalizedTarget = target is null ? null : normalize(target);
        if (!string.Equals(normalizedSource, normalizedTarget, StringComparison.Ordinal))
        {
            _differences.Add(new PropertyDifference(property, source, target));
        }
    }

    /// <summary>Compares two lists element by element and shows each list as one line of text.</summary>
    public void AddList<T>(
        string property,
        IReadOnlyList<T> source,
        IReadOnlyList<T> target,
        Func<T, T, bool> equals,
        Func<T, string> format)
    {
        if (source.Count == target.Count && source.Zip(target).All(pair => equals(pair.First, pair.Second)))
        {
            return;
        }

        _differences.Add(new PropertyDifference(property, FormatList(source, format), FormatList(target, format)));
    }

    public static string FormatBoolean(bool value) => value ? "Yes" : "No";

    public static string FormatInteger(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string FormatList<T>(IReadOnlyList<T> items, Func<T, string> format) =>
        items.Count == 0 ? "(none)" : string.Join(", ", items.Select(format));
}
