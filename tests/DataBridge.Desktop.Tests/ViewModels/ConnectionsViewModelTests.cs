using DataBridge.Application.Connections;
using DataBridge.Core.Connections;
using DataBridge.Desktop.ViewModels;
using Microsoft.Data.Sqlite;

namespace DataBridge.Desktop.Tests.ViewModels;

/// <summary>
/// Phase 1 acceptance through the view models: profiles can be created, edited and deleted; secrets are
/// not in SQLite or logs; behavior without a secure store is shown to the user; operations can be cancelled.
/// </summary>
public sealed class ConnectionsViewModelTests : IDisposable
{
    private const string Password = "Hunter2-very-secret";

    private readonly DesktopFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Startup_prepares_storage_and_shows_the_connections_page()
    {
        var page = await _fixture.StartAsync();

        Assert.Same(page, _fixture.MainWindow.CurrentPage);
        Assert.Equal("Connections", Assert.Single(_fixture.MainWindow.NavigationItems).Title);
        Assert.False(page.HasProfiles);
        Assert.True(page.Editor.IsNew);
        Assert.True(File.Exists(_fixture.DatabasePath));
    }

    [Fact]
    public async Task Startup_refuses_a_database_from_a_newer_version_and_says_why()
    {
        await _fixture.StartAsync();
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={_fixture.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 999;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var second = new DesktopFixtureOver(_fixture.DatabasePath);
        await second.MainWindow.InitializeCommand.ExecuteAsync(null);

        Assert.False(second.MainWindow.IsReady);
        Assert.Null(second.MainWindow.CurrentPage);
        Assert.Contains("Update DataBridge", second.MainWindow.StartupError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_a_profile_saves_it_lists_it_and_stores_the_password_securely()
    {
        var page = await _fixture.StartAsync();
        Fill(page.Editor, "Staging", password: Password);

        await page.SaveCommand.ExecuteAsync(null);

        var item = Assert.Single(page.Profiles);
        Assert.Equal("Staging", item.Name);
        Assert.Equal("db01 · Sales · app", item.Detail);
        Assert.Same(item, page.SelectedProfile);
        Assert.False(page.Editor.IsNew);
        Assert.Null(page.Editor.Password);
        Assert.True(page.Editor.HasSavedPassword);
        Assert.Null(page.Notice);
        Assert.Null(page.ErrorMessage);
        Assert.Equal("Saved Staging.", page.StatusText);
        Assert.Equal(new Secret(Password), Assert.Single(_fixture.CredentialStore.Secrets).Value);
    }

    [Fact]
    public async Task Password_is_not_written_to_sqlite_or_the_log()
    {
        var page = await _fixture.StartAsync();
        Fill(page.Editor, "Staging", password: Password);
        await page.SaveCommand.ExecuteAsync(null);
        _fixture.StopHost();

        Assert.DoesNotContain(Password, await File.ReadAllTextAsync(_fixture.DatabasePath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        foreach (var log in Directory.GetFiles(_fixture.LogDirectory))
        {
            var text = await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
            Assert.DoesNotContain("Staging", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Invalid_input_shows_errors_next_to_their_fields_and_saves_nothing()
    {
        var page = await _fixture.StartAsync();
        Fill(page.Editor, name: " ", server: "", userName: "");

        await page.SaveCommand.ExecuteAsync(null);

        Assert.Equal(ConnectionProfileErrors.NameRequired.Message, page.Editor.NameError);
        Assert.Equal(ConnectionProfileErrors.ServerRequired.Message, page.Editor.ServerError);
        Assert.Equal(ConnectionProfileErrors.UserNameRequired.Message, page.Editor.UserNameError);
        Assert.Null(page.Editor.DatabaseNameError);
        Assert.Empty(page.Profiles);
        Assert.Empty(_fixture.CredentialStore.Secrets);
    }

    [Fact]
    public async Task Duplicate_name_is_shown_on_the_name_field()
    {
        var page = await _fixture.StartAsync();
        Fill(page.Editor, "Staging");
        await page.SaveCommand.ExecuteAsync(null);
        page.NewProfileCommand.Execute(null);
        Fill(page.Editor, "STAGING");

        await page.SaveCommand.ExecuteAsync(null);

        Assert.Equal(ConnectionProfileErrors.NameAlreadyExists.Message, page.Editor.NameError);
        Assert.Single(page.Profiles);
    }

    [Fact]
    public async Task Without_a_secure_store_the_user_is_told_why_the_password_is_kept_for_this_session_only()
    {
        _fixture.CredentialStore.IsAvailable = false;
        var page = await _fixture.StartAsync();
        Fill(page.Editor, "Staging", password: Password);

        await page.SaveCommand.ExecuteAsync(null);

        Assert.Single(page.Profiles);
        Assert.Contains(FakeCredentialStore.UnavailableReason, page.Notice, StringComparison.Ordinal);
        Assert.Contains("never written to disk", page.Notice, StringComparison.Ordinal);
        Assert.True(page.Editor.HasSavedPassword);
        Assert.Empty(_fixture.CredentialStore.Secrets);
    }

    [Fact]
    public async Task Editing_a_profile_updates_it_and_keeps_the_saved_password()
    {
        var page = await _fixture.StartAsync();
        Fill(page.Editor, "Staging", password: Password);
        await page.SaveCommand.ExecuteAsync(null);

        page.Editor.Server = "db02";
        page.Editor.Name = "Staging 2";
        await page.SaveCommand.ExecuteAsync(null);

        var item = Assert.Single(page.Profiles);
        Assert.Equal("Staging 2", item.Name);
        Assert.Equal("db02", item.Profile.Server);
        Assert.Equal(new Secret(Password), Assert.Single(_fixture.CredentialStore.Secrets).Value);
    }

    [Fact]
    public async Task Selecting_a_profile_loads_it_into_the_editor_without_the_password()
    {
        var page = await _fixture.StartAsync();
        Fill(page.Editor, "Alpha", password: Password);
        await page.SaveCommand.ExecuteAsync(null);
        page.NewProfileCommand.Execute(null);
        Fill(page.Editor, "Beta", server: "db09");
        await page.SaveCommand.ExecuteAsync(null);

        page.SelectedProfile = page.Profiles.Single(item => item.Name == "Alpha");

        Assert.Equal("Alpha", page.Editor.Name);
        Assert.Equal("db01", page.Editor.Server);
        Assert.Null(page.Editor.Password);
        Assert.True(page.Editor.HasSavedPassword);
        Assert.Equal("Edit Alpha", page.Editor.Title);
    }

    [Fact]
    public async Task Integrated_authentication_sends_no_user_name_or_password()
    {
        var page = await _fixture.StartAsync();
        Fill(page.Editor, "Local", password: Password);
        page.Editor.Authentication = AuthenticationOption.For(AuthenticationType.Integrated);

        Assert.False(page.Editor.UsesSqlAuthentication);
        var input = page.Editor.ToInput();
        Assert.Null(input.UserName);
        Assert.Null(input.Password);

        await page.SaveCommand.ExecuteAsync(null);
        Assert.Equal(AuthenticationType.Integrated, Assert.Single(page.Profiles).Profile.AuthenticationType);
        Assert.Empty(_fixture.CredentialStore.Secrets);
    }

    [Fact]
    public async Task Deleting_asks_for_confirmation_then_removes_the_profile_and_its_password()
    {
        var page = await _fixture.StartAsync();
        Fill(page.Editor, "Staging", password: Password);
        await page.SaveCommand.ExecuteAsync(null);

        page.RequestDeleteCommand.Execute(null);
        Assert.True(page.IsConfirmingDelete);
        Assert.False(page.SaveCommand.CanExecute(null));
        Assert.Single(page.Profiles);

        await page.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.False(page.IsConfirmingDelete);
        Assert.Empty(page.Profiles);
        Assert.Empty(_fixture.CredentialStore.Secrets);
        Assert.True(page.Editor.IsNew);
        Assert.Equal("Deleted Staging.", page.StatusText);
    }

    [Fact]
    public async Task Cancelling_a_delete_keeps_the_profile()
    {
        var page = await _fixture.StartAsync();
        Fill(page.Editor, "Staging");
        await page.SaveCommand.ExecuteAsync(null);

        page.RequestDeleteCommand.Execute(null);
        page.CancelDeleteCommand.Execute(null);

        Assert.False(page.IsConfirmingDelete);
        Assert.Single(page.Profiles);
        Assert.True(page.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_running_save_can_be_cancelled_and_saves_nothing()
    {
        var page = await _fixture.StartAsync();
        _fixture.CredentialStore.SaveGate = new TaskCompletionSource();
        Fill(page.Editor, "Staging", password: Password);

        var saving = page.SaveCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => page.IsBusy);
        Assert.False(page.NewProfileCommand.CanExecute(null));
        page.CancelBusyCommand.Execute(null);
        await saving;

        Assert.False(page.IsBusy);
        Assert.Equal("Cancelled.", page.StatusText);
        Assert.Empty(page.Profiles);
        Assert.Empty(_fixture.CredentialStore.Secrets);
    }

    [Fact]
    public async Task Unexpected_failure_is_reported_without_details_and_the_page_recovers()
    {
        var page = await _fixture.StartAsync();
        _fixture.CredentialStore.SaveFailure = new InvalidOperationException($"Store failed with {Password}");
        Fill(page.Editor, "Staging", password: Password);

        await page.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Could not save profile. The details are in the log file.", page.ErrorMessage);
        Assert.False(page.IsBusy);
        Assert.True(page.SaveCommand.CanExecute(null));
        Assert.Empty(page.Profiles);
    }

    private static void Fill(
        ConnectionProfileEditorViewModel editor,
        string? name,
        string? server = "db01",
        string? userName = "app",
        string? password = null)
    {
        editor.Name = name;
        editor.Server = server;
        editor.DatabaseName = "Sales";
        editor.UserName = userName;
        editor.Password = password;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>A second app instance over an existing database file.</summary>
    private sealed class DesktopFixtureOver(string databasePath) : IDisposable
    {
        private readonly Microsoft.Extensions.Hosting.IHost _host = DependencyInjection.DesktopServiceCollectionExtensions.BuildHost(
            [],
            new Infrastructure.DependencyInjection.InfrastructureOptions(databasePath),
            new Infrastructure.Logging.LoggingOptions(Path.Combine(Path.GetDirectoryName(databasePath)!, "logs2")));

        public MainWindowViewModel MainWindow =>
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<MainWindowViewModel>(_host.Services);

        public void Dispose() => _host.Dispose();
    }
}
