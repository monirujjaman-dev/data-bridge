using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DataBridge.Core.Data;

/// <summary>
/// A canonical SHA-256 fingerprint of a row's values. Equal fingerprints make equal values very likely, not certain:
/// a fingerprint is an optimization and a drift check, never proof of equality (domain-model §11).
/// </summary>
/// <remarks>
/// The serialization is unambiguous: each value is written as its kind, a NULL flag, and a length-prefixed canonical
/// encoding (decimals normalized, text as UTF-16, date-times as ticks with their offset), so different value lists
/// never serialize to the same bytes. Values that compare equal under <see cref="ValueEquality"/> serialize
/// identically.
/// </remarks>
public static class RowFingerprint
{
    public static string Compute(IReadOnlyList<DatabaseValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, values.Count);
        hash.AppendData(header);
        foreach (var value in values)
        {
            Append(hash, value);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, DatabaseValue value)
    {
        Span<byte> prefix = stackalloc byte[6];
        prefix[0] = (byte)value.Kind;
        prefix[1] = value.IsNull ? (byte)1 : (byte)0;
        var payload = value.IsNull ? [] : Canonical(value);
        BinaryPrimitives.WriteInt32LittleEndian(prefix[2..], payload.Length);
        hash.AppendData(prefix);
        hash.AppendData(payload);
    }

    private static byte[] Canonical(DatabaseValue value) => value.RawValue switch
    {
        long number => Int64(number),
        DecimalValue number => DecimalBytes(number.Normalize()),
        double number => Int64(BitConverter.DoubleToInt64Bits(number == 0 ? 0d : number)),
        bool flag => [flag ? (byte)1 : (byte)0],
        string text => Encoding.Unicode.GetBytes(text),
        byte[] bytes => bytes,
        Guid guid => guid.ToByteArray(),
        DateOnly date => Int64(date.DayNumber),
        TimeSpan time => Int64(time.Ticks),
        DateTime dateTime => Int64(dateTime.Ticks),
        DateTimeOffset offset => [.. Int64(offset.Ticks), .. Int64(offset.Offset.Ticks)],
        _ => throw new InvalidOperationException("Unexpected value representation."),
    };

    private static byte[] Int64(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] DecimalBytes(DecimalValue value)
    {
        var unscaled = value.Unscaled.ToByteArray();
        var bytes = new byte[4 + unscaled.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value.Scale);
        unscaled.CopyTo(bytes, 4);
        return bytes;
    }
}
