using DataBridge.Core.Connections;
using DataBridge.Shared.Errors;
using DataBridge.Shared.Results;

namespace DataBridge.Core.Tests.Connections;

public sealed class DatabaseConnectionTests
{
    private static readonly Guid _id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly DateTimeOffset _created = new(2026, 11, 14, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Create_sql_authentication_profile_normalizes_text()
    {
        var result = Create(name: "  Staging  ", server: " db01,1433 ", database: " Sales ", userName: " app ");

        Assert.True(result.IsSuccess);
        var profile = result.Value;
        Assert.Equal("Staging", profile.Name);
        Assert.Equal("db01,1433", profile.Server);
        Assert.Equal("Sales", profile.DatabaseName);
        Assert.Equal("app", profile.UserName);
        Assert.Equal(DatabaseProviderType.SqlServer, profile.Provider);
    }

    [Fact]
    public void Create_turns_blank_optional_text_into_null()
    {
        var settings = ConnectionSettings.Default with { ApplicationName = "   " };

        var profile = Create(credentialReference: " ", settings: settings).Value;

        Assert.Null(profile.CredentialReference);
        Assert.Null(profile.Settings.ApplicationName);
    }

    [Fact]
    public void Create_reports_every_missing_required_field_at_once()
    {
        var result = Create(name: " ", server: "", database: null, userName: null);

        AssertErrors(
            result,
            ConnectionProfileErrors.NameRequired,
            ConnectionProfileErrors.ServerRequired,
            ConnectionProfileErrors.DatabaseNameRequired,
            ConnectionProfileErrors.UserNameRequired);
    }

    [Fact]
    public void Create_rejects_empty_id() =>
        AssertErrors(Create(id: Guid.Empty), ConnectionProfileErrors.IdRequired);

    [Theory]
    [InlineData(DatabaseConnection.MaxNameLength, true)]
    [InlineData(DatabaseConnection.MaxNameLength + 1, false)]
    public void Create_limits_name_length(int length, bool valid)
    {
        var result = Create(name: new string('n', length));

        Assert.Equal(valid, result.IsSuccess);
        if (!valid)
        {
            AssertErrors(result, ConnectionProfileErrors.NameTooLong);
        }
    }

    [Fact]
    public void Create_rejects_control_characters_in_name() =>
        AssertErrors(Create(name: "Prod\nCopy"), ConnectionProfileErrors.NameInvalidCharacters);

    [Fact]
    public void Create_limits_server_and_database_length() =>
        AssertErrors(
            Create(
                server: new string('s', DatabaseConnection.MaxServerLength + 1),
                database: new string('d', DatabaseConnection.MaxSqlIdentifierLength + 1)),
            ConnectionProfileErrors.ServerTooLong,
            ConnectionProfileErrors.DatabaseNameTooLong);

    [Fact]
    public void Create_integrated_profile_without_user_name_succeeds()
    {
        var profile = Create(authentication: AuthenticationType.Integrated, userName: null, credentialReference: null).Value;

        Assert.Equal(AuthenticationType.Integrated, profile.AuthenticationType);
        Assert.Null(profile.UserName);
        Assert.Null(profile.CredentialReference);
    }

    [Fact]
    public void Create_integrated_profile_rejects_user_name_and_credential_reference() =>
        AssertErrors(
            Create(authentication: AuthenticationType.Integrated, userName: "app", credentialReference: "ref"),
            ConnectionProfileErrors.UserNameNotAllowed,
            ConnectionProfileErrors.CredentialReferenceNotAllowed);

    [Fact]
    public void Create_sql_profile_without_credential_reference_succeeds() =>
        Assert.True(Create(credentialReference: null).IsSuccess);

    [Fact]
    public void Create_rejects_undefined_enum_values() =>
        AssertErrors(
            DatabaseConnection.Create(
                _id,
                "Name",
                (DatabaseProviderType)42,
                "server",
                "db",
                (AuthenticationType)42,
                null,
                null,
                ConnectionSettings.Default,
                _created,
                _created),
            ConnectionProfileErrors.ProviderNotSupported,
            ConnectionProfileErrors.AuthenticationTypeNotSupported);

    [Theory]
    [InlineData(0, false)]
    [InlineData(ConnectionSettings.MinConnectTimeoutSeconds, true)]
    [InlineData(ConnectionSettings.MaxConnectTimeoutSeconds, true)]
    [InlineData(ConnectionSettings.MaxConnectTimeoutSeconds + 1, false)]
    public void Create_limits_connect_timeout(int seconds, bool valid)
    {
        var result = Create(settings: ConnectionSettings.Default with { ConnectTimeoutSeconds = seconds });

        Assert.Equal(valid, result.IsSuccess);
        if (!valid)
        {
            AssertErrors(result, ConnectionProfileErrors.ConnectTimeoutOutOfRange);
        }
    }

    [Fact]
    public void Create_requires_settings() =>
        AssertErrors(Create(settings: null, useDefaultSettings: false), ConnectionProfileErrors.SettingsRequired);

    [Fact]
    public void Create_rejects_non_utc_timestamps()
    {
        var local = new DateTimeOffset(2026, 11, 14, 9, 30, 0, TimeSpan.FromHours(6));

        AssertErrors(Create(created: local, updated: local), ConnectionProfileErrors.TimestampNotUtc);
    }

    [Fact]
    public void Create_rejects_update_before_creation() =>
        AssertErrors(Create(updated: _created.AddSeconds(-1)), ConnectionProfileErrors.UpdatedBeforeCreated);

    [Fact]
    public void Default_settings_encrypt_and_validate_the_certificate()
    {
        Assert.True(ConnectionSettings.Default.Encrypt);
        Assert.False(ConnectionSettings.Default.TrustServerCertificate);
        Assert.Equal(ConnectionSettings.DefaultConnectTimeoutSeconds, ConnectionSettings.Default.ConnectTimeoutSeconds);
    }

    [Fact]
    public void Rename_changes_only_name_and_update_time()
    {
        var original = Create().Value;
        var later = _created.AddMinutes(5);

        var renamed = original.Rename(" Production copy ", later).Value;

        Assert.Equal("Production copy", renamed.Name);
        Assert.Equal(later, renamed.UpdatedAtUtc);
        Assert.Equal(original.Server, renamed.Server);
        Assert.Equal(original.CreatedAtUtc, renamed.CreatedAtUtc);
        Assert.Equal(original.CredentialReference, renamed.CredentialReference);
    }

    [Fact]
    public void Rename_validates_the_new_name() =>
        AssertErrors(Create().Value.Rename("", _created), ConnectionProfileErrors.NameRequired);

    [Fact]
    public void WithCredentialReference_sets_and_clears_the_reference()
    {
        var profile = Create(credentialReference: null).Value;

        var withReference = profile.WithCredentialReference("DataBridge/x", _created).Value;
        var cleared = withReference.WithCredentialReference(null, _created).Value;

        Assert.Equal("DataBridge/x", withReference.CredentialReference);
        Assert.Null(cleared.CredentialReference);
    }

    [Fact]
    public void Profiles_with_equal_fields_are_equal() =>
        Assert.Equal(Create().Value, Create().Value);

    private static Result<DatabaseConnection> Create(
        Guid? id = null,
        string? name = "Staging",
        string? server = "db01",
        string? database = "Sales",
        AuthenticationType authentication = AuthenticationType.SqlAuthentication,
        string? userName = "app",
        string? credentialReference = "DataBridge/connection-profile/x",
        ConnectionSettings? settings = null,
        bool useDefaultSettings = true,
        DateTimeOffset? created = null,
        DateTimeOffset? updated = null) =>
        DatabaseConnection.Create(
            id ?? _id,
            name,
            DatabaseProviderType.SqlServer,
            server,
            database,
            authentication,
            userName,
            credentialReference,
            settings ?? (useDefaultSettings ? ConnectionSettings.Default : null),
            created ?? _created,
            updated ?? created ?? _created);

    private static void AssertErrors(Result result, params Error[] expected)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(expected.Select(error => error.Code).Order(), result.Errors.Select(error => error.Code).Order());
    }
}
