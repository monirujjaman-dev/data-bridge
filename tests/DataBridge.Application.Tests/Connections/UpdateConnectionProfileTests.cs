using DataBridge.Application.Connections;
using DataBridge.Core.Connections;

namespace DataBridge.Application.Tests.Connections;

public sealed class UpdateConnectionProfileTests
{
    private readonly ConnectionProfileFixture _fixture = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Updates_fields_and_keeps_password_when_none_is_entered()
    {
        var original = await _fixture.CreateSqlProfileAsync();
        _fixture.Clock.Now = ConnectionProfileFixture.Start.AddHours(1);
        var input = ConnectionProfileFixture.SqlInput(password: null) with { Server = "db02", DatabaseName = "Sales_v2" };

        var result = await _fixture.Update.ExecuteAsync(original.Id, input, Token);

        Assert.True(result.IsSuccess);
        var (profile, storage, warning) = result.Value;
        Assert.Equal(SecretStorage.Unchanged, storage);
        Assert.Null(warning);
        Assert.Equal("db02", profile.Server);
        Assert.Equal("Sales_v2", profile.DatabaseName);
        Assert.Equal(original.CredentialReference, profile.CredentialReference);
        Assert.Equal(original.CreatedAtUtc, profile.CreatedAtUtc);
        Assert.Equal(ConnectionProfileFixture.Start.AddHours(1), profile.UpdatedAtUtc);
        Assert.Equal(new Secret("s3cr3t!"), _fixture.CredentialStore.Secrets[profile.CredentialReference!]);
        Assert.Equal(profile, Assert.Single(_fixture.Repository.Profiles));
    }

    [Fact]
    public async Task New_password_goes_to_a_new_entry_and_the_old_entry_is_removed()
    {
        var original = await _fixture.CreateSqlProfileAsync();

        var result = await _fixture.Update.ExecuteAsync(
            original.Id, ConnectionProfileFixture.SqlInput(password: "n3w-pass"), Token);

        var profile = result.Value.Profile;
        Assert.Equal(SecretStorage.StoredSecurely, result.Value.SecretStorage);
        Assert.Null(result.Value.Warning);
        Assert.NotEqual(original.CredentialReference, profile.CredentialReference);
        Assert.True(ProfileSecretStore.BelongsTo(profile.CredentialReference!, profile.Id));
        var stored = Assert.Single(_fixture.CredentialStore.Secrets);
        Assert.Equal(profile.CredentialReference, stored.Key);
        Assert.Equal(new Secret("n3w-pass"), stored.Value);
    }

    [Fact]
    public async Task Failed_update_keeps_the_old_password_and_profile()
    {
        var original = await _fixture.CreateSqlProfileAsync();
        _fixture.Repository.FailNextWrite = new IOException("database is locked");

        await Assert.ThrowsAsync<IOException>(() =>
            _fixture.Update.ExecuteAsync(original.Id, ConnectionProfileFixture.SqlInput(password: "n3w-pass"), Token));

        Assert.Equal(original, Assert.Single(_fixture.Repository.Profiles));
        var stored = Assert.Single(_fixture.CredentialStore.Secrets);
        Assert.Equal(original.CredentialReference, stored.Key);
        Assert.Equal(new Secret("s3cr3t!"), stored.Value);
    }

    [Fact]
    public async Task Profile_changed_concurrently_is_rejected_and_keeps_the_newer_version()
    {
        var original = await _fixture.CreateSqlProfileAsync();
        var newer = original.Rename("Renamed elsewhere", original.UpdatedAtUtc.AddSeconds(1)).Value;
        _fixture.Repository.BeforeUpdate = () => _fixture.Repository.Seed(newer);

        var result = await _fixture.Update.ExecuteAsync(
            original.Id, ConnectionProfileFixture.SqlInput(password: "n3w-pass"), Token);

        Assert.Equal(ConnectionProfileErrors.ModifiedConcurrently, Assert.Single(result.Errors));
        Assert.Equal(newer, Assert.Single(_fixture.Repository.Profiles));
        Assert.Equal(original.CredentialReference, Assert.Single(_fixture.CredentialStore.Secrets).Key);
    }

