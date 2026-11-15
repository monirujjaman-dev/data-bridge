using DataBridge.Core.Connections;

namespace DataBridge.Application.Connections;

/// <summary>The fields a user enters to create or edit a connection profile.</summary>
/// <param name="Name">Profile name, unique case-insensitively.</param>
/// <param name="Server">Server name or address, optionally with instance and port.</param>
/// <param name="DatabaseName">Database to connect to.</param>
/// <param name="AuthenticationType">How to authenticate.</param>
/// <param name="UserName">SQL authentication user name; must be empty for integrated authentication.</param>
/// <param name="Password">
/// A new password, or <see langword="null"/> to keep the existing one (or have none). Must be
/// <see langword="null"/> for integrated authentication.
/// </param>
/// <param name="Settings">Transport settings.</param>
/// <param name="Environment">The environment the profile points at, as the user labels it.</param>
public sealed record ConnectionProfileInput(
    string? Name,
    string? Server,
    string? DatabaseName,
    AuthenticationType AuthenticationType,
    string? UserName,
    Secret? Password,
    ConnectionSettings Settings,
    EnvironmentLabel Environment = EnvironmentLabel.None);
