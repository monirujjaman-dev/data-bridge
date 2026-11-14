namespace DataBridge.Core.Connections;

/// <summary>Transport settings of a connection profile.</summary>
/// <param name="Encrypt">Encrypt the connection. Defaults to <see langword="true"/>.</param>
/// <param name="TrustServerCertificate">
/// Skip server certificate validation. Defaults to <see langword="false"/>; an explicit per-profile opt-in.
/// </param>
/// <param name="ConnectTimeoutSeconds">
/// Seconds to wait for a connection, from <see cref="MinConnectTimeoutSeconds"/> to <see cref="MaxConnectTimeoutSeconds"/>.
/// </param>
/// <param name="ApplicationName">Optional application name reported to the server.</param>
public sealed record ConnectionSettings(
    bool Encrypt,
    bool TrustServerCertificate,
    int ConnectTimeoutSeconds,
    string? ApplicationName)
{
    public const int MinConnectTimeoutSeconds = 1;
    public const int MaxConnectTimeoutSeconds = 600;
    public const int DefaultConnectTimeoutSeconds = 15;

    /// <summary>Encrypted, certificate validated, 15-second timeout, no application name.</summary>
    public static ConnectionSettings Default { get; } =
        new(Encrypt: true, TrustServerCertificate: false, DefaultConnectTimeoutSeconds, ApplicationName: null);
}
