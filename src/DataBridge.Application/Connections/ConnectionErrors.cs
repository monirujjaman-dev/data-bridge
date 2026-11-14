using DataBridge.Shared.Errors;

namespace DataBridge.Application.Connections;

/// <summary>
/// Provider-independent connection failures. Messages say what happened and what to check; they never
/// contain passwords or connection strings.
/// </summary>
public static class ConnectionErrors
{
    public static readonly Error PasswordRequired =
        new("Connection.PasswordRequired", "Enter the password: none is saved for this profile.");

    public static readonly Error LoginFailed =
        new("Connection.LoginFailed", "The server rejected the login. Check the user name and password, and that the login is enabled.");

    public static readonly Error PasswordExpired =
        new("Connection.PasswordExpired", "The login's password has expired or must be changed. Change it on the server, then try again.");

    public static readonly Error IntegratedAuthenticationFailed =
        new(
            "Connection.IntegratedAuthenticationFailed",
            "Integrated authentication failed. Check that your account has a login on the server; on Linux and macOS, check your Kerberos configuration.");

    public static readonly Error DatabaseUnavailable =
        new("Connection.DatabaseUnavailable", "The database does not exist, is offline, or the login cannot open it. Check the database name and the login's permissions.");

    public static readonly Error ServerUnreachable =
        new("Connection.ServerUnreachable", "The server could not be reached. Check the server name, instance and port, and that a firewall allows the connection.");

    public static readonly Error CertificateNotTrusted =
        new(
            "Connection.CertificateNotTrusted",
            "The server's certificate is not trusted, so the connection was refused. Install a trusted certificate on the server, or, only for a server you control, choose to trust its certificate in this profile.");

    public static readonly Error EncryptionFailed =
        new("Connection.EncryptionFailed", "An encrypted connection could not be established with the server. Check that the server supports encryption (TLS 1.2 or later).");

    public static Error Timeout(int seconds) =>
        new("Connection.Timeout", $"The server did not respond within {seconds} seconds. Check the server name and network, or increase the connection timeout.");

    public static Error UnsupportedServerVersion(string found) =>
        new("Connection.UnsupportedServerVersion", $"{found} is not supported. DataBridge supports SQL Server 2017 and later.");

    public static Error UnsupportedEngine(string found) =>
        new(
            "Connection.UnsupportedEngine",
            $"{found} is not supported in this version of DataBridge. It supports SQL Server 2017 and later on Windows or Linux; Azure SQL is planned for a later version.");

    /// <summary>A server error with no more specific mapping; the server's message is safe to show.</summary>
    public static Error ServerError(int number, string message) =>
        new("Connection.ServerError", $"The server returned error {number}: {message}");
}
