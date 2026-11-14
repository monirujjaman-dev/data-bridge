using System.Globalization;
using System.Numerics;

namespace DataBridge.Core.Data;

/// <summary>
/// The comparison family of a column's values. Two columns are compared only when their kinds match, so values are
/// never coerced across families (FR-DAT-015). The provider maps its types to kinds.
/// </summary>
public enum ValueKind
{
    /// <summary>Whole numbers of any width, as <see cref="long"/>.</summary>
    WholeNumber,

    /// <summary>Exact numerics and money, as <see cref="DecimalValue"/>.</summary>
    ExactNumeric,

    /// <summary>Floating point, as <see cref="double"/>.</summary>
    ApproximateNumeric,

    Boolean,

    /// <summary>Character data of any width or encoding, as <see cref="string"/>.</summary>
    Text,

    /// <summary>Bytes, as a private copy of a <see cref="byte"/> array.</summary>
    Binary,

    UniqueIdentifier,

    /// <summary>A calendar date, as <see cref="DateOnly"/>.</summary>
    Date,

    /// <summary>A time of day, as <see cref="TimeSpan"/>.</summary>
    Time,

    /// <summary>A date and time without offset, as <see cref="System.DateTime"/>.</summary>
    DateTime,

    /// <summary>A date and time with offset, as <see cref="System.DateTimeOffset"/>.</summary>
    DateTimeOffset,

    /// <summary>XML text as the server serializes it, as <see cref="string"/>.</summary>
    Xml,

    /// <summary>Server-generated row versions: different in every database, so never compared.</summary>
    RowVersion,

    /// <summary>A type the comparison cannot read safely; such columns are excluded and reported.</summary>
    Unsupported,
}

/// <summary>
/// An exact decimal: <c>Unscaled × 10^-Scale</c>. SQL Server decimals have up to 38 digits, more than
/// <see cref="decimal"/> holds, so the value is kept as a <see cref="BigInteger"/>. Equality and ordering are
/// numeric: 1.50 equals 1.5.
/// </summary>
public readonly record struct DecimalValue(BigInteger Unscaled, int Scale) : IComparable<DecimalValue>
{
    /// <summary>The same number with trailing fractional zeros removed.</summary>
    public DecimalValue Normalize()
    {
        var (unscaled, scale) = (Unscaled, Scale);
        while (scale > 0 && !unscaled.IsZero && unscaled % 10 == 0)
        {
            unscaled /= 10;
            scale--;
        }

        return unscaled.IsZero ? new DecimalValue(0, 0) : new DecimalValue(unscaled, scale);
    }

    public bool NumericallyEquals(DecimalValue other) => CompareTo(other) == 0;

    public static bool operator <(DecimalValue left, DecimalValue right) => left.CompareTo(right) < 0;

    public static bool operator <=(DecimalValue left, DecimalValue right) => left.CompareTo(right) <= 0;

    public static bool operator >(DecimalValue left, DecimalValue right) => left.CompareTo(right) > 0;

    public static bool operator >=(DecimalValue left, DecimalValue right) => left.CompareTo(right) >= 0;

    public int CompareTo(DecimalValue other)
    {
        var scale = Math.Max(Scale, other.Scale);
        return (Unscaled * BigInteger.Pow(10, scale - Scale)).CompareTo(other.Unscaled * BigInteger.Pow(10, scale - other.Scale));
    }

    /// <summary>Invariant text such as <c>-12.50</c>, keeping the scale. For display only.</summary>
    public override string ToString()
    {
        var digits = BigInteger.Abs(Unscaled).ToString(CultureInfo.InvariantCulture).PadLeft(Scale + 1, '0');
        var text = Scale == 0 ? digits : $"{digits[..^Scale]}.{digits[^Scale..]}";
        return Unscaled.Sign < 0 ? $"-{text}" : text;
    }
}

/// <summary>
/// One typed column value (domain-model §6). The CLR representation is fixed by <see cref="Kind"/> and enforced by
/// the factory methods; NULL is explicit. Values are compared by kind-specific rules, never by
/// <see cref="object.ToString"/>.
/// </summary>
public sealed class DatabaseValue
{
    private readonly object? _value;

