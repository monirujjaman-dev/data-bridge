using DataBridge.Application.Connections;
using DataBridge.Core.Connections;

namespace DataBridge.Application.Tests.Connections;

public sealed class CreateConnectionProfileTests
{
    private readonly ConnectionProfileFixture _fixture = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Saves_profile_and_stores_password_in_the_credential_store()
    {
        var result = await _fixture.Create.ExecuteAsync(ConnectionProfileFixture.SqlInput(), Token);

        Assert.True(result.IsSuccess);
        var profile = result.Value.Profile;
        Assert.Equal(SecretStorage.StoredSecurely, result.Value.SecretStorage);
        Assert.True(ProfileSecretStore.BelongsTo(profile.CredentialReference!, profile.Id));
        Assert.Equal(new Secret("s3cr3t!"), _fixture.CredentialStore.Secrets[profile.CredentialReference!]);
        Assert.Equal(profile, Assert.Single(_fixture.Repository.Profiles));
        Assert.Equal(ConnectionProfileFixture.Start, profile.CreatedAtUtc);
        Assert.Equal(ConnectionProfileFixture.Start, profile.UpdatedAtUtc);
    }

    [Fact]
    public async Task Credential_reference_contains_only_the_profile_id_and_a_random_suffix()
    {
        var profile = await _fixture.CreateSqlProfileAsync(name: "Customer Prod");

        Assert.Matches(
            $"^DataBridge/connection-profile/{profile.Id:D}/[0-9a-f]{{32}}$", profile.CredentialReference!);
        Assert.DoesNotContain("Customer", profile.CredentialReference!, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", profile.CredentialReference!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_secure_store_keeps_the_password_for_the_session_and_says_why()
    {
        _fixture.CredentialStore.IsAvailable = false;

        var result = await _fixture.Create.ExecuteAsync(ConnectionProfileFixture.SqlInput(), Token);

        Assert.True(result.IsSuccess);
        var (profile, storage, _) = result.Value;
        Assert.Equal(SecretStorageKind.SessionOnly, storage.Kind);
        Assert.Equal(FakeCredentialStore.UnavailableReason, storage.Reason);
        Assert.Null(profile.CredentialReference);
        Assert.Empty(_fixture.CredentialStore.Secrets);
        Assert.Equal(new Secret("s3cr3t!"), _fixture.SessionSecrets.Find(profile.Id));
    }

    [Fact]
    public async Task Without_a_password_saves_profile_with_no_secret()
    {
        var result = await _fixture.Create.ExecuteAsync(ConnectionProfileFixture.SqlInput(password: null), Token);

        Assert.Equal(SecretStorage.None, result.Value.SecretStorage);
        Assert.Null(result.Value.Profile.CredentialReference);
        Assert.Empty(_fixture.CredentialStore.Secrets);
    }

    [Fact]
    public async Task Integrated_profile_has_no_user_name_or_secret()
    {
        var result = await _fixture.Create.ExecuteAsync(ConnectionProfileFixture.IntegratedInput(), Token);

        Assert.Equal(AuthenticationType.Integrated, result.Value.Profile.AuthenticationType);
        Assert.Null(result.Value.Profile.CredentialReference);
        Assert.Empty(_fixture.CredentialStore.Secrets);
    }

    [Fact]
    public async Task Integrated_profile_rejects_a_password()
    {
        var input = ConnectionProfileFixture.IntegratedInput() with { Password = new Secret("x") };

        var result = await _fixture.Create.ExecuteAsync(input, Token);

        Assert.Contains(ConnectionProfileErrors.PasswordNotAllowed, result.Errors);
        Assert.Empty(_fixture.Repository.Profiles);
        Assert.Empty(_fixture.CredentialStore.Secrets);
    }

    [Fact]
    public async Task Invalid_input_saves_nothing()
    {
        var input = ConnectionProfileFixture.SqlInput() with { Server = " ", UserName = null };

        var result = await _fixture.Create.ExecuteAsync(input, Token);

        Assert.Contains(ConnectionProfileErrors.ServerRequired, result.Errors);
        Assert.Contains(ConnectionProfileErrors.UserNameRequired, result.Errors);
        Assert.Empty(_fixture.Repository.Profiles);
        Assert.Empty(_fixture.CredentialStore.Secrets);
    }

    [Fact]
    public async Task Duplicate_name_is_rejected_case_insensitively_before_storing_a_secret()
    {
        await _fixture.CreateSqlProfileAsync(name: "Staging");

        var result = await _fixture.Create.ExecuteAsync(ConnectionProfileFixture.SqlInput(name: "STAGING"), Token);

        Assert.Equal(ConnectionProfileErrors.NameAlreadyExists, Assert.Single(result.Errors));
        Assert.Single(_fixture.Repository.Profiles);
        Assert.Single(_fixture.CredentialStore.Secrets);
    }

    [Fact]
    public async Task Duplicate_name_missed_by_the_pre_check_is_reported_and_removes_the_new_secret()
    {
        await _fixture.CreateSqlProfileAsync(name: "Staging");
        _fixture.Repository.NameCheckMissesDuplicates = true;

        var result = await _fixture.Create.ExecuteAsync(ConnectionProfileFixture.SqlInput(name: "staging"), Token);

        Assert.Equal(ConnectionProfileErrors.NameAlreadyExists, Assert.Single(result.Errors));
        Assert.Single(_fixture.Repository.Profiles);
        Assert.Single(_fixture.CredentialStore.Secrets);
    }

    [Fact]
    public async Task Repository_failure_without_a_secure_store_removes_the_session_password()
    {
        _fixture.CredentialStore.IsAvailable = false;
        _fixture.Repository.FailNextWrite = new IOException("disk full");

        await Assert.ThrowsAsync<IOException>(() =>
            _fixture.Create.ExecuteAsync(ConnectionProfileFixture.SqlInput(), Token));

        Assert.Empty(_fixture.Repository.Profiles);
        Assert.Null(_fixture.SessionSecrets.Find(_fixture.Repository.LastAddAttempt!.Id));
    }

    [Fact]
    public async Task Repository_failure_removes_the_stored_secret_and_rethrows()
    {
        _fixture.Repository.FailNextWrite = new IOException("disk full");

        await Assert.ThrowsAsync<IOException>(() =>
            _fixture.Create.ExecuteAsync(ConnectionProfileFixture.SqlInput(), Token));

        Assert.Empty(_fixture.CredentialStore.Secrets);
        Assert.Empty(_fixture.Repository.Profiles);
    }

    [Fact]
    public async Task Cancellation_is_honored_and_saves_nothing()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _fixture.Create.ExecuteAsync(ConnectionProfileFixture.SqlInput(), cancellation.Token));

        Assert.Empty(_fixture.Repository.Profiles);
        Assert.Empty(_fixture.CredentialStore.Secrets);
    }

    [Fact]
    public void Input_and_secret_do_not_reveal_the_password_when_formatted()
    {
        var input = ConnectionProfileFixture.SqlInput(password: "hunter2");

        Assert.DoesNotContain("hunter2", input.ToString(), StringComparison.Ordinal);
        Assert.Equal(Secret.RedactedText, input.Password!.ToString());
        Assert.Equal("hunter2", input.Password.Reveal());
    }
}
