using DataBridge.Application.Connections;
using Microsoft.Extensions.Logging;

namespace DataBridge.Application.Tests.Connections;

/// <summary>Use cases log ids and outcomes only: no passwords, names, servers or user names (NFR-SEC-003).</summary>
public sealed class ConnectionProfileLoggingTests
{
    private const string Password = "Hunter2-very-secret";

    private readonly ConnectionProfileFixture _fixture = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_logs_the_profile_id_and_storage_outcome()
    {
        var profile = await _fixture.CreateSqlProfileAsync(password: Password);

        var entry = Assert.Single(_fixture.Log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(profile.Id.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SecretStorageKind.StoredSecurely), entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_use_case_logs_no_password_or_identifying_profile_data()
    {
        var profile = await _fixture.CreateSqlProfileAsync(name: "Customer Prod", password: Password);
        await _fixture.Update.ExecuteAsync(profile.Id, ConnectionProfileFixture.SqlInput("Customer Prod", Password + "2"), Token);
        await _fixture.Rename.ExecuteAsync(profile.Id, "Customer Prod (old)", Token);
        _fixture.CredentialStore.IsAvailable = false;
        await _fixture.Update.ExecuteAsync(profile.Id, ConnectionProfileFixture.SqlInput("Customer Prod (old)", Password + "3"), Token);
        await _fixture.Delete.ExecuteAsync(profile.Id, Token);

        Assert.NotEmpty(_fixture.Log.Entries);
        Assert.All(_fixture.Log.Entries, entry =>
        {
            Assert.DoesNotContain(Password, entry.AllText, StringComparison.Ordinal);
            Assert.DoesNotContain("Customer", entry.AllText, StringComparison.Ordinal);
            Assert.DoesNotContain("db01", entry.AllText, StringComparison.Ordinal);
            Assert.DoesNotContain("app", entry.Values.Select(value => value.Value?.ToString()));
        });
    }

    [Fact]
    public async Task Session_only_fallback_is_logged_as_a_warning_with_the_reason()
    {
        _fixture.CredentialStore.IsAvailable = false;

        var profile = await _fixture.CreateSqlProfileAsync(password: Password);

        var warning = Assert.Single(_fixture.Log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(profile.Id.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.Contains(FakeCredentialStore.UnavailableReason, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_update_logs_the_error_codes()
    {
        var profile = await _fixture.CreateSqlProfileAsync();

        await _fixture.Update.ExecuteAsync(profile.Id, ConnectionProfileFixture.SqlInput() with { Server = "" }, Token);

        var entry = _fixture.Log.Entries[^1];
        Assert.Contains("ConnectionProfile.ServerRequired", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_cleanup_is_logged_with_the_entry_to_remove()
    {
        var profile = await _fixture.CreateSqlProfileAsync();
        _fixture.CredentialStore.IsAvailable = false;

        await _fixture.Delete.ExecuteAsync(profile.Id, Token);

        var warning = Assert.Single(_fixture.Log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(profile.CredentialReference!, warning.Message, StringComparison.Ordinal);
    }
}
