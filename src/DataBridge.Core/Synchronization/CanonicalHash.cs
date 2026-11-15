using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DataBridge.Core.Synchronization;

/// <summary>
/// Builds an unambiguous SHA-256 over named, typed, length-prefixed fields: different field sequences never produce
/// the same bytes, and a missing value never equals an empty one.
/// </summary>
internal sealed class CanonicalHash : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public CanonicalHash Add(string name, string? value)
    {
        var text = value is null
            ? $"{name}=null;"
            : string.Create(CultureInfo.InvariantCulture, $"{name}={value.Length}:{value};");
        _hash.AppendData(Encoding.UTF8.GetBytes(text));
        return this;
    }

    public CanonicalHash Add(string name, long value) => Add(name, value.ToString(CultureInfo.InvariantCulture));

    public CanonicalHash Add(string name, long? value) => Add(name, value?.ToString(CultureInfo.InvariantCulture));

    public CanonicalHash Add(string name, bool value) => Add(name, value ? "1" : "0");

    public CanonicalHash Add(string name, Guid value) => Add(name, value.ToString("D"));

    public CanonicalHash Add(string name, DateTimeOffset value) =>
        Add(name, value.UtcTicks.ToString(CultureInfo.InvariantCulture));

    public CanonicalHash Add<TEnum>(string name, TEnum value)
        where TEnum : struct, Enum => Add(name, value.ToString());

    /// <summary>Starts a list: the count is hashed, so items cannot move between adjacent lists.</summary>
    public CanonicalHash List(string name, int count) => Add(name + "[]", count);

    public string Finish() => Convert.ToHexStringLower(_hash.GetHashAndReset());

    public void Dispose() => _hash.Dispose();
}
