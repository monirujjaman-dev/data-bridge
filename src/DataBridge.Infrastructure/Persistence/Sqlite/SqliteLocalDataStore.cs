using DataBridge.Application.Abstractions;

namespace DataBridge.Infrastructure.Persistence.Sqlite;

/// <summary>Prepares the local SQLite database by applying pending migrations.</summary>
internal sealed class SqliteLocalDataStore(SqliteMigrator migrator) : ILocalDataStore
{
    public Task InitializeAsync(CancellationToken cancellationToken) => migrator.MigrateAsync(cancellationToken);
}
