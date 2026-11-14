using System.Diagnostics;
using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;
using DataBridge.Core.Connections;
using DataBridge.Infrastructure.Providers.SqlServer;
using DataBridge.Shared.Results;

namespace DataBridge.IntegrationTests.SqlServer;

/// <summary>
/// Phase 2 acceptance against a real SQL Server: connecting to two databases, actionable errors for wrong
/// credentials, unknown databases, untrusted certificates and unreachable servers, and timeout and cancellation.
/// </summary>
public sealed class SqlServerConnectionTests(SqlServerFixture server)
{
    private readonly SqlServerDatabaseProvider _provider = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sql_authentication_connects_and_resolves_the_database_identity()
    {
        server.RequireServer();
        var database = await server.CreateDatabaseAsync(Token);

        var result = await ConnectAsync(database);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        var identity = result.Value;
        Assert.Equal(DatabaseProviderType.SqlServer, identity.Provider);
        Assert.Equal(database, identity.DatabaseName);
        Assert.False(string.IsNullOrWhiteSpace(identity.ServerName));
        Assert.StartsWith("SQL Server 20", identity.ServerVersion, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(identity.DatabaseCollation));
        Assert.True(identity.DatabaseId > 4, "User databases have ids above the system databases.");
        Assert.NotNull(identity.DatabaseGuid);
        Assert.NotNull(identity.DatabaseCreateDate);
    }

    [Fact]
    public async Task Two_databases_resolve_to_different_identities_and_one_database_to_the_same()
    {
        server.RequireServer();
        var source = await server.CreateDatabaseAsync(Token);
        var target = await server.CreateDatabaseAsync(Token);

        var sourceIdentity = (await ConnectAsync(source)).Value;
        var targetIdentity = (await ConnectAsync(target)).Value;
        var sourceAgain = (await ConnectAsync(source)).Value;

        Assert.False(sourceIdentity.IsSameDatabaseAs(targetIdentity));
        Assert.True(sourceIdentity.IsSameDatabaseAs(sourceAgain));
        Assert.Equal(sourceIdentity.ServerName, targetIdentity.ServerName);
    }

