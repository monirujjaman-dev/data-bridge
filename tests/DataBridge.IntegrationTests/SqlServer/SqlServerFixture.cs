using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;
using DataBridge.Core.Connections;
using DataBridge.IntegrationTests.SqlServer;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(SqlServerFixture))]

namespace DataBridge.IntegrationTests.SqlServer;

/// <summary>
/// A throwaway SQL Server container shared by the test run (ADR-0011). Tests create their own uniquely named
/// databases. When Docker is unavailable the tests are skipped, unless <c>DATABRIDGE_REQUIRE_SQL_SERVER=true</c>.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string RequireVariable = "DATABRIDGE_REQUIRE_SQL_SERVER";
    public const string ImageVariable = "DATABRIDGE_SQL_SERVER_IMAGE";
    public const string DefaultImage = "mcr.microsoft.com/mssql/server:2022-latest";

    private MsSqlContainer? _container;

    /// <summary>Why the server could not be started; <see langword="null"/> when it is running.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>A generated <c>sa</c> password that meets SQL Server's complexity rules.</summary>
    public string AdminPassword { get; } = $"Db!{Guid.NewGuid():N}aA1";

    /// <summary>The server as a profile would name it: <c>host,port</c>.</summary>
    public string Server => $"{Container.Hostname},{Container.GetMappedPublicPort(1433)}";

    private MsSqlContainer Container => _container ?? throw new InvalidOperationException("The SQL Server container is not running.");

    public async ValueTask InitializeAsync()
    {
        try
        {
            var image = Environment.GetEnvironmentVariable(ImageVariable) is { Length: > 0 } custom ? custom : DefaultImage;
            _container = new MsSqlBuilder(image).WithPassword(AdminPassword).Build();
            await _container.StartAsync();
        }
#pragma warning disable CA1031 // Any start-up failure (no Docker, no image) makes the server unavailable.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            UnavailableReason = exception.Message;
            _container = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Skips the calling test when there is no server, or fails it when CI requires one.</summary>
    public void RequireServer()
    {
        if (UnavailableReason is null)
        {
            return;
        }

        var required = string.Equals(Environment.GetEnvironmentVariable(RequireVariable), "true", StringComparison.OrdinalIgnoreCase);
        Assert.False(required, $"A SQL Server container is required here but could not start: {UnavailableReason}");
        Assert.Skip($"No SQL Server container (Docker unavailable?): {UnavailableReason}");
    }

    /// <summary>An <c>sa</c> connection string for <paramref name="database"/>, for tests that drive a connection themselves.</summary>
    public string ConnectionString(string database) =>
        new SqlConnectionStringBuilder(Container.GetConnectionString()) { InitialCatalog = database }.ConnectionString;

    /// <summary>
    /// Creates an empty database named exactly <paramref name="name"/>, which may contain line breaks, quotes and other
    /// text tools act on (but not <c>]</c>). Its files get generated names, so the name never becomes a path.
    /// </summary>
    public async Task CreateNamedDatabaseAsync(string name, string? collation, CancellationToken cancellationToken)
    {
        var path = (await QueryRowAsync("master", "SELECT CONVERT(nvarchar(260), SERVERPROPERTY('InstanceDefaultDataPath'))", cancellationToken)).TrimEnd('/');
        var file = $"DataBridgeTest_{Guid.NewGuid():N}";
        await ExecuteOneBatchAsync(
            $"CREATE DATABASE [{name}] ON (NAME = N'{file}', FILENAME = N'{path}/{file}.mdf') LOG ON (NAME = N'{file}_log', FILENAME = N'{path}/{file}.ldf')"
            + (collation is null ? "" : $" COLLATE {collation}") + ";",
            cancellationToken);
    }

    /// <summary>Drops a database created by a test, closing pooled connections to it first.</summary>
    public async Task DropDatabaseAsync(string name, CancellationToken cancellationToken)
    {
        SqlConnection.ClearAllPools();
        var literal = name.Replace("'", "''", StringComparison.Ordinal);
        await ExecuteOneBatchAsync(
            $"IF DB_ID(N'{literal}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END;",
            cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="sql"/> in <c>master</c> as one batch, never splitting on <c>GO</c>, for SQL that holds a
    /// name which may itself contain a <c>GO</c> line.
    /// </summary>
    private async Task ExecuteOneBatchAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString("master"));
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="script"/> with the container's own <c>sqlcmd</c> as <c>sa</c>, from a UTF-8 file, in
    /// <paramref name="database"/> (a plain name). With <paramref name="abortOnError"/> it passes <c>-b</c>.
    /// </summary>
    public async Task<(long ExitCode, string Output)> RunSqlcmdAsync(string database, string script, bool abortOnError, CancellationToken cancellationToken)
    {
        var file = $"/tmp/{Guid.NewGuid():N}.sql";
        await Container.CopyAsync(System.Text.Encoding.UTF8.GetBytes(script), file, ct: cancellationToken);
        // mssql-tools18 needs -C to trust the container's self-signed certificate; the older tools have no such flag.
        var command = $"""
            if [ -x /opt/mssql-tools18/bin/sqlcmd ]; then S=/opt/mssql-tools18/bin/sqlcmd; X=-C; else S=/opt/mssql-tools/bin/sqlcmd; X=; fi
            "$S" $X -S localhost -U sa -P '{AdminPassword}' -d '{database}' -f 65001 {(abortOnError ? "-b" : "")} -i {file}
            """;
        var result = await Container.ExecAsync(["/bin/sh", "-c", command], cancellationToken);
        return (result.ExitCode ?? -1, result.Stdout + result.Stderr);
    }

    /// <summary>Creates an empty database with a unique, safe name and returns the name.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = $"DataBridgeTest_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE [{name}];", cancellationToken);
        return name;
    }

    /// <summary>Runs administrative SQL as <c>sa</c>. Test-only: callers pass generated, safe identifiers.</summary>
    public Task ExecuteAsync(string sql, CancellationToken cancellationToken) =>
        ExecuteInAsync("master", sql, cancellationToken);

    /// <summary>
    /// Runs a script as <c>sa</c> in <paramref name="database"/>, batch by batch: lines that contain only <c>GO</c>
    /// separate batches, as in SQL Server tools.
    /// </summary>
    public async Task ExecuteInAsync(string database, string script, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(Container.GetConnectionString()) { InitialCatalog = database };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var batch in Batches(script))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>Runs a query as <c>sa</c> and returns its first row as invariant text, for before-and-after checks.</summary>
    public async Task<string> QueryRowAsync(string database, string sql, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(Container.GetConnectionString()) { InitialCatalog = database };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken), "The query returned no row.");
        return string.Join("|", Enumerable.Range(0, reader.FieldCount)
            .Select(ordinal => Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>A request for <paramref name="database"/> as <c>sa</c>, or as the given login.</summary>
    public DatabaseConnectionRequest Request(string database, string userName = "sa", string? password = null)
    {
        var profile = DatabaseConnection.Create(
            Guid.NewGuid(),
            "Integration test",
            DatabaseProviderType.SqlServer,
            Server,
            database,
            AuthenticationType.SqlAuthentication,
            userName,
            credentialReference: null,
            new ConnectionSettings(Encrypt: true, TrustServerCertificate: true, ConnectTimeoutSeconds: 30, ApplicationName: null),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow).Value;
        return new DatabaseConnectionRequest(profile, new Secret(password ?? AdminPassword));
    }

    private static IEnumerable<string> Batches(string script)
    {
        var batch = new System.Text.StringBuilder();
        foreach (var line in script.Split('\n'))
        {
            if (string.Equals(line.Trim(), "GO", StringComparison.OrdinalIgnoreCase))
            {
                if (batch.ToString().Trim().Length > 0)
                {
                    yield return batch.ToString();
                }

                batch.Clear();
                continue;
            }

            batch.Append(line).Append('\n');
        }

        if (batch.ToString().Trim().Length > 0)
        {
            yield return batch.ToString();
        }
    }
}