    private DatabaseValue(ValueKind kind, string sqlTypeName, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlTypeName);
        Kind = kind;
        SqlTypeName = sqlTypeName;
        _value = value;
    }

    public ValueKind Kind { get; }

    /// <summary>The column's type as the provider names it, for display.</summary>
    public string SqlTypeName { get; }

    public bool IsNull => _value is null;

    public static DatabaseValue Null(ValueKind kind, string sqlTypeName) => new(kind, sqlTypeName, null);

    public static DatabaseValue FromInt64(long value, string sqlTypeName) => new(ValueKind.WholeNumber, sqlTypeName, value);

    public static DatabaseValue FromDecimal(DecimalValue value, string sqlTypeName) => new(ValueKind.ExactNumeric, sqlTypeName, value);

    public static DatabaseValue FromDouble(double value, string sqlTypeName) =>
        double.IsFinite(value)
            ? new(ValueKind.ApproximateNumeric, sqlTypeName, value)
            : throw new ArgumentOutOfRangeException(nameof(value), "SQL floating-point values are finite.");

    public static DatabaseValue FromBoolean(bool value, string sqlTypeName) => new(ValueKind.Boolean, sqlTypeName, value);

    public static DatabaseValue FromText(string value, string sqlTypeName) =>
        new(ValueKind.Text, sqlTypeName, value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>Copies <paramref name="value"/>, so later changes to the array do not change the value.</summary>
    public static DatabaseValue FromBinary(ReadOnlySpan<byte> value, string sqlTypeName) => new(ValueKind.Binary, sqlTypeName, value.ToArray());

    public static DatabaseValue FromGuid(Guid value, string sqlTypeName) => new(ValueKind.UniqueIdentifier, sqlTypeName, value);

    public static DatabaseValue FromDate(DateOnly value, string sqlTypeName) => new(ValueKind.Date, sqlTypeName, value);

    public static DatabaseValue FromTime(TimeSpan value, string sqlTypeName) =>
        value >= TimeSpan.Zero && value < TimeSpan.FromDays(1)
            ? new(ValueKind.Time, sqlTypeName, value)
            : throw new ArgumentOutOfRangeException(nameof(value), "A time of day is between 00:00 and 24:00.");

    public static DatabaseValue FromDateTime(DateTime value, string sqlTypeName) => new(ValueKind.DateTime, sqlTypeName, value);

    public static DatabaseValue FromDateTimeOffset(DateTimeOffset value, string sqlTypeName) => new(ValueKind.DateTimeOffset, sqlTypeName, value);

    public static DatabaseValue FromXml(string value, string sqlTypeName) =>
        new(ValueKind.Xml, sqlTypeName, value ?? throw new ArgumentNullException(nameof(value)));

    public long AsInt64() => As<long>(ValueKind.WholeNumber);

    public DecimalValue AsDecimal() => As<DecimalValue>(ValueKind.ExactNumeric);

    public double AsDouble() => As<double>(ValueKind.ApproximateNumeric);

    public bool AsBoolean() => As<bool>(ValueKind.Boolean);

    public string AsString() => Kind is ValueKind.Text or ValueKind.Xml && _value is string text
        ? text
        : throw WrongKind(ValueKind.Text);

    public ReadOnlySpan<byte> AsBytes() => Kind == ValueKind.Binary && _value is byte[] bytes ? bytes : throw WrongKind(ValueKind.Binary);

    public Guid AsGuid() => As<Guid>(ValueKind.UniqueIdentifier);

    public DateOnly AsDate() => As<DateOnly>(ValueKind.Date);

    public TimeSpan AsTime() => As<TimeSpan>(ValueKind.Time);

    public DateTime AsDateTime() => As<DateTime>(ValueKind.DateTime);

    public DateTimeOffset AsDateTimeOffset() => As<DateTimeOffset>(ValueKind.DateTimeOffset);

    /// <summary>Invariant, unambiguous text for showing the value to the user. Never used for comparison or logging.</summary>
    public string ToDisplayString() => _value switch
    {
        null => "NULL",
        long number => number.ToString(CultureInfo.InvariantCulture),
        DecimalValue number => number.ToString(),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        bool flag => flag ? "1" : "0",
        string text => text,
        byte[] bytes => bytes.Length <= 64 ? $"0x{Convert.ToHexString(bytes)}" : $"0x{Convert.ToHexString(bytes, 0, 64)}… ({bytes.Length} bytes)",
        Guid guid => guid.ToString("D"),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeSpan time => time.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture),
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException("Unexpected value representation."),
    };

    /// <summary>The raw representation, for the comparers in this namespace.</summary>
    internal object? RawValue => _value;

    public override string ToString() => $"{SqlTypeName} value";

    private T As<T>(ValueKind kind) => Kind == kind && _value is T value ? value : throw WrongKind(kind);

    private InvalidOperationException WrongKind(ValueKind expected) =>
        new(IsNull ? $"The value is NULL, not a {expected} value." : $"The value is {Kind}, not {expected}.");
}
