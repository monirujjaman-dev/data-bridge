namespace DataBridge.Core.Data;

/// <summary>
/// Whether two values of a compared column are the same stored value. Exact, kind by kind: NULL equals only NULL,
/// decimals compare numerically, text and binary compare ordinally and in full (trailing spaces count), and a
/// date-time-offset must match in both instant and offset.
/// </summary>
public static class ValueEquality
{
    public static bool AreEqual(DatabaseValue source, DatabaseValue target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        if (source.IsNull || target.IsNull)
        {
            return source.IsNull && target.IsNull;
        }

        if (source.Kind != target.Kind)
        {
            throw new ArgumentException($"Cannot compare a {source.Kind} value with a {target.Kind} value.", nameof(target));
        }

        return (source.RawValue, target.RawValue) switch
        {
            (DecimalValue left, DecimalValue right) => left.NumericallyEquals(right),

            // SQL Server floats are finite; 0 and -0 are the same stored number.
            (double left, double right) => left == right,
            (string left, string right) => string.Equals(left, right, StringComparison.Ordinal),
            (byte[] left, byte[] right) => left.AsSpan().SequenceEqual(right),
            (DateTimeOffset left, DateTimeOffset right) => left.EqualsExact(right),
            var (left, right) => left!.Equals(right),
        };
    }
}

/// <summary>
/// Orders key values exactly as the provider's server orders them in the comparison query (FR-DAT-016). The merge
/// relies on this order; it also verifies it, so a disagreement stops matching instead of producing wrong rows.
/// </summary>
public interface IKeyOrdering
{
    /// <summary>Compares two non-NULL key values of the same kind.</summary>
    int Compare(DatabaseValue x, DatabaseValue y);
}

/// <summary>
/// The provider-independent key order: numeric for numbers, chronological for temporal values, false before true,
/// unsigned bytewise for binary, and ordinal UTF-16 for text with the shorter string padded with spaces (SQL
/// comparisons pad, so <c>'a'</c> and <c>'a '</c> are the same key). Providers override what their engine orders
/// differently, such as GUIDs.
/// </summary>
public class DefaultKeyOrdering : IKeyOrdering
{
    public static readonly DefaultKeyOrdering Instance = new();

    public int Compare(DatabaseValue x, DatabaseValue y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        if (x.IsNull || y.IsNull)
        {
            throw new ArgumentException("Key values cannot be NULL.");
        }

        if (x.Kind != y.Kind)
        {
            throw new ArgumentException($"Cannot order a {x.Kind} key against a {y.Kind} key.");
        }

        return (x.RawValue, y.RawValue) switch
        {
            (string left, string right) => ComparePadded(left, right),
            (byte[] left, byte[] right) => left.AsSpan().SequenceCompareTo(right),
            (DecimalValue left, DecimalValue right) => left.CompareTo(right),
            (Guid left, Guid right) => CompareGuids(left, right),
            (DateTimeOffset left, DateTimeOffset right) => left.UtcDateTime.CompareTo(right.UtcDateTime),
            (IComparable left, var right) => left.CompareTo(right),
            _ => throw new ArgumentException($"{x.Kind} values cannot be ordered as keys."),
        };
    }

    /// <summary>
    /// Ordinal UTF-16 order as if the shorter string were padded with spaces, which is how SQL comparisons treat
    /// trailing spaces: <c>'a'</c> equals <c>'a '</c>, and <c>'a'</c> plus a tab sorts before <c>'a'</c>.
    /// </summary>
    public static int ComparePadded(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var length = Math.Max(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            var x = index < left.Length ? left[index] : ' ';
            var y = index < right.Length ? right[index] : ' ';
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }

    /// <summary>The engine's GUID order. The default is <see cref="System.Guid.CompareTo(System.Guid)"/>.</summary>
    protected virtual int CompareGuids(Guid x, Guid y) => x.CompareTo(y);
}

/// <summary>Lexicographic order of composite keys, column by column, with one <see cref="IKeyOrdering"/>.</summary>
public sealed class CompositeKeyComparer(IKeyOrdering ordering) : IComparer<IReadOnlyList<DatabaseValue>>
{
    private readonly IKeyOrdering _ordering = ordering ?? throw new ArgumentNullException(nameof(ordering));

    public int Compare(IReadOnlyList<DatabaseValue>? x, IReadOnlyList<DatabaseValue>? y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        if (x.Count != y.Count)
        {
            throw new ArgumentException("Keys must have the same number of columns.");
        }

        for (var index = 0; index < x.Count; index++)
        {
            var result = _ordering.Compare(x[index], y[index]);
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }
}
