using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;
using DataBridge.Core.Connections;
using DataBridge.Shared.Results;

namespace DataBridge.Application.Tests.Connections;

public sealed class TestConnectionTests
{
    private readonly ConnectionProfileFixture _fixture = new();
    private readonly FakeDatabaseProvider _provider = new();
    private readonly TestConnection _test;

    public TestConnectionTests() =>
        _test = new TestConnection(
            _fixture.Repository, _fixture.Secrets, new FakeProviderFactory(_provider), _fixture.Clock, _fixture.Log.For<TestConnection>());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unsaved_input_is_tested_with_the_entered_password()
    {
        var result = await _test.ExecuteAsync(null, ConnectionProfileFixture.SqlInput(password: "typed"), Token);

        Assert.True(result.IsSuccess);
        var request = Assert.Single(_provider.Requests);
        Assert.Equal(new Secret("typed"), request.Password);
        Assert.Equal("db01", request.Profile.Server);
        Assert.Empty(_fixture.Repository.Profiles);
    }

    [Fact]
    public async Task Saved_profile_uses_its_stored_password_when_none_is_entered()
    {
        var profile = await _fixture.CreateSqlProfileAsync(password: "stored");

        await _test.ExecuteAsync(profile.Id, ConnectionProfileFixture.SqlInput(password: null), Token);

        Assert.Equal(new Secret("stored"), Assert.Single(_provider.Requests).Password);
    }

    [Fact]
    public async Task Entered_password_takes_precedence_over_the_stored_one()
    {
        var profile = await _fixture.CreateSqlProfileAsync(password: "stored");

        await _test.ExecuteAsync(profile.Id, ConnectionProfileFixture.SqlInput(password: "typed"), Token);

        Assert.Equal(new Secret("typed"), Assert.Single(_provider.Requests).Password);
    }

    [Fact]
    public async Task Session_only_password_is_used_when_the_store_is_unavailable()
    {
        _fixture.CredentialStore.IsAvailable = false;
        var profile = await _fixture.CreateSqlProfileAsync(password: "session");

        await _test.ExecuteAsync(profile.Id, ConnectionProfileFixture.SqlInput(password: null), Token);

        Assert.Equal(new Secret("session"), Assert.Single(_provider.Requests).Password);
    }

    [Fact]
    public async Task Missing_password_asks_for_one_without_connecting()
    {
        var profile = await _fixture.CreateSqlProfileAsync(password: null);

        var result = await _test.ExecuteAsync(profile.Id, ConnectionProfileFixture.SqlInput(password: null), Token);

        Assert.Equal(ConnectionErrors.PasswordRequired, Assert.Single(result.Errors));
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task Integrated_authentication_connects_without_a_password()
    {
        var result = await _test.ExecuteAsync(null, ConnectionProfileFixture.IntegratedInput(), Token);

        Assert.True(result.IsSuccess);
        Assert.Null(Assert.Single(_provider.Requests).Password);
    }

    [Fact]
    public async Task Invalid_input_is_reported_without_connecting()
    {
        var result = await _test.ExecuteAsync(null, ConnectionProfileFixture.SqlInput() with { Server = "" }, Token);

        Assert.Contains(ConnectionProfileErrors.ServerRequired, result.Errors);
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task Missing_saved_profile_is_reported()
    {
        var result = await _test.ExecuteAsync(Guid.NewGuid(), ConnectionProfileFixture.SqlInput(), Token);

        Assert.Equal(ConnectionProfileErrors.NotFound, Assert.Single(result.Errors));
    }

    [Fact]
    public async Task Provider_failure_is_returned_and_logged_by_code_only()
    {
        _provider.Next = Result.Failure<ResolvedDatabaseIdentity>(ConnectionErrors.LoginFailed);

        var result = await _test.ExecuteAsync(null, ConnectionProfileFixture.SqlInput(password: "Hunter2-secret"), Token);

        Assert.Equal(ConnectionErrors.LoginFailed, Assert.Single(result.Errors));
        var entry = _fixture.Log.Entries[^1];
        Assert.Contains("Connection.LoginFailed", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Hunter2-secret", entry.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("db01", entry.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_is_passed_to_the_provider()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _test.ExecuteAsync(null, ConnectionProfileFixture.SqlInput(), cancellation.Token));
    }

    private sealed class FakeDatabaseProvider : IDatabaseProvider
    {
        public List<DatabaseConnectionRequest> Requests { get; } = [];

        public Result<ResolvedDatabaseIdentity> Next { get; set; } = Result.Success(
            ResolvedDatabaseIdentity.Create(DatabaseProviderType.SqlServer, "DB01", "Sales", 5, "SQL Server 2022 (16.0.1) Developer Edition", "Latin1_General_CI_AS"));

        public DatabaseProviderType ProviderType => DatabaseProviderType.SqlServer;

        public Task<Result<ResolvedDatabaseIdentity>> ResolveIdentityAsync(
            DatabaseConnectionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(Next);
        }
    }

    private sealed class FakeProviderFactory(IDatabaseProvider provider) : IDatabaseProviderFactory
    {
        public IDatabaseProvider GetProvider(DatabaseProviderType providerType) => provider;

        public ISchemaReader GetSchemaReader(DatabaseProviderType providerType) => throw new NotSupportedException();

        public IDataReaderProvider GetDataReader(DatabaseProviderType providerType) => throw new NotSupportedException();
    }
}
