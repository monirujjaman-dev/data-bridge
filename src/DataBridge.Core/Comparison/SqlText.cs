using System.Globalization;
using System.Numerics;

namespace DataBridge.Core.Comparison;

/// <summary>Culture-independent display text for metadata values.</summary>
internal static class SqlText
{
    public static string Integer(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);
}
