using DataBridge.Application.Connections;
using DataBridge.Core.Connections;
using DataBridge.Desktop.ViewModels;
using DataBridge.Shared.Results;

namespace DataBridge.Desktop.Tests.ViewModels;

/// <summary>The Test connection button: resolved identity on success (FR-CON-008), actionable errors on failure.</summary>
public sealed class TestConnectionViewModelTests : IDisposable
{
    private readonly DesktopFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Successful_test_shows_what_the_server_reports_and_saves_nothing()
    {
        var page = await StartWithFormAsync(password: "typed");

        await page.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal("Sales", page.TestedIdentity!.DatabaseName);
        Assert.Equal("DB01", page.TestedIdentity.ServerName);
        Assert.Null(page.TestError);
        Assert.Equal("Connected to Sales on DB01.", page.StatusText);
        Assert.Empty(page.Profiles);
        Assert.Empty(_fixture.CredentialStore.Secrets);
        Assert.Equal(new Secret("typed"), Assert.Single(_fixture.DatabaseProvider.Requests).Password);
    }

    [Fact]
    public async Task Failed_test_shows_the_actionable_message()
    {
        _fixture.DatabaseProvider.Next = Result.Failure<ResolvedDatabaseIdentity>(ConnectionErrors.LoginFailed);
        var page = await StartWithFormAsync(password: "wrong");

        await page.TestConnectionCommand.ExecuteAsync(null);

        Assert.Null(page.TestedIdentity);
        Assert.Equal(ConnectionErrors.LoginFailed.Message, page.TestError);
        Assert.Equal("The connection test failed.", page.StatusText);
    }

    [Fact]
    public async Task Missing_password_is_explained_without_connecting()
    {
        var page = await StartWithFormAsync(password: null);

        await page.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal(ConnectionErrors.PasswordRequired.Message, page.TestError);
        Assert.Empty(_fixture.DatabaseProvider.Requests);
    }

    [Fact]
    public async Task Invalid_fields_are_shown_on_the_form()
    {
        var page = await StartWithFormAsync(password: "typed");
        page.Editor.Server = "";

        await page.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal(ConnectionProfileErrors.ServerRequired.Message, page.Editor.ServerError);
        Assert.Null(page.TestError);
        Assert.Empty(_fixture.DatabaseProvider.Requests);
    }

    [Fact]
    public async Task Saved_profile_is_tested_with_its_saved_password()
    {
        var page = await StartWithFormAsync(password: "stored");
        await page.SaveCommand.ExecuteAsync(null);

        await page.TestConnectionCommand.ExecuteAsync(null);

        Assert.NotNull(page.TestedIdentity);
        Assert.Equal(new Secret("stored"), Assert.Single(_fixture.DatabaseProvider.Requests).Password);
    }

    [Fact]
    public async Task Editing_a_field_clears_a_stale_result()
    {
        var page = await StartWithFormAsync(password: "typed");
        await page.TestConnectionCommand.ExecuteAsync(null);
        Assert.True(page.HasTestResult);

        page.Editor.Server = "db02";

        Assert.False(page.HasTestResult);
        Assert.Null(page.TestedIdentity);
    }

    private async Task<ConnectionsViewModel> StartWithFormAsync(string? password)
    {
        var page = await _fixture.StartAsync();
        page.Editor.Name = "Staging";
        page.Editor.Server = "db01";
        page.Editor.DatabaseName = "Sales";
        page.Editor.Authentication = AuthenticationOption.For(AuthenticationType.SqlAuthentication);
        page.Editor.UserName = "app";
        page.Editor.Password = password;
        return page;
    }
}
