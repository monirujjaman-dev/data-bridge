using System.Data.Common;
using System.Globalization;
using DataBridge.Application.Abstractions;
using DataBridge.Core.Connections;
using Microsoft.Data.Sqlite;

namespace DataBridge.Infrastructure.Persistence.Sqlite;

/// <summary>
/// Stores connection profile metadata in the local SQLite database. There is no column for a password:
/// profiles carry only a reference into the OS credential store.
/// </summary>
/// <remarks>
/// Names are unique case-insensitively through a <c>COLLATE NOCASE</c> index. SQLite's NOCASE folds only
/// ASCII letters, so "Ä" and "ä" count as different names.
/// </remarks>
public sealed class SqliteConnectionProfileRepository(LocalDatabase database) : IConnectionProfileRepository
{
    /// <summary><c>SQLITE_CONSTRAINT_UNIQUE</c>; the only unique index besides the primary key is on the name.</summary>
    private const int SqliteConstraintUnique = 2067;

    private const string SelectColumns =
        """
        SELECT id, name, provider, server, database_name, authentication_type, user_name, credential_reference,
               encrypt, trust_server_certificate, connect_timeout_seconds, application_name,
               created_at_utc, updated_at_utc, environment
        FROM connection_profiles
        """;

    public async Task<IReadOnlyList<DatabaseConnection>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " ORDER BY name COLLATE NOCASE, id;";

        var profiles = new List<DatabaseConnection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            profiles.Add(ReadProfile(reader));
        }

        return profiles;
    }

    public async Task<DatabaseConnection?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", FormatId(id));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProfile(reader) : null;
    }

    public async Task<bool> NameExistsAsync(string name, Guid? excludingId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1 FROM connection_profiles
                WHERE name = $name COLLATE NOCASE AND ($excludingId IS NULL OR id <> $excludingId));
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$excludingId", excludingId is { } id ? FormatId(id) : DBNull.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) != 0;
    }

    public async Task AddAsync(DatabaseConnection profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO connection_profiles (
                id, name, provider, server, database_name, authentication_type, user_name, credential_reference,
                encrypt, trust_server_certificate, connect_timeout_seconds, application_name,
                created_at_utc, updated_at_utc, environment)
            VALUES (
                $id, $name, $provider, $server, $databaseName, $authenticationType, $userName, $credentialReference,
                $encrypt, $trustServerCertificate, $connectTimeoutSeconds, $applicationName,
                $createdAtUtc, $updatedAtUtc, $environment);
            """;
        AddProfileParameters(command, profile);
        await ExecuteWriteAsync(command, cancellationToken);
    }

    public async Task<bool> UpdateAsync(
        DatabaseConnection profile,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE connection_profiles SET
                name = $name,
                provider = $provider,
                server = $server,
                database_name = $databaseName,
                authentication_type = $authenticationType,
                user_name = $userName,
                credential_reference = $credentialReference,
                encrypt = $encrypt,
                trust_server_certificate = $trustServerCertificate,
                connect_timeout_seconds = $connectTimeoutSeconds,
                application_name = $applicationName,
                updated_at_utc = $updatedAtUtc,
                environment = $environment
            WHERE id = $id AND updated_at_utc = $expectedUpdatedAtUtc;
            """;
        AddProfileParameters(command, profile);
        command.Parameters.AddWithValue("$expectedUpdatedAtUtc", FormatTimestamp(expectedUpdatedAtUtc));
        return await ExecuteWriteAsync(command, cancellationToken) == 1;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM connection_profiles WHERE id = $id;";
        command.Parameters.AddWithValue("$id", FormatId(id));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Runs a write, turning a violation of the unique name index into a typed exception.</summary>
    private static async Task<int> ExecuteWriteAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == SqliteConstraintUnique)
        {
            throw new DuplicateConnectionProfileNameException(
                "Another connection profile already has this name.", exception);
        }
    }

    private static void AddProfileParameters(SqliteCommand command, DatabaseConnection profile)
    {
        var parameters = command.Parameters;
        parameters.AddWithValue("$id", FormatId(profile.Id));
        parameters.AddWithValue("$name", profile.Name);
        parameters.AddWithValue("$provider", profile.Provider.ToString());
        parameters.AddWithValue("$server", profile.Server);
        parameters.AddWithValue("$databaseName", profile.DatabaseName);
        parameters.AddWithValue("$authenticationType", profile.AuthenticationType.ToString());
        parameters.AddWithValue("$userName", (object?)profile.UserName ?? DBNull.Value);
        parameters.AddWithValue("$credentialReference", (object?)profile.CredentialReference ?? DBNull.Value);
        parameters.AddWithValue("$encrypt", profile.Settings.Encrypt ? 1 : 0);
        parameters.AddWithValue("$trustServerCertificate", profile.Settings.TrustServerCertificate ? 1 : 0);
        parameters.AddWithValue("$connectTimeoutSeconds", profile.Settings.ConnectTimeoutSeconds);
        parameters.AddWithValue("$applicationName", (object?)profile.Settings.ApplicationName ?? DBNull.Value);
        parameters.AddWithValue("$createdAtUtc", FormatTimestamp(profile.CreatedAtUtc));
        parameters.AddWithValue("$updatedAtUtc", FormatTimestamp(profile.UpdatedAtUtc));
        parameters.AddWithValue("$environment", profile.Environment.ToString());
    }

    private static DatabaseConnection ReadProfile(DbDataReader reader)
    {
        var idText = reader.GetString(0);
        if (!Guid.TryParseExact(idText, "D", out var id))
        {
            throw Corrupt(idText, "id");
        }

        var settings = new ConnectionSettings(
            Encrypt: reader.GetInt64(8) != 0,
            TrustServerCertificate: reader.GetInt64(9) != 0,
            ConnectTimeoutSeconds: checked((int)reader.GetInt64(10)),
            ApplicationName: NullableString(reader, 11));

        var result = DatabaseConnection.Create(
            id,
            reader.GetString(1),
            ParseEnum<DatabaseProviderType>(reader.GetString(2), idText, "provider"),
            reader.GetString(3),
            reader.GetString(4),
            ParseEnum<AuthenticationType>(reader.GetString(5), idText, "authentication_type"),
            NullableString(reader, 6),
            NullableString(reader, 7),
            settings,
            ParseTimestamp(reader.GetString(12), idText, "created_at_utc"),
            ParseTimestamp(reader.GetString(13), idText, "updated_at_utc"),
            ParseEnum<EnvironmentLabel>(reader.GetString(14), idText, "environment"));

        return result.IsSuccess
            ? result.Value
            : throw Corrupt(idText, string.Join(", ", result.Errors.Select(error => error.Code)));
    }

    private static string? NullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static TEnum ParseEnum<TEnum>(string value, string id, string column)
        where TEnum : struct, Enum =>
        // Enums are stored by name; accept only exact member names, not numbers or comma lists.
        Enum.GetNames<TEnum>().Contains(value, StringComparer.Ordinal)
            ? Enum.Parse<TEnum>(value)
            : throw Corrupt(id, column);

    private static string FormatId(Guid id) => id.ToString("D");

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value, string id, string column) =>
        DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToUniversalTime()
            : throw Corrupt(id, column);

    private static InvalidDataException Corrupt(string id, string detail) =>
        new($"Stored connection profile \"{id}\" is invalid ({detail}). The local database may be damaged.");
}