    [Fact]
    public async Task Wrong_password_is_reported_as_a_failed_login_without_revealing_it()
    {
        server.RequireServer();
        const string wrongPassword = "Not-The-Password-123";

        var result = await ConnectAsync("master", password: wrongPassword);

        AssertError(result, ConnectionErrors.LoginFailed);
        Assert.DoesNotContain(wrongPassword, result.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_database_is_reported_as_unavailable()
    {
        server.RequireServer();

        var result = await ConnectAsync($"Missing_{Guid.NewGuid():N}");

        AssertError(result, ConnectionErrors.DatabaseUnavailable);
    }

    [Fact]
    public async Task Login_without_access_to_the_database_is_reported_as_unavailable()
    {
        server.RequireServer();
        var database = await server.CreateDatabaseAsync(Token);
        var login = $"databridge_{Guid.NewGuid():N}"[..24];
        var password = $"Lg!{Guid.NewGuid():N}aA1";

        // Generated identifier and password only; CREATE LOGIN cannot take parameters.
        await server.ExecuteAsync($"CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;", Token);

        var result = await ConnectAsync(database, userName: login, password: password);

        AssertError(result, ConnectionErrors.DatabaseUnavailable);
    }

    [Fact]
    public async Task Untrusted_server_certificate_is_refused_when_validation_is_on()
    {
        server.RequireServer();

        // The container uses a self-signed certificate, so validation must fail.
        var result = await ConnectAsync("master", settings: ConnectionSettings.Default);

        AssertError(result, ConnectionErrors.CertificateNotTrusted);
    }

    [Fact]
    public async Task Optional_encryption_connects_without_validating_the_certificate()
    {
        server.RequireServer();

        var result = await ConnectAsync("master", settings: ConnectionSettings.Default with { Encrypt = false });

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
    }

    [Fact]
    public async Task Closed_port_is_reported_as_unreachable()
    {
        var result = await ConnectToAsync("127.0.0.1,1", timeoutSeconds: 5, Token);

        AssertError(result, ConnectionErrors.ServerUnreachable);
    }

    [Fact]
    public async Task Unknown_host_is_reported_as_unreachable()
    {
        var result = await ConnectToAsync("databridge-host-does-not-exist.invalid", timeoutSeconds: 5, Token);

        AssertError(result, ConnectionErrors.ServerUnreachable);
    }

    [Fact]
    public async Task Server_that_accepts_but_never_answers_times_out_with_the_timeout_error()
    {
        // A local listener accepts the TCP connection and then stays silent, so only a timeout can end the attempt.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var accepted = new CancellationTokenSource();
        var accepting = Task.Run(async () =>
        {
            var clients = new List<System.Net.Sockets.TcpClient>();
            try
            {
                while (!accepted.IsCancellationRequested)
                {
                    clients.Add(await listener.AcceptTcpClientAsync(accepted.Token));
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                clients.ForEach(client => client.Dispose());
            }
        }, Token);
        var stopwatch = Stopwatch.StartNew();

        var result = await ConnectToAsync($"127.0.0.1,{port}", timeoutSeconds: 3, Token);

        await accepted.CancelAsync();
        await accepting;
        AssertError(result, ConnectionErrors.Timeout(3));

        // 3 s timeout plus the provider's 2 s grace, with margin for slow CI machines.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Non_routable_address_fails_within_the_timeout_budget()
    {
        // Depending on the network this hangs (timeout) or is refused (unreachable); either way it must not
        // take the OS's own TCP timeout of 20 seconds or more.
        var stopwatch = Stopwatch.StartNew();

        var result = await ConnectToAsync("10.255.255.1", timeoutSeconds: 3, Token);

        Assert.True(result.IsFailure);
        Assert.Contains(result.Errors[0].Code, new[] { "Connection.Timeout", ConnectionErrors.ServerUnreachable.Code });
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Cancelled_token_stops_before_connecting()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConnectToAsync("127.0.0.1,1", timeoutSeconds: 5, cancellation.Token));
    }

    [Fact]
    public async Task Cancelling_during_a_connection_attempt_stops_it_promptly()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConnectToAsync("10.255.255.1", timeoutSeconds: 60, cancellation.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"Took {stopwatch.Elapsed}.");
    }

    private Task<Result<ResolvedDatabaseIdentity>> ConnectAsync(
        string database,
        string userName = "sa",
        string? password = null,
        ConnectionSettings? settings = null)
    {
        var profile = Profile(server.Server, database, userName, settings ?? TrustedSettings(timeoutSeconds: 30));
        return _provider.ResolveIdentityAsync(
            new DatabaseConnectionRequest(profile, new Secret(password ?? server.AdminPassword)), Token);
    }

    private Task<Result<ResolvedDatabaseIdentity>> ConnectToAsync(
        string serverName,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var profile = Profile(serverName, "master", "sa", TrustedSettings(timeoutSeconds));
        return _provider.ResolveIdentityAsync(new DatabaseConnectionRequest(profile, new Secret("unused")), cancellationToken);
    }

    private static ConnectionSettings TrustedSettings(int timeoutSeconds) =>
        new(Encrypt: true, TrustServerCertificate: true, ConnectTimeoutSeconds: timeoutSeconds, ApplicationName: null);

    private static DatabaseConnection Profile(string serverName, string database, string userName, ConnectionSettings settings) =>
        DatabaseConnection.Create(
            Guid.NewGuid(),
            "Integration test",
            DatabaseProviderType.SqlServer,
            serverName,
            database,
            AuthenticationType.SqlAuthentication,
            userName,
            credentialReference: null,
            settings,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow).Value;

    private static void AssertError(Result result, Shared.Errors.Error expected)
    {
        Assert.True(result.IsFailure, "Expected the connection to fail.");
        Assert.Equal(expected.Code, Assert.Single(result.Errors).Code);
    }
}
