using Microsoft.Data.Sqlite;

namespace DataBridge.Infrastructure.Persistence.Sqlite;

/// <summary>
/// The local SQLite database file that holds application metadata (never secrets). Opens connections
/// with foreign keys enforced.
/// </summary>
public sealed class LocalDatabase
{
    private readonly string _connectionString;

    public LocalDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,

            // Opening a local file is cheap, and without pooling the file is released as soon as a
            // connection is disposed, so it can be moved, backed up or deleted.
            Pooling = false,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>
    /// The default location: <c>DataBridge/databridge.db</c> under the user's local application data folder.
    /// </summary>
    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "DataBridge",
            "databridge.db");

    /// <summary>Opens a connection, creating the file and its folder if they do not exist.</summary>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