    [Fact]
    public async Task Profile_deleted_concurrently_returns_not_found_and_removes_the_new_secret()
    {
        var original = await _fixture.CreateSqlProfileAsync(password: null);
        _fixture.Repository.BeforeUpdate = () => _fixture.Repository.RemoveBehindTheScenes(original.Id);

        var result = await _fixture.Update.ExecuteAsync(original.Id, ConnectionProfileFixture.SqlInput(password: "n3w"), Token);

        Assert.Equal(ConnectionProfileErrors.NotFound, Assert.Single(result.Errors));
        Assert.Empty(_fixture.CredentialStore.Secrets);
    }

    [Fact]
    public async Task Duplicate_name_missed_by_the_pre_check_is_reported_and_keeps_the_old_password()
    {
        await _fixture.CreateSqlProfileAsync(name: "Staging");
        var other = await _fixture.CreateSqlProfileAsync(name: "Production");
        _fixture.Repository.NameCheckMissesDuplicates = true;

        var result = await _fixture.Update.ExecuteAsync(
            other.Id, ConnectionProfileFixture.SqlInput(name: "staging", password: "n3w"), Token);

        Assert.Equal(ConnectionProfileErrors.NameAlreadyExists, Assert.Single(result.Errors));
        Assert.Equal(new Secret("s3cr3t!"), _fixture.CredentialStore.Secrets[other.CredentialReference!]);
        Assert.Equal(2, _fixture.CredentialStore.Secrets.Count);
    }

    [Fact]
    public async Task Switching_to_integrated_removes_the_stored_password()
    {
        var original = await _fixture.CreateSqlProfileAsync();

        var result = await _fixture.Update.ExecuteAsync(original.Id, ConnectionProfileFixture.IntegratedInput(), Token);

        Assert.Equal(SecretStorage.Removed, result.Value.SecretStorage);
        Assert.Null(result.Value.Warning);
        Assert.Null(result.Value.Profile.CredentialReference);
        Assert.Null(result.Value.Profile.UserName);
        Assert.Empty(_fixture.CredentialStore.Secrets);
    }

    [Fact]
    public async Task Switching_to_integrated_without_a_secure_store_warns_that_the_password_remains()
    {
        var original = await _fixture.CreateSqlProfileAsync();
        _fixture.CredentialStore.IsAvailable = false;

        var result = await _fixture.Update.ExecuteAsync(original.Id, ConnectionProfileFixture.IntegratedInput(), Token);

        Assert.True(result.IsSuccess);
        Assert.Contains(original.CredentialReference!, result.Value.Warning, StringComparison.Ordinal);
        Assert.Null(result.Value.Profile.CredentialReference);
    }

    [Fact]
    public async Task Switching_to_integrated_clears_a_session_only_password()
    {
        _fixture.CredentialStore.IsAvailable = false;
        var original = await _fixture.CreateSqlProfileAsync();

        await _fixture.Update.ExecuteAsync(original.Id, ConnectionProfileFixture.IntegratedInput(), Token);

        Assert.Null(_fixture.SessionSecrets.Find(original.Id));
    }

    [Fact]
    public async Task Switching_to_integrated_is_saved_even_if_cancelled_during_cleanup()
    {
        var original = await _fixture.CreateSqlProfileAsync();
        using var cancellation = new CancellationTokenSource();
        _fixture.Repository.BeforeUpdate = cancellation.Cancel;

        var result = await _fixture.Update.ExecuteAsync(
            original.Id, ConnectionProfileFixture.IntegratedInput(), cancellation.Token);

        Assert.Equal(SecretStorage.Removed, result.Value.SecretStorage);
        Assert.Empty(_fixture.CredentialStore.Secrets);
    }

