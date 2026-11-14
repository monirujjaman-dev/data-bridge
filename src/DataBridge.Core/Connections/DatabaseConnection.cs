using DataBridge.Shared.Errors;
using DataBridge.Shared.Results;

namespace DataBridge.Core.Connections;

/// <summary>
/// A saved connection profile. Holds no secrets: the password of a SQL authentication profile lives
/// in the OS credential store under <see cref="CredentialReference"/>.
/// </summary>
/// <remarks>
/// Instances are created only through <see cref="Create"/>, which normalizes and validates every
/// field, so an existing instance always satisfies the profile invariants.
/// </remarks>
public sealed record DatabaseConnection
{
    public const int MaxNameLength = 128;
    public const int MaxServerLength = 256;

    /// <summary>The length of a SQL Server <c>sysname</c>.</summary>
    public const int MaxSqlIdentifierLength = 128;

    public const int MaxCredentialReferenceLength = 256;

    private DatabaseConnection(
        Guid id,
        string name,
        DatabaseProviderType provider,
        string server,
        string databaseName,
        AuthenticationType authenticationType,
        string? userName,
        string? credentialReference,
        ConnectionSettings settings,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        Id = id;
        Name = name;
        Provider = provider;
        Server = server;
        DatabaseName = databaseName;
        AuthenticationType = authenticationType;
        UserName = userName;
        CredentialReference = credentialReference;
        Settings = settings;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public Guid Id { get; }

    public string Name { get; }

    public DatabaseProviderType Provider { get; }

    public string Server { get; }

    public string DatabaseName { get; }

    public AuthenticationType AuthenticationType { get; }

    /// <summary>The SQL authentication user name; <see langword="null"/> for integrated authentication. Not a secret.</summary>
    public string? UserName { get; }

    /// <summary>
    /// Key of the password in the OS credential store; never the secret itself.
    /// <see langword="null"/> when no password is saved (integrated authentication, or no secure store).
    /// </summary>
    public string? CredentialReference { get; }

    public ConnectionSettings Settings { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>
    /// Normalizes (trims text, turns blank optional text into <see langword="null"/>) and validates a
    /// profile, returning every validation error at once.
    /// </summary>
    public static Result<DatabaseConnection> Create(
        Guid id,
        string? name,
        DatabaseProviderType provider,
        string? server,
        string? databaseName,
        AuthenticationType authenticationType,
        string? userName,
        string? credentialReference,
        ConnectionSettings? settings,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        var errors = new List<Error>();

        if (id == Guid.Empty)
        {
            errors.Add(ConnectionProfileErrors.IdRequired);
        }

        var normalizedName = NormalizeName(name, errors);

        if (!Enum.IsDefined(provider))
        {
            errors.Add(ConnectionProfileErrors.ProviderNotSupported);
        }

        var normalizedServer = Required(
            server, MaxServerLength, ConnectionProfileErrors.ServerRequired, ConnectionProfileErrors.ServerTooLong, errors);
        var normalizedDatabase = Required(
            databaseName,
            MaxSqlIdentifierLength,
            ConnectionProfileErrors.DatabaseNameRequired,
            ConnectionProfileErrors.DatabaseNameTooLong,
            errors);

        var normalizedUserName = Optional(userName);
        var normalizedReference = Optional(credentialReference);
        ValidateAuthentication(authenticationType, normalizedUserName, normalizedReference, errors);

        var normalizedSettings = NormalizeSettings(settings, errors);

        if (createdAtUtc.Offset != TimeSpan.Zero || updatedAtUtc.Offset != TimeSpan.Zero)
        {
            errors.Add(ConnectionProfileErrors.TimestampNotUtc);
        }
        else if (updatedAtUtc < createdAtUtc)
        {
            errors.Add(ConnectionProfileErrors.UpdatedBeforeCreated);
        }

        if (errors.Count > 0)
        {
            return Result.Failure<DatabaseConnection>(errors);
        }

        return Result.Success(new DatabaseConnection(
            id,
            normalizedName!,
            provider,
            normalizedServer!,
            normalizedDatabase!,
            authenticationType,
            normalizedUserName,
            normalizedReference,
            normalizedSettings!,
            createdAtUtc,
            updatedAtUtc));
    }

    /// <summary>Returns a copy with a new name and <see cref="UpdatedAtUtc"/>.</summary>
    public Result<DatabaseConnection> Rename(string? newName, DateTimeOffset updatedAtUtc) =>
        Create(
            Id,
            newName,
            Provider,
            Server,
            DatabaseName,
            AuthenticationType,
            UserName,
            CredentialReference,
            Settings,
            CreatedAtUtc,
            updatedAtUtc);

    /// <summary>Returns a copy that points at a different credential store entry (or none).</summary>
    public Result<DatabaseConnection> WithCredentialReference(string? credentialReference, DateTimeOffset updatedAtUtc) =>
        Create(
            Id,
            Name,
            Provider,
            Server,
            DatabaseName,
            AuthenticationType,
            UserName,
            credentialReference,
            Settings,
            CreatedAtUtc,
            updatedAtUtc);

    private static string? NormalizeName(string? name, List<Error> errors)
    {
        var normalized = Required(
            name, MaxNameLength, ConnectionProfileErrors.NameRequired, ConnectionProfileErrors.NameTooLong, errors);
        if (normalized is not null && normalized.Any(char.IsControl))
        {
            errors.Add(ConnectionProfileErrors.NameInvalidCharacters);
        }

        return normalized;
    }

    private static void ValidateAuthentication(
        AuthenticationType authenticationType,
        string? userName,
        string? credentialReference,
        List<Error> errors)
    {
        switch (authenticationType)
        {
            case AuthenticationType.SqlAuthentication:
                if (userName is null)
                {
                    errors.Add(ConnectionProfileErrors.UserNameRequired);
                }
                else if (userName.Length > MaxSqlIdentifierLength)
                {
                    errors.Add(ConnectionProfileErrors.UserNameTooLong);
                }

                if (credentialReference is { Length: > MaxCredentialReferenceLength })
                {
                    errors.Add(ConnectionProfileErrors.CredentialReferenceTooLong);
                }

                break;

            case AuthenticationType.Integrated:
                if (userName is not null)
                {
                    errors.Add(ConnectionProfileErrors.UserNameNotAllowed);
                }

                if (credentialReference is not null)
                {
                    errors.Add(ConnectionProfileErrors.CredentialReferenceNotAllowed);
                }

                break;

            default:
                errors.Add(ConnectionProfileErrors.AuthenticationTypeNotSupported);
                break;
        }
    }

    private static ConnectionSettings? NormalizeSettings(ConnectionSettings? settings, List<Error> errors)
    {
        if (settings is null)
        {
            errors.Add(ConnectionProfileErrors.SettingsRequired);
            return null;
        }

        if (settings.ConnectTimeoutSeconds is < ConnectionSettings.MinConnectTimeoutSeconds
            or > ConnectionSettings.MaxConnectTimeoutSeconds)
        {
            errors.Add(ConnectionProfileErrors.ConnectTimeoutOutOfRange);
        }

        var applicationName = Optional(settings.ApplicationName);
        if (applicationName is { Length: > MaxSqlIdentifierLength })
        {
            errors.Add(ConnectionProfileErrors.ApplicationNameTooLong);
        }

        return settings with { ApplicationName = applicationName };
    }

    private static string? Required(string? value, int maxLength, Error missing, Error tooLong, List<Error> errors)
    {
        var normalized = Optional(value);
        if (normalized is null)
        {
            errors.Add(missing);
        }
        else if (normalized.Length > maxLength)
        {
            errors.Add(tooLong);
        }

        return normalized;
    }

    private static string? Optional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
