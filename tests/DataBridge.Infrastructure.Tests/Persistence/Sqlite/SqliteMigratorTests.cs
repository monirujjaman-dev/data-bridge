using DataBridge.Infrastructure.Persistence.Sqlite;

namespace DataBridge.Infrastructure.Tests.Persistence.Sqlite;

public sealed class SqliteMigratorTests : IDisposable
{
    private readonly TemporaryDatabase _temporary = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temporary.Dispose();

    [Fact]
    public async Task Creates_the_file_and_applies_every_migration()
    {
        var version = await new SqliteMigrator(_temporary.Database).MigrateAsync(Token);

        Assert.Equal(SqliteMigrator.LatestVersion, version);
        Assert.True(File.Exists(_temporary.Database.DatabasePath));
        Assert.Equal(SqliteMigrator.LatestVersion, await ScalarAsync("PRAGMA user_version;"));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'connection_profiles';"));
    }

    [Fact]
    public async Task Running_again_is_a_no_op()
    {
        var migrator = new SqliteMigrator(_temporary.Database);
        await migrator.MigrateAsync(Token);

        var version = await migrator.MigrateAsync(Token);

        Assert.Equal(SqliteMigrator.LatestVersion, version);
    }

    [Fact]
    public async Task Refuses_a_database_from_a_newer_version()
    {
        await new SqliteMigrator(_temporary.Database).MigrateAsync(Token);
        await ExecuteAsync($"PRAGMA user_version = {SqliteMigrator.LatestVersion + 1};");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteMigrator(_temporary.Database).MigrateAsync(Token));

        Assert.Contains("Update DataBridge", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Migrations_are_numbered_sequentially_from_one() =>
        Assert.Equal(
            Enumerable.Range(1, SqliteMigrations.All.Count),
            SqliteMigrations.All.Select(migration => migration.Version));

    [Fact]
    public async Task Connection_profiles_table_has_no_column_for_a_secret()
    {
        await new SqliteMigrator(_temporary.Database).MigrateAsync(Token);

        await using var connection = await _temporary.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('connection_profiles');";
        var columns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Token))
        {
            while (await reader.ReadAsync(Token))
            {
                columns.Add(reader.GetString(0));
            }
        }

        Assert.Contains("credential_reference", columns);
        Assert.DoesNotContain(columns, column =>
            column.Contains("password", StringComparison.OrdinalIgnoreCase)
            || column.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cancellation_is_honored()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SqliteMigrator(_temporary.Database).MigrateAsync(cancellation.Token));
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = await _temporary.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _temporary.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }
}