    [Fact]
    public async Task New_password_without_a_secure_store_is_kept_for_the_session()
    {
        _fixture.CredentialStore.IsAvailable = false;
        var original = await _fixture.CreateSqlProfileAsync(password: null);

        var result = await _fixture.Update.ExecuteAsync(
            original.Id, ConnectionProfileFixture.SqlInput(password: "n3w-pass"), Token);

        Assert.Equal(SecretStorageKind.SessionOnly, result.Value.SecretStorage.Kind);
        Assert.Null(result.Value.Warning);
        Assert.Null(result.Value.Profile.CredentialReference);
        Assert.Equal(new Secret("n3w-pass"), _fixture.SessionSecrets.Find(original.Id));
    }

    [Fact]
    public async Task New_password_without_a_secure_store_warns_about_the_previously_stored_one()
    {
        var original = await _fixture.CreateSqlProfileAsync();
        _fixture.CredentialStore.IsAvailable = false;

        var result = await _fixture.Update.ExecuteAsync(
            original.Id, ConnectionProfileFixture.SqlInput(password: "n3w-pass"), Token);

        Assert.Equal(SecretStorageKind.SessionOnly, result.Value.SecretStorage.Kind);
        Assert.Contains(original.CredentialReference!, result.Value.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_session_only_update_restores_the_previous_session_password()
    {
        _fixture.CredentialStore.IsAvailable = false;
        var original = await _fixture.CreateSqlProfileAsync(password: "first");
        _fixture.Repository.FailNextWrite = new IOException("database is locked");

        await Assert.ThrowsAsync<IOException>(() =>
            _fixture.Update.ExecuteAsync(original.Id, ConnectionProfileFixture.SqlInput(password: "second"), Token));

        Assert.Equal(new Secret("first"), _fixture.SessionSecrets.Find(original.Id));
    }

    [Fact]
    public async Task Missing_profile_returns_not_found()
    {
        var result = await _fixture.Update.ExecuteAsync(Guid.NewGuid(), ConnectionProfileFixture.SqlInput(), Token);

        Assert.Equal(ConnectionProfileErrors.NotFound, Assert.Single(result.Errors));
        Assert.Empty(_fixture.CredentialStore.Secrets);
    }

    [Fact]
    public async Task Name_taken_by_another_profile_is_rejected()
    {
        await _fixture.CreateSqlProfileAsync(name: "Staging");
        var other = await _fixture.CreateSqlProfileAsync(name: "Production");

        var result = await _fixture.Update.ExecuteAsync(
            other.Id, ConnectionProfileFixture.SqlInput(name: "staging", password: null), Token);

        Assert.Equal(ConnectionProfileErrors.NameAlreadyExists, Assert.Single(result.Errors));
    }

    [Fact]
    public async Task Keeping_its_own_name_is_allowed()
    {
        var original = await _fixture.CreateSqlProfileAsync(name: "Staging");

        var result = await _fixture.Update.ExecuteAsync(
            original.Id, ConnectionProfileFixture.SqlInput(name: "Staging", password: null), Token);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Update_time_always_moves_forward()
    {
        var original = await _fixture.CreateSqlProfileAsync();
        _fixture.Clock.Now = ConnectionProfileFixture.Start.AddHours(-1);

        var result = await _fixture.Update.ExecuteAsync(
            original.Id, ConnectionProfileFixture.SqlInput(password: null), Token);

        Assert.True(result.Value.Profile.UpdatedAtUtc > original.UpdatedAtUtc);
    }

    [Fact]
    public async Task Cancellation_is_honored()
    {
        var original = await _fixture.CreateSqlProfileAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _fixture.Update.ExecuteAsync(original.Id, ConnectionProfileFixture.SqlInput(), cancellation.Token));

        Assert.Equal(original, Assert.Single(_fixture.Repository.Profiles));
        Assert.Single(_fixture.CredentialStore.Secrets);
    }
}
