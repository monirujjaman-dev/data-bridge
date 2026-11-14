using DataBridge.Application.Abstractions;
using DataBridge.Core.Connections;
using DataBridge.Infrastructure.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace DataBridge.Infrastructure.Tests.Persistence.Sqlite;

public sealed class SqliteConnectionProfileRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset _created = new(2026, 11, 14, 9, 30, 15, 123, TimeSpan.Zero);

    private readonly TemporaryDatabase _temporary = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temporary.Dispose();

    [Fact]
    public async Task Added_sql_profile_round_trips_every_field()
    {
        var repository = await CreateRepositoryAsync();
        var settings = new ConnectionSettings(
            Encrypt: false, TrustServerCertificate: true, ConnectTimeoutSeconds: 42, ApplicationName: "DataBridge tests");
        var profile = Profile(
            "Staging", settings: settings, credentialReference: "DataBridge/connection-profile/x", updated: _created.AddTicks(7));

        await repository.AddAsync(profile, Token);
        var loaded = await repository.FindAsync(profile.Id, Token);

        Assert.Equal(profile, loaded);
        Assert.Equal(TimeSpan.Zero, loaded!.UpdatedAtUtc.Offset);
    }

    [Fact]
    public async Task Added_integrated_profile_round_trips_null_fields()
    {
        var repository = await CreateRepositoryAsync();
        var profile = Profile("Local", authentication: AuthenticationType.Integrated, userName: null);

        await repository.AddAsync(profile, Token);

        Assert.Equal(profile, await repository.FindAsync(profile.Id, Token));
    }

    [Fact]
    public async Task Find_returns_null_for_unknown_id()
    {
        var repository = await CreateRepositoryAsync();

        Assert.Null(await repository.FindAsync(Guid.NewGuid(), Token));
    }

    [Fact]
    public async Task List_orders_by_name_ignoring_case()
    {
        var repository = await CreateRepositoryAsync();
        await repository.AddAsync(Profile("zeta"), Token);
        await repository.AddAsync(Profile("Alpha"), Token);
        await repository.AddAsync(Profile("beta"), Token);

        var profiles = await repository.ListAsync(Token);

        Assert.Equal(["Alpha", "beta", "zeta"], profiles.Select(profile => profile.Name));
    }

    [Fact]
    public async Task Name_exists_ignores_case_and_the_excluded_profile()
    {
        var repository = await CreateRepositoryAsync();
        var staging = Profile("Staging");
        await repository.AddAsync(staging, Token);

        Assert.True(await repository.NameExistsAsync("STAGING", excludingId: null, Token));
        Assert.False(await repository.NameExistsAsync("staging", staging.Id, Token));
        Assert.False(await repository.NameExistsAsync("Production", excludingId: null, Token));
    }

    [Fact]
    public async Task Add_with_a_name_differing_only_by_case_throws_duplicate_name()
    {
        var repository = await CreateRepositoryAsync();
        await repository.AddAsync(Profile("Staging"), Token);

        var exception = await Assert.ThrowsAsync<DuplicateConnectionProfileNameException>(() =>
            repository.AddAsync(Profile("STAGING"), Token));

        Assert.IsType<SqliteException>(exception.InnerException);
    }

    [Fact]
    public async Task Update_to_a_taken_name_throws_duplicate_name()
    {
        var repository = await CreateRepositoryAsync();
        await repository.AddAsync(Profile("Staging"), Token);
        var other = Profile("Production");
        await repository.AddAsync(other, Token);

        await Assert.ThrowsAsync<DuplicateConnectionProfileNameException>(() =>
            repository.UpdateAsync(other.Rename("staging", _created.AddMinutes(1)).Value, other.UpdatedAtUtc, Token));
    }

    [Fact]
    public async Task Add_with_an_existing_id_is_not_reported_as_a_duplicate_name()
    {
        var repository = await CreateRepositoryAsync();
        var profile = Profile("Staging");
        await repository.AddAsync(profile, Token);

        await Assert.ThrowsAsync<SqliteException>(() =>
            repository.AddAsync(profile.Rename("Other", _created).Value, Token));
    }

    [Fact]
    public async Task Update_replaces_the_stored_profile()
    {
        var repository = await CreateRepositoryAsync();
        var profile = Profile("Staging");
        await repository.AddAsync(profile, Token);
        var renamed = profile.Rename("Staging (old)", _created.AddMinutes(1)).Value;

        var updated = await repository.UpdateAsync(renamed, profile.UpdatedAtUtc, Token);

        Assert.True(updated);
        Assert.Equal(renamed, await repository.FindAsync(profile.Id, Token));
    }

    [Fact]
    public async Task Update_from_a_stale_copy_is_rejected_and_keeps_the_stored_profile()
    {
        var repository = await CreateRepositoryAsync();
        var profile = Profile("Staging");
        await repository.AddAsync(profile, Token);
        var newer = profile.Rename("Newer", _created.AddMinutes(1)).Value;
        await repository.UpdateAsync(newer, profile.UpdatedAtUtc, Token);
        var stale = profile.Rename("Stale", _created.AddMinutes(2)).Value;

        var updated = await repository.UpdateAsync(stale, profile.UpdatedAtUtc, Token);

        Assert.False(updated);
        Assert.Equal(newer, await repository.FindAsync(profile.Id, Token));
    }

    [Fact]
    public async Task Update_of_missing_profile_returns_false()
    {
        var repository = await CreateRepositoryAsync();

        var ghost = Profile("Ghost");

        Assert.False(await repository.UpdateAsync(ghost, ghost.UpdatedAtUtc, Token));
        Assert.Empty(await repository.ListAsync(Token));
    }

    [Fact]
    public async Task Delete_removes_the_profile_once()
    {
        var repository = await CreateRepositoryAsync();
        var profile = Profile("Staging");
        await repository.AddAsync(profile, Token);

        Assert.True(await repository.DeleteAsync(profile.Id, Token));
        Assert.False(await repository.DeleteAsync(profile.Id, Token));
        Assert.Null(await repository.FindAsync(profile.Id, Token));
    }

    [Fact]
    public async Task Values_are_parameterized_not_concatenated()
    {
        var repository = await CreateRepositoryAsync();
        var profile = Profile("x'); DROP TABLE connection_profiles; --", database: "Sales'--");

        await repository.AddAsync(profile, Token);

        Assert.Equal(profile, await repository.FindAsync(profile.Id, Token));
    }

    [Fact]
    public async Task Corrupt_row_is_reported_with_the_profile_id_only()
    {
        var repository = await CreateRepositoryAsync();
        var profile = Profile("Staging");
        await repository.AddAsync(profile, Token);
        await ExecuteAsync("UPDATE connection_profiles SET authentication_type = '1';");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => repository.FindAsync(profile.Id, Token));

        Assert.Contains(profile.Id.ToString("D"), exception.Message, StringComparison.Ordinal);
        Assert.Contains("authentication_type", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_is_honored()
    {
        var repository = await CreateRepositoryAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.AddAsync(Profile("Staging"), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ListAsync(cancellation.Token));
        Assert.Empty(await repository.ListAsync(Token));
    }

    [Fact]
    public async Task Database_file_is_released_after_each_operation()
    {
        var repository = await CreateRepositoryAsync();
        await repository.AddAsync(Profile("Staging"), Token);

        // Without pooling, nothing keeps the file open, so it can be moved or deleted.
        File.Move(_temporary.Database.DatabasePath, _temporary.Database.DatabasePath + ".moved");
    }

    private async Task<SqliteConnectionProfileRepository> CreateRepositoryAsync() =>
        new(await _temporary.MigratedAsync());

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _temporary.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }

    private static DatabaseConnection Profile(
        string name,
        string database = "Sales",
        AuthenticationType authentication = AuthenticationType.SqlAuthentication,
        string? userName = "app",
        string? credentialReference = null,
        ConnectionSettings? settings = null,
        DateTimeOffset? updated = null) =>
        DatabaseConnection.Create(
            Guid.NewGuid(),
            name,
            DatabaseProviderType.SqlServer,
            "db01.example.test,1433",
            database,
            authentication,
            userName,
            credentialReference,
            settings ?? ConnectionSettings.Default,
            _created,
            updated ?? _created).Value;
}
