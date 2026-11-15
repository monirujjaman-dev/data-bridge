using DataBridge.Shared.Errors;

namespace DataBridge.Core.Connections;

/// <summary>Validation errors for <see cref="DatabaseConnection"/>.</summary>
public static class ConnectionProfileErrors
{
    public static readonly Error IdRequired =
        new("ConnectionProfile.IdRequired", "The profile needs an identifier.");

    public static readonly Error NameRequired =
        new("ConnectionProfile.NameRequired", "Enter a profile name.");

    public static readonly Error NameTooLong =
        new("ConnectionProfile.NameTooLong", $"The profile name can be at most {DatabaseConnection.MaxNameLength} characters.");

    public static readonly Error NameInvalidCharacters =
        new("ConnectionProfile.NameInvalidCharacters", "The profile name cannot contain control characters.");

    public static readonly Error NameAlreadyExists =
        new("ConnectionProfile.NameAlreadyExists", "Another profile already has this name.");

    public static readonly Error ProviderNotSupported =
        new("ConnectionProfile.ProviderNotSupported", "This database provider is not supported.");

    public static readonly Error EnvironmentNotSupported =
        new("ConnectionProfile.EnvironmentNotSupported", "This environment label is not supported.");

    public static readonly Error ServerRequired =
        new("ConnectionProfile.ServerRequired", "Enter a server.");

    public static readonly Error ServerTooLong =
        new("ConnectionProfile.ServerTooLong", $"The server can be at most {DatabaseConnection.MaxServerLength} characters.");

    public static readonly Error DatabaseNameRequired =
        new("ConnectionProfile.DatabaseNameRequired", "Enter a database name.");

    public static readonly Error DatabaseNameTooLong =
        new("ConnectionProfile.DatabaseNameTooLong", $"The database name can be at most {DatabaseConnection.MaxSqlIdentifierLength} characters.");

    public static readonly Error AuthenticationTypeNotSupported =
        new("ConnectionProfile.AuthenticationTypeNotSupported", "This authentication type is not supported.");

    public static readonly Error UserNameRequired =
        new("ConnectionProfile.UserNameRequired", "Enter a user name for SQL authentication.");

    public static readonly Error UserNameTooLong =
        new("ConnectionProfile.UserNameTooLong", $"The user name can be at most {DatabaseConnection.MaxSqlIdentifierLength} characters.");

    public static readonly Error UserNameNotAllowed =
        new("ConnectionProfile.UserNameNotAllowed", "Integrated authentication does not use a user name.");

    public static readonly Error CredentialReferenceNotAllowed =
        new("ConnectionProfile.CredentialReferenceNotAllowed", "Integrated authentication does not use a stored credential.");

    public static readonly Error CredentialReferenceTooLong =
        new("ConnectionProfile.CredentialReferenceTooLong", $"The credential reference can be at most {DatabaseConnection.MaxCredentialReferenceLength} characters.");

    public static readonly Error PasswordNotAllowed =
        new("ConnectionProfile.PasswordNotAllowed", "Integrated authentication does not use a password.");

    public static readonly Error SettingsRequired =
        new("ConnectionProfile.SettingsRequired", "The profile needs connection settings.");

    public static readonly Error ConnectTimeoutOutOfRange =
        new(
            "ConnectionProfile.ConnectTimeoutOutOfRange",
            $"The connection timeout must be from {ConnectionSettings.MinConnectTimeoutSeconds} to {ConnectionSettings.MaxConnectTimeoutSeconds} seconds.");

    public static readonly Error ApplicationNameTooLong =
        new("ConnectionProfile.ApplicationNameTooLong", $"The application name can be at most {DatabaseConnection.MaxSqlIdentifierLength} characters.");

    public static readonly Error TimestampNotUtc =
        new("ConnectionProfile.TimestampNotUtc", "Profile timestamps must be in UTC.");

    public static readonly Error UpdatedBeforeCreated =
        new("ConnectionProfile.UpdatedBeforeCreated", "A profile cannot be updated before it was created.");

    public static readonly Error NotFound =
        new("ConnectionProfile.NotFound", "The connection profile no longer exists.");

    public static readonly Error ModifiedConcurrently =
        new("ConnectionProfile.ModifiedConcurrently", "The connection profile was changed elsewhere. Reload it and try again.");
}
