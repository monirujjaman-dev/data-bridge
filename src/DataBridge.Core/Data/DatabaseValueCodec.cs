using System.Numerics;
using System.Text;

namespace DataBridge.Core.Data;

/// <summary>
/// A reversible binary encoding of <see cref="DatabaseValue"/>s, used to store the keys of the rows a synchronization
/// plan changes (ADR-0016). Each value is written as its kind, SQL type name, a NULL flag and a typed payload, so
/// reading it back gives a value equal to the original under <see cref="ValueEquality"/>.
/// </summary>
public static class DatabaseValueCodec
{
    public static void Write(BinaryWriter writer, DatabaseValue value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.Write((byte)value.Kind);
        writer.Write(value.SqlTypeName);
        writer.Write(value.IsNull);
        if (value.IsNull)
        {
            return;
        }

        switch (value.RawValue)
        {
            case long number:
                writer.Write(number);
                break;
            case DecimalValue number:
                var unscaled = number.Unscaled.ToByteArray();
                writer.Write(number.Scale);
                writer.Write(unscaled.Length);
                writer.Write(unscaled);
                break;
            case double number:
                writer.Write(number);
                break;
            case bool flag:
                writer.Write(flag);
                break;
            case string text:
                var bytes = Encoding.UTF8.GetBytes(text);
                writer.Write(bytes.Length);
                writer.Write(bytes);
                break;
            case byte[] binary:
                writer.Write(binary.Length);
                writer.Write(binary);
                break;
            case Guid guid:
                writer.Write(guid.ToByteArray());
                break;
            case DateOnly date:
                writer.Write(date.DayNumber);
                break;
            case TimeSpan time:
                writer.Write(time.Ticks);
                break;
            case DateTime dateTime:
                writer.Write(dateTime.Ticks);
                writer.Write((byte)dateTime.Kind);
                break;
            case DateTimeOffset offset:
                writer.Write(offset.Ticks);
                writer.Write(offset.Offset.Ticks);
                break;
            default:
                throw new InvalidOperationException("Unexpected value representation.");
        }
    }

    /// <exception cref="InvalidDataException">The bytes are not a value written by <see cref="Write"/>.</exception>
    public static DatabaseValue Read(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var kind = (ValueKind)reader.ReadByte();
        var typeName = reader.ReadString();
        if (reader.ReadBoolean())
        {
            return DatabaseValue.Null(kind, typeName);
        }

        return kind switch
        {
            ValueKind.WholeNumber => DatabaseValue.FromInt64(reader.ReadInt64(), typeName),
            ValueKind.ExactNumeric => DatabaseValue.FromDecimal(ReadDecimal(reader), typeName),
            ValueKind.ApproximateNumeric => DatabaseValue.FromDouble(reader.ReadDouble(), typeName),
            ValueKind.Boolean => DatabaseValue.FromBoolean(reader.ReadBoolean(), typeName),
            ValueKind.Text => DatabaseValue.FromText(Encoding.UTF8.GetString(ReadBytes(reader)), typeName),
            ValueKind.Xml => DatabaseValue.FromXml(Encoding.UTF8.GetString(ReadBytes(reader)), typeName),
            ValueKind.Binary or ValueKind.RowVersion => DatabaseValue.FromBinary(ReadBytes(reader), typeName),
            ValueKind.UniqueIdentifier => DatabaseValue.FromGuid(new Guid(reader.ReadBytes(16)), typeName),
            ValueKind.Date => DatabaseValue.FromDate(DateOnly.FromDayNumber(reader.ReadInt32()), typeName),
            ValueKind.Time => DatabaseValue.FromTime(new TimeSpan(reader.ReadInt64()), typeName),
            ValueKind.DateTime => DatabaseValue.FromDateTime(new DateTime(reader.ReadInt64(), (DateTimeKind)reader.ReadByte()), typeName),
            ValueKind.DateTimeOffset => ReadDateTimeOffset(reader, typeName),
            _ => throw new InvalidDataException("The stored value has an unknown kind."),
        };
    }

    private static DecimalValue ReadDecimal(BinaryReader reader)
    {
        var scale = reader.ReadInt32();
        return new DecimalValue(new BigInteger(ReadBytes(reader)), scale);
    }

    private static DatabaseValue ReadDateTimeOffset(BinaryReader reader, string typeName)
    {
        var ticks = reader.ReadInt64();
        var offset = new TimeSpan(reader.ReadInt64());
        return DatabaseValue.FromDateTimeOffset(new DateTimeOffset(ticks, offset), typeName);
    }

    private static byte[] ReadBytes(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > reader.BaseStream.Length)
        {
            throw new InvalidDataException("The stored value has an invalid length.");
        }

        var bytes = reader.ReadBytes(length);
        return bytes.Length == length ? bytes : throw new InvalidDataException("The stored value is truncated.");
    }
}
