using System.Numerics;
using DataBridge.Core.Data;

namespace DataBridge.Core.Tests.Data;

public sealed class DatabaseValueTests
{
    private static DatabaseValue Dec(long unscaled, int scale) => DatabaseValue.FromDecimal(new DecimalValue(unscaled, scale), "decimal");

    private static DatabaseValue Text(string value) => DatabaseValue.FromText(value, "nvarchar");

    [Fact]
    public void Null_equals_only_null()
    {
        var nullText = DatabaseValue.Null(ValueKind.Text, "nvarchar");

        Assert.True(ValueEquality.AreEqual(nullText, DatabaseValue.Null(ValueKind.Text, "nvarchar")));
        Assert.False(ValueEquality.AreEqual(nullText, Text("")));
        Assert.False(ValueEquality.AreEqual(Text(""), nullText));
    }

    [Fact]
    public void Decimals_compare_numerically_and_keep_38_digits()
    {
        var big = BigInteger.Parse("12345678901234567890123456789012345678", System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(ValueEquality.AreEqual(Dec(150, 2), Dec(15, 1)));
        Assert.False(ValueEquality.AreEqual(Dec(150, 2), Dec(151, 2)));
        Assert.False(ValueEquality.AreEqual(
            DatabaseValue.FromDecimal(new DecimalValue(big, 0), "decimal"),
            DatabaseValue.FromDecimal(new DecimalValue(big + 1, 0), "decimal")));
        Assert.Equal("-0.05", new DecimalValue(-5, 2).ToString());
        Assert.Equal(new DecimalValue(15, 1), new DecimalValue(1500, 3).Normalize());
    }

    [Fact]
    public void Text_and_binary_compare_exactly_including_trailing_spaces_and_case()
    {
        Assert.False(ValueEquality.AreEqual(Text("a"), Text("a ")));
        Assert.False(ValueEquality.AreEqual(Text("a"), Text("A")));
        Assert.True(ValueEquality.AreEqual(Text("ü"), Text("ü")));
        Assert.False(ValueEquality.AreEqual(Text("ü"), Text("ü")));
        Assert.False(ValueEquality.AreEqual(
            DatabaseValue.FromBinary([1, 2], "varbinary"),
            DatabaseValue.FromBinary([1, 2, 0], "varbinary")));
    }

    [Fact]
    public void Binary_values_are_copied()
    {
        byte[] bytes = [1, 2, 3];
        var value = DatabaseValue.FromBinary(bytes, "varbinary");
        bytes[0] = 9;

        Assert.Equal(1, value.AsBytes()[0]);
    }

    [Fact]
    public void Date_time_offsets_must_match_instant_and_offset()
    {
        var utc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var sameInstant = utc.ToOffset(TimeSpan.FromHours(2));

        Assert.False(ValueEquality.AreEqual(DatabaseValue.FromDateTimeOffset(utc, "datetimeoffset"), DatabaseValue.FromDateTimeOffset(sameInstant, "datetimeoffset")));
        Assert.True(ValueEquality.AreEqual(DatabaseValue.FromDateTimeOffset(utc, "datetimeoffset"), DatabaseValue.FromDateTimeOffset(utc, "datetimeoffset")));
    }

    [Fact]
    public void Temporal_values_compare_to_the_tick()
    {
        var time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

        Assert.False(ValueEquality.AreEqual(DatabaseValue.FromDateTime(time, "datetime2"), DatabaseValue.FromDateTime(time.AddTicks(1), "datetime2")));
        Assert.False(ValueEquality.AreEqual(DatabaseValue.FromTime(TimeSpan.FromHours(1), "time"), DatabaseValue.FromTime(TimeSpan.FromHours(1).Add(TimeSpan.FromTicks(1)), "time")));
    }

    [Fact]
    public void Values_of_different_kinds_are_never_compared()
    {
        Assert.Throws<ArgumentException>(() => ValueEquality.AreEqual(DatabaseValue.FromInt64(1, "int"), Text("1")));
    }

    [Fact]
    public void Accessors_enforce_the_kind_and_to_string_never_shows_the_value()
    {
        var value = DatabaseValue.FromInt64(42, "int");

        Assert.Equal(42, value.AsInt64());
        Assert.Throws<InvalidOperationException>(() => value.AsString());
        Assert.Equal("int value", value.ToString());
        Assert.Equal("42", value.ToDisplayString());
        Assert.Throws<InvalidOperationException>(() => DatabaseValue.Null(ValueKind.WholeNumber, "int").AsInt64());
    }

    [Fact]
    public void Key_ordering_trims_trailing_spaces_and_orders_text_ordinally()
    {
        var ordering = DefaultKeyOrdering.Instance;

        Assert.Equal(0, ordering.Compare(Text("a"), Text("a  ")));
        Assert.True(ordering.Compare(Text("B"), Text("a")) < 0);
        Assert.True(ordering.Compare(Dec(15, 1), Dec(200, 2)) < 0);
        Assert.True(ordering.Compare(DatabaseValue.FromBinary([0x01], "binary"), DatabaseValue.FromBinary([0x01, 0x00], "binary")) < 0);
        Assert.Throws<ArgumentException>(() => ordering.Compare(DatabaseValue.Null(ValueKind.Text, "nvarchar"), Text("a")));
    }

    [Fact]
    public void Composite_keys_order_column_by_column()
    {
        var keys = new CompositeKeyComparer(DefaultKeyOrdering.Instance);
        DatabaseValue[] Key(long first, string second) => [DatabaseValue.FromInt64(first, "int"), Text(second)];

        Assert.True(keys.Compare(Key(1, "z"), Key(2, "a")) < 0);
        Assert.True(keys.Compare(Key(2, "a"), Key(2, "b")) < 0);
        Assert.Equal(0, keys.Compare(Key(2, "b"), Key(2, "b")));
    }

    [Fact]
    public void Fingerprints_are_stable_canonical_and_null_aware()
    {
        DatabaseValue[] row = [Dec(150, 2), Text("x"), DatabaseValue.Null(ValueKind.Text, "nvarchar")];

        Assert.Equal(RowFingerprint.Compute(row), RowFingerprint.Compute([Dec(15, 1), Text("x"), DatabaseValue.Null(ValueKind.Text, "nvarchar")]));
        Assert.NotEqual(RowFingerprint.Compute(row), RowFingerprint.Compute([Dec(150, 2), Text("x"), Text("")]));
        Assert.NotEqual(RowFingerprint.Compute([Text("ab"), Text("c")]), RowFingerprint.Compute([Text("a"), Text("bc")]));
        Assert.Matches("^[0-9a-f]{64}$", RowFingerprint.Compute(row));
    }
}
