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

    /// <summary>Creates an empty database with a unique, safe name and returns the name.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = $"DataBridgeTest_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE [{name}];", cancellationToken);
        return name;
    }

    /// <summary>Runs administrative SQL as <c>sa</c>. Test-only: callers pass generated, safe identifiers.</summary>
    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(Container.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
