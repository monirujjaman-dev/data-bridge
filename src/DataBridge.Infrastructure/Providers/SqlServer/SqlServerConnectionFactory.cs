using DataBridge.Application.Abstractions;
using DataBridge.Core.Connections;
using Microsoft.Data.SqlClient;

namespace DataBridge.Infrastructure.Providers.SqlServer;

/// <summary>
/// Builds SqlClient connections from a profile. The connection string holds the password, so it is never
/// logged, returned or put in an exception message.
/// </summary>
internal static class SqlServerConnectionFactory
{
    public const string DefaultApplicationName = "DataBridge";

    /// <summary>The connection string builder for a request. Exposed for tests; callers use <see cref="Create"/>.</summary>
    public static SqlConnectionStringBuilder BuildConnectionString(DatabaseConnectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = request.Profile;
        var settings = profile.Settings;

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = profile.Server,
            InitialCatalog = profile.DatabaseName,

            // Mandatory encryption validates the server certificate unless the profile explicitly trusts it
            // (FR-CON-004). Optional sends data in clear text after login; the UI warns about it.
            Encrypt = settings.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = settings.TrustServerCertificate,
            ConnectTimeout = settings.ConnectTimeoutSeconds,
            ApplicationName = settings.ApplicationName ?? DefaultApplicationName,

            // Failed connection attempts must fail fast with their real error, not retry into a timeout.
            ConnectRetryCount = 0,

            // A test must really log in: a pooled, already-authenticated session would hide a disabled login
            // or revoked access, and pooling would keep sessions alive after a cancelled attempt.
            Pooling = false,
            MultipleActiveResultSets = false,
            PersistSecurityInfo = false,
        };

        switch (profile.AuthenticationType)
        {
            case AuthenticationType.SqlAuthentication:
                builder.UserID = profile.UserName;
                builder.Password = request.Password?.Reveal()
                    ?? throw new ArgumentException("SQL authentication needs a password.", nameof(request));
                break;

            case AuthenticationType.Integrated:
                builder.IntegratedSecurity = true;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(request), profile.AuthenticationType, "Unsupported authentication type.");
        }

        return builder;
    }

    public static SqlConnection Create(DatabaseConnectionRequest request) =>
        new(BuildConnectionString(request).ConnectionString);
}
