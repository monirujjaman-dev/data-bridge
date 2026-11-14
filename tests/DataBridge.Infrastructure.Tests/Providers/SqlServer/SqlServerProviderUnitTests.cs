using System.Security.Authentication;
using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;
using DataBridge.Core.Connections;
using DataBridge.Infrastructure.Providers.SqlServer;
using Microsoft.Data.SqlClient;

namespace DataBridge.Infrastructure.Tests.Providers.SqlServer;

public sealed class SqlServerConnectionFactoryTests
{
    [Fact]
    public void Defaults_encrypt_validate_the_certificate_and_do_not_retry()
    {
        var builder = SqlServerConnectionFactory.BuildConnectionString(SqlRequest());

        Assert.Equal("db01,1433", builder.DataSource);
        Assert.Equal("Sales", builder.InitialCatalog);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.False(builder.TrustServerCertificate);
        Assert.Equal(ConnectionSettings.DefaultConnectTimeoutSeconds, builder.ConnectTimeout);
        Assert.Equal(SqlServerConnectionFactory.DefaultApplicationName, builder.ApplicationName);
        Assert.Equal(0, builder.ConnectRetryCount);
        Assert.False(builder.Pooling);
        Assert.False(builder.PersistSecurityInfo);
        Assert.False(builder.IntegratedSecurity);
        Assert.Equal("app", builder.UserID);
        Assert.Equal("s3cr3t!", builder.Password);
    }

    [Fact]
    public void Profile_settings_are_applied()
    {
        var settings = new ConnectionSettings(Encrypt: false, TrustServerCertificate: true, ConnectTimeoutSeconds: 42, ApplicationName: "Nightly check");

        var builder = SqlServerConnectionFactory.BuildConnectionString(SqlRequest(settings));

        Assert.Equal(SqlConnectionEncryptOption.Optional, builder.Encrypt);
        Assert.True(builder.TrustServerCertificate);
        Assert.Equal(42, builder.ConnectTimeout);
        Assert.Equal("Nightly check", builder.ApplicationName);
    }

    [Fact]
    public void Integrated_authentication_uses_no_user_name_or_password()
    {
        var profile = Profile(AuthenticationType.Integrated, userName: null);

        var builder = SqlServerConnectionFactory.BuildConnectionString(new DatabaseConnectionRequest(profile, null));

        Assert.True(builder.IntegratedSecurity);
        Assert.Equal("", builder.UserID);
        Assert.Equal("", builder.Password);
    }

    [Fact]
    public void Sql_authentication_without_a_password_is_rejected() =>
        Assert.Throws<ArgumentException>(() =>
            SqlServerConnectionFactory.BuildConnectionString(new DatabaseConnectionRequest(Profile(), null)));

    [Fact]
    public void Server_and_database_names_cannot_inject_connection_string_keywords()
    {
        var profile = Profile(server: "db01;Password=injected", database: "Sales;Integrated Security=true");

        var builder = new SqlConnectionStringBuilder(
            SqlServerConnectionFactory.BuildConnectionString(new DatabaseConnectionRequest(profile, new Secret("s3cr3t!"))).ConnectionString);

        Assert.Equal("db01;Password=injected", builder.DataSource);
        Assert.Equal("Sales;Integrated Security=true", builder.InitialCatalog);
        Assert.False(builder.IntegratedSecurity);
        Assert.Equal("s3cr3t!", builder.Password);
    }

    private static DatabaseConnectionRequest SqlRequest(ConnectionSettings? settings = null) =>
        new(Profile(settings: settings), new Secret("s3cr3t!"));

    internal static DatabaseConnection Profile(
        AuthenticationType authentication = AuthenticationType.SqlAuthentication,
        string? userName = "app",
        ConnectionSettings? settings = null,
        string server = "db01,1433",
        string database = "Sales") =>
        DatabaseConnection.Create(
            Guid.NewGuid(),
            "Test",
            DatabaseProviderType.SqlServer,
            server,
            database,
            authentication,
            userName,
            null,
            settings ?? ConnectionSettings.Default,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch).Value;
}

public sealed class SqlServerServerInfoTests
{
    [Theory]
    [InlineData(14, 2, "SQL Server 2017")]
    [InlineData(15, 4, "SQL Server 2019")]
    [InlineData(16, 3, "SQL Server 2022")]
    [InlineData(17, 1, "SQL Server 2025")]
    public void Supported_versions_and_editions_pass(int major, int engineEdition, string product)
    {
        var info = Info(major, engineEdition);

        Assert.Null(info.CheckSupported());
        Assert.Equal(product, info.ProductName);
        Assert.Equal($"{product} ({major}.0.1000.1) Developer Edition (64-bit)", info.DisplayVersion);
    }

    [Theory]
    [InlineData(13, "SQL Server 2016")]
    [InlineData(11, "SQL Server 2012")]
    public void Versions_before_2017_are_rejected(int major, string product)
    {
        var error = Info(major, engineEdition: 3).CheckSupported();

        Assert.Equal("Connection.UnsupportedServerVersion", error!.Code);
        Assert.Contains(product, error.Message, StringComparison.Ordinal);
        Assert.Contains("SQL Server 2017 and later", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5, "Azure SQL Database")]
    [InlineData(8, "Azure SQL Managed Instance")]
    [InlineData(9, "Azure SQL Edge")]
    [InlineData(6, "Azure Synapse Analytics")]
    [InlineData(99, "engine edition 99")]
    public void Azure_and_unknown_engines_are_rejected(int engineEdition, string expected)
    {
        var error = Info(major: 16, engineEdition).CheckSupported();

        Assert.Equal("Connection.UnsupportedEngine", error!.Code);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(16, "16.0.4135.4", 16)]
    [InlineData(null, "10.50.6000.34", 10)]
    [InlineData(null, "11.0.5058.0", 11)]
    [InlineData(null, null, 0)]
    [InlineData(null, "garbage", 0)]
    public void Major_version_falls_back_to_the_product_version(int? reported, string? productVersion, int expected) =>
        Assert.Equal(expected, SqlServerServerInfo.ParseMajorVersion(reported, productVersion));

    [Fact]
    public void Unreadable_version_is_rejected_as_unsupported() =>
        Assert.Equal("Connection.UnsupportedServerVersion", Info(major: 0, engineEdition: 3).CheckSupported()!.Code);

    private static SqlServerServerInfo Info(int major, int engineEdition) =>
        new("DB01", "Sales", 5, major, $"{major}.0.1000.1", "Developer Edition (64-bit)", engineEdition, "Latin1_General_CI_AS");
}

public sealed class SqlServerErrorTranslatorTests
{
    [Fact]
    public void Tls_failure_about_the_certificate_means_untrusted_certificate()
    {
        var exception = new InvalidOperationException(
            "A connection was successfully established with the server, but then an error occurred during the login process.",
            new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback."));

        Assert.Equal(ConnectionErrors.CertificateNotTrusted, SqlServerErrorTranslator.Translate(exception, SqlServerConnectionFactoryTests.Profile()));
    }

    [Fact]
    public void Other_tls_failures_mean_encryption_failed() =>
        Assert.Equal(
            ConnectionErrors.EncryptionFailed,
            SqlServerErrorTranslator.Translate(
                new InvalidOperationException("Login failed.", new AuthenticationException("Authentication failed because the remote party sent a TLS alert.")),
                SqlServerConnectionFactoryTests.Profile()));

    [Fact]
    public void Unrecognized_exceptions_are_not_translated() =>
        Assert.Null(SqlServerErrorTranslator.Translate(new InvalidOperationException("Bug"), SqlServerConnectionFactoryTests.Profile()));
}
