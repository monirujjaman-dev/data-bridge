using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DataBridge.Core.Connections;

/// <summary>
/// What the server reports about itself after connecting, as opposed to what a profile says. Drives the
/// same-database warning, the route sign in the UI and write-target revalidation (FR-CON-008).
/// </summary>
/// <remarks>Create instances with <see cref="Create"/>, which computes <see cref="IdentityFingerprint"/>.</remarks>
public sealed record ResolvedDatabaseIdentity
{
    private ResolvedDatabaseIdentity(
        DatabaseProviderType provider,
        string serverName,
        string databaseName,
        int? databaseId,
        Guid? databaseGuid,
        DateTime? databaseCreateDate,
        string serverVersion,
        string? databaseCollation,
        string identityFingerprint)
    {
        Provider = provider;
        ServerName = serverName;
        DatabaseName = databaseName;
        DatabaseId = databaseId;
        DatabaseGuid = databaseGuid;
        DatabaseCreateDate = databaseCreateDate;
        ServerVersion = serverVersion;
        DatabaseCollation = databaseCollation;
        IdentityFingerprint = identityFingerprint;
    }

    public DatabaseProviderType Provider { get; }

    /// <summary>The server name as the server reports it (for SQL Server, <c>SERVERPROPERTY('ServerName')</c>).</summary>
    public string ServerName { get; }

    public string DatabaseName { get; }

    public int? DatabaseId { get; }

    /// <summary>
    /// A GUID the engine assigns to the database (for SQL Server, <c>service_broker_guid</c>). Distinguishes
    /// databases that share a server name, name and id, for example on cloned machines.
    /// </summary>
    public Guid? DatabaseGuid { get; }

    /// <summary>
    /// When the database was created or last restored, exactly as the server reports it (for SQL Server,
    /// <c>sys.databases.create_date</c>, in the server's local time). An identifying value, not a UTC timestamp.
    /// </summary>
    public DateTime? DatabaseCreateDate { get; }

    /// <summary>A human-readable product, version and edition, for example "SQL Server 2022 (16.0.4135.4) Developer Edition".</summary>
    public string ServerVersion { get; }

    public string? DatabaseCollation { get; }

    /// <summary>
    /// A stable hash of the identifying fields: provider, server name, database id, name, GUID and create date.
    /// Different fingerprints mean different databases. Equal fingerprints make it very likely, not certain,
    /// that both identities are the same database (domain-model §11: a fingerprint is not proof); it says
    /// nothing about contents.
    /// </summary>
    public string IdentityFingerprint { get; }

    /// <summary>Builds an identity and computes its fingerprint.</summary>
    public static ResolvedDatabaseIdentity Create(
        DatabaseProviderType provider,
        string serverName,
        string databaseName,
        int? databaseId,
        string serverVersion,
        string? databaseCollation,
        Guid? databaseGuid = null,
        DateTime? databaseCreateDate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverVersion);

        return new ResolvedDatabaseIdentity(
            provider,
            serverName,
            databaseName,
            databaseId,
            databaseGuid,
            databaseCreateDate,
            serverVersion,
            databaseCollation,
            ComputeFingerprint(provider, serverName, databaseName, databaseId, databaseGuid, databaseCreateDate));
    }

    /// <summary>
    /// Whether both identities have equal fingerprints, that is, very likely resolve to the same database.
    /// Used for the same-database warning and write-target revalidation, which must treat a match as "same".
    /// </summary>
    public bool IsSameDatabaseAs(ResolvedDatabaseIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(IdentityFingerprint, other.IdentityFingerprint, StringComparison.Ordinal);
    }

    /// <summary>
    /// SHA-256 over typed, length-prefixed identifying fields, so field boundaries and missing values are
    /// unambiguous. Server names compare case-insensitively (host names do), so the server name is upper-cased
    /// invariantly.
    /// </summary>
    private static string ComputeFingerprint(
        DatabaseProviderType provider,
        string serverName,
        string databaseName,
        int? databaseId,
        Guid? databaseGuid,
        DateTime? databaseCreateDate)
    {
        var builder = new StringBuilder();
        Append(builder, "provider", provider.ToString());
        Append(builder, "server", serverName.ToUpperInvariant());
        Append(builder, "id", databaseId?.ToString(CultureInfo.InvariantCulture));
        Append(builder, "name", databaseName);
        Append(builder, "guid", databaseGuid?.ToString("D"));
        Append(builder, "created", databaseCreateDate?.Ticks.ToString(CultureInfo.InvariantCulture));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);

        // name=length:value; or name=null; for a missing value, so NULL never equals an empty string.
        static void Append(StringBuilder builder, string name, string? value)
        {
            builder.Append(name).Append('=');
            if (value is null)
            {
                builder.Append("null;");
                return;
            }

            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
        }
    }
}
