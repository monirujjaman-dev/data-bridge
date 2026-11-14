using DataBridge.Application.Connections;
using DataBridge.Core.Connections;

namespace DataBridge.Application.Tests.Connections;

public sealed class RenameConnectionProfileTests
{
    private readonly ConnectionProfileFixture _fixture = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Renames_and_keeps_the_password()
    {
        var original = await _fixture.CreateSqlProfileAsync(name: "Staging");
        _fixture.Clock.Now = ConnectionProfileFixture.Start.AddMinutes(3);

        var result = await _fixture.Rename.ExecuteAsync(original.Id, "Staging (old)", Token);

        Assert.Equal("Staging (old)", result.Value.Name);
        Assert.Equal(ConnectionProfileFixture.Start.AddMinutes(3), result.Value.UpdatedAtUtc);
        Assert.Equal(original.CredentialReference, result.Value.CredentialReference);
        Assert.Single(_fixture.CredentialStore.Secrets);
        Assert.Equal("Staging (old)", Assert.Single(_fixture.Repository.Profiles).Name);
    }

    [Fact]
    public async Task Changing_only_the_case_of_its_own_name_is_allowed()
    {
        var original = await _fixture.CreateSqlProfileAsync(name: "staging");

        var result = await _fixture.Rename.ExecuteAsync(original.Id, "Staging", Token);

        Assert.Equal("Staging", result.Value.Name);
    }

    [Fact]
    public async Task Name_taken_by_another_profile_is_rejected()
    {
        await _fixture.CreateSqlProfileAsync(name: "Staging");
        var other = await _fixture.CreateSqlProfileAsync(name: "Production");

        var result = await _fixture.Rename.ExecuteAsync(other.Id, "STAGING", Token);

        Assert.Equal(ConnectionProfileErrors.NameAlreadyExists, Assert.Single(result.Errors));
    }

    [Fact]
    public async Task Blank_name_is_rejected()
    {
        var original = await _fixture.CreateSqlProfileAsync();

        var result = await _fixture.Rename.ExecuteAsync(original.Id, "  ", Token);

        Assert.Equal(ConnectionProfileErrors.NameRequired, Assert.Single(result.Errors));
    }

    [Fact]
    public async Task Missing_profile_returns_not_found()
    {
        var result = await _fixture.Rename.ExecuteAsync(Guid.NewGuid(), "Anything", Token);

        Assert.Equal(ConnectionProfileErrors.NotFound, Assert.Single(result.Errors));
    }

    [Fact]
    public async Task Clock_behind_the_stored_time_still_moves_the_update_time_forward()
    {
        var original = await _fixture.CreateSqlProfileAsync();
        _fixture.Clock.Now = ConnectionProfileFixture.Start.AddHours(-1);

        var result = await _fixture.Rename.ExecuteAsync(original.Id, "Renamed", Token);

        Assert.True(result.Value.UpdatedAtUtc > original.UpdatedAtUtc);
    }

    [Fact]
    public async Task Profile_changed_concurrently_is_rejected_and_keeps_the_newer_version()
    {
        var original = await _fixture.CreateSqlProfileAsync();
        var newer = original.WithCredentialReference(
            ProfileSecretStore.NewReferenceFor(original.Id), original.UpdatedAtUtc.AddSeconds(1)).Value;
        _fixture.Repository.BeforeUpdate = () => _fixture.Repository.Seed(newer);

        var result = await _fixture.Rename.ExecuteAsync(original.Id, "Renamed", Token);

        Assert.Equal(ConnectionProfileErrors.ModifiedConcurrently, Assert.Single(result.Errors));
        Assert.Equal(newer, Assert.Single(_fixture.Repository.Profiles));
    }

    [Fact]
    public async Task Cancellation_is_honored()
    {
        var original = await _fixture.CreateSqlProfileAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _fixture.Rename.ExecuteAsync(original.Id, "Renamed", cancellation.Token));

        Assert.Equal(original.Name, Assert.Single(_fixture.Repository.Profiles).Name);
    }
}

public sealed class DeleteConnectionProfileTests
{
    private readonly ConnectionProfileFixture _fixture = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deletes_profile_and_its_stored_password()
    {
        var profile = await _fixture.CreateSqlProfileAsync();

        var result = await _fixture.Delete.ExecuteAsync(profile.Id, Token);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Warning);
        Assert.Empty(_fixture.Repository.Profiles);
        Assert.Empty(_fixture.CredentialStore.Secrets);
    }

    [Fact]
    public async Task Deletes_a_session_only_password()
    {
        _fixture.CredentialStore.IsAvailable = false;
        var profile = await _fixture.CreateSqlProfileAsync();

        var result = await _fixture.Delete.ExecuteAsync(profile.Id, Token);

        Assert.Null(result.Value.Warning);
        Assert.Null(_fixture.SessionSecrets.Find(profile.Id));
    }

    [Fact]
    public async Task Without_a_secure_store_deletes_the_profile_and_warns_about_the_stored_password()
    {
        var profile = await _fixture.CreateSqlProfileAsync();
        _fixture.CredentialStore.IsAvailable = false;

        var result = await _fixture.Delete.ExecuteAsync(profile.Id, Token);

        Assert.True(result.IsSuccess);
        Assert.Contains(profile.CredentialReference!, result.Value.Warning, StringComparison.Ordinal);
        Assert.Empty(_fixture.Repository.Profiles);
    }

    [Fact]
    public async Task Integrated_profile_is_deleted_without_touching_the_store()
    {
        var created = await _fixture.Create.ExecuteAsync(ConnectionProfileFixture.IntegratedInput(), Token);
        _fixture.CredentialStore.IsAvailable = false;

        var result = await _fixture.Delete.ExecuteAsync(created.Value.Profile.Id, Token);

        Assert.Null(result.Value.Warning);
        Assert.Empty(_fixture.Repository.Profiles);
    }

    [Fact]
    public async Task Missing_profile_returns_not_found()
    {
        var result = await _fixture.Delete.ExecuteAsync(Guid.NewGuid(), Token);

        Assert.Equal(ConnectionProfileErrors.NotFound, Assert.Single(result.Errors));
    }

    [Fact]
    public async Task Cancellation_is_honored()
    {
        var profile = await _fixture.CreateSqlProfileAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _fixture.Delete.ExecuteAsync(profile.Id, cancellation.Token));

        Assert.Single(_fixture.Repository.Profiles);
        Assert.Single(_fixture.CredentialStore.Secrets);
    }

    [Fact]
    public async Task List_returns_profiles_ordered_by_name()
    {
        await _fixture.CreateSqlProfileAsync(name: "zeta");
        await _fixture.CreateSqlProfileAsync(name: "Alpha");

        var profiles = await _fixture.List.ExecuteAsync(Token);

        Assert.Equal(["Alpha", "zeta"], profiles.Select(profile => profile.Name));
    }
}
