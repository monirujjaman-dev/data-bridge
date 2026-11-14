using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DataBridge.Infrastructure.Persistence.Sqlite;

/// <summary>
/// Brings the local database schema up to date. The schema version is kept in <c>PRAGMA user_version</c>;
/// each pending migration runs in its own transaction together with the version bump, so a failure
/// leaves the database at the last fully applied version.
/// </summary>
public sealed class SqliteMigrator(LocalDatabase database)
{
    /// <summary>The schema version this build of DataBridge expects.</summary>
    public static int LatestVersion => SqliteMigrations.LatestVersion;

    /// <summary>Applies pending migrations and returns the resulting schema version.</summary>
    /// <exception cref="InvalidOperationException">
    /// The database was written by a newer DataBridge version whose schema this build does not know.
    /// </exception>
    public async Task<int> MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);

        var current = await ReadVersionAsync(connection, transaction: null, cancellationToken);
        if (current > LatestVersion)
        {
            throw new InvalidOperationException(
                $"The local database \"{database.DatabasePath}\" has schema version {current}, but this version "
                + $"of DataBridge supports up to {LatestVersion}. Update DataBridge to open it.");
        }

        foreach (var migration in SqliteMigrations.All.Where(migration => migration.Version > current))
        {
            await ApplyAsync(connection, migration, cancellationToken);
            current = migration.Version;
        }

        return current;
    }

    private static async Task ApplyAsync(
        SqliteConnection connection,
        SqliteMigration migration,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // Re-check inside the transaction in case another process migrated concurrently.
        if (await ReadVersionAsync(connection, transaction, cancellationToken) >= migration.Version)
        {
            return;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = migration.Sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;

            // PRAGMA does not accept parameters; the version is an integer from code, not user input.
            command.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {migration.Version};");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<int> ReadVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }
}
