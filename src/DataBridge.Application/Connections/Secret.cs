namespace DataBridge.Application.Connections;

/// <summary>
/// A password or other secret. <see cref="ToString"/> never returns the value, so a secret that ends up
/// in a log message, exception message or debugger display is redacted. Call <see cref="Reveal"/> only
/// where the plaintext is handed to the credential store or the database driver.
/// </summary>
public sealed class Secret : IEquatable<Secret>
{
    public const string RedactedText = "[redacted]";

    private readonly string _value;

    public Secret(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    /// <summary>Returns the plaintext. Never log, display or persist the result.</summary>
    public string Reveal() => _value;

    public override string ToString() => RedactedText;

    public bool Equals(Secret? other) =>
        other is not null && string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as Secret);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_value);
}
