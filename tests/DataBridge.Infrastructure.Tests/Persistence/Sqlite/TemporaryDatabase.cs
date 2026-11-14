using DataBridge.Infrastructure.Persistence.Sqlite;

namespace DataBridge.Infrastructure.Tests.Persistence.Sqlite;

/// <summary>A SQLite file in a fresh temporary folder, deleted when the test ends.</summary>
internal sealed class TemporaryDatabase : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "DataBridge.Tests", Guid.NewGuid().ToString("N"));

    public TemporaryDatabase()
    {
        // A nested, not-yet-existing folder also checks that the folder is created on first open.
        Database = new LocalDatabase(Path.Combine(_directory, "nested", "databridge.db"));
    }

    public LocalDatabase Database { get; }

    public async Task<LocalDatabase> MigratedAsync()
    {
        await new SqliteMigrator(Database).MigrateAsync(TestContext.Current.CancellationToken);
        return Database;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
