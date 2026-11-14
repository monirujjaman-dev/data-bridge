using System.Data;
using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;
using DataBridge.Core.Connections;
using DataBridge.Shared.Results;
using Microsoft.Data.SqlClient;

namespace DataBridge.Infrastructure.Providers.SqlServer;

/// <summary>The SQL Server provider's connection and identity capability (FR-CON-001 to FR-CON-005, FR-CON-008).</summary>
internal sealed class SqlServerDatabaseProvider : IDatabaseProvider
{
    /// <summary>Extra time on top of the profile timeout before the provider gives up on a step itself.</summary>
    private static readonly TimeSpan _timeoutGrace = TimeSpan.FromSeconds(2);

    public DatabaseProviderType ProviderType => DatabaseProviderType.SqlServer;

    public async Task<Result<ResolvedDatabaseIdentity>> ResolveIdentityAsync(
        DatabaseConnectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var timeoutSeconds = request.Profile.Settings.ConnectTimeoutSeconds;

        // SqlClient's ConnectTimeout does not bound every platform's TCP connect (on Windows a SYN to an
        // unreachable host waits for the OS timeout, about 21 seconds), so each step also gets its own budget.
        var budget = TimeSpan.FromSeconds(timeoutSeconds) + _timeoutGrace;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await using var connection = SqlServerConnectionFactory.Create(request);
            timeout.CancelAfter(budget);
            await connection.OpenAsync(timeout.Token);

            timeout.CancelAfter(budget);
            var info = await ReadServerInfoAsync(connection, timeoutSeconds, timeout.Token);
            if (info.CheckSupported() is { } unsupported)
            {
                return Result.Failure<ResolvedDatabaseIdentity>(unsupported);
            }

            return Result.Success(ResolvedDatabaseIdentity.Create(
                DatabaseProviderType.SqlServer,
                info.ServerName,
                info.DatabaseName,
                info.DatabaseId,
                info.DisplayVersion,
                info.DatabaseCollation,
                info.DatabaseGuid,
                info.DatabaseCreateDate));
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
        {
            // SqlClient may surface a cancelled attempt as a SqlException; report it as a cancellation.
            throw new OperationCanceledException("The connection attempt was cancelled.", exception, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            // Our budget ran out, not the caller's patience: that is a timeout, not a cancellation.
            return Result.Failure<ResolvedDatabaseIdentity>(ConnectionErrors.Timeout(timeoutSeconds));
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && SqlServerErrorTranslator.Translate(exception, request.Profile) is { } error)
        {
            return Result.Failure<ResolvedDatabaseIdentity>(error);
        }
    }

    private static async Task<SqlServerServerInfo> ReadServerInfoAsync(
        SqlConnection connection,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SqlServerServerInfo.Query;
        command.CommandTimeout = timeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The server returned no identity information.");
        }

        return new SqlServerServerInfo(
            ServerName: reader.GetString(0),
            DatabaseName: reader.GetString(1),
            DatabaseId: reader.GetInt16OrInt32(2),
            MajorVersion: SqlServerServerInfo.ParseMajorVersion(
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)),
            ProductVersion: reader.IsDBNull(4) ? "unknown version" : reader.GetString(4),
            Edition: reader.GetString(5),
            EngineEdition: reader.GetInt32(6),
            DatabaseCollation: reader.IsDBNull(7) ? null : reader.GetString(7),
            DatabaseGuid: reader.IsDBNull(8) ? null : reader.GetGuid(8),
            DatabaseCreateDate: reader.IsDBNull(9) ? null : reader.GetDateTime(9));
    }
}

internal static class SqlDataReaderExtensions
{
    /// <summary><c>DB_ID()</c> is <c>smallint</c>; read it without assuming the exact integer width.</summary>
    public static int GetInt16OrInt32(this SqlDataReader reader, int ordinal) =>
        Convert.ToInt32(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
}
