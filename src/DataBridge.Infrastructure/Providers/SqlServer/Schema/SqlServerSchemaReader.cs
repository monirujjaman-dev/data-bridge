using System.Data;
using DataBridge.Application.Abstractions;
using DataBridge.Application.SchemaComparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Schema;
using DataBridge.Shared.Results;
using Microsoft.Data.SqlClient;

namespace DataBridge.Infrastructure.Providers.SqlServer.Schema;

/// <summary>
/// Reads a SQL Server database's schema from its catalog views (architecture §7) in one read-only batch, and
/// reports what it cannot compare (FR-SCH-010).
/// </summary>
/// <remarks>
/// Catalog views are not versioned, so a concurrent DDL change can make the result sets disagree; that is detected
/// (a child whose parent is missing) and reported as <see cref="SchemaComparisonErrors.MetadataChanged"/>.
/// </remarks>
internal sealed class SqlServerSchemaReader(TimeProvider timeProvider) : ISchemaReader
{
    /// <summary>Catalog reads of very large databases can be slow; this bounds them without being the connect timeout.</summary>
    public const int CommandTimeoutSeconds = 300;

    public DatabaseProviderType ProviderType => DatabaseProviderType.SqlServer;

    public ISqlDefinitionNormalizer DefinitionNormalizer => TSqlDefinitionNormalizer.Instance;

    public async Task<Result<DatabaseSchema>> ReadSchemaAsync(DatabaseConnectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var opened = false;
        try
        {
            await using var connection = SqlServerConnectionFactory.Create(request);
            await connection.OpenAsync(cancellationToken);
            opened = true;

            await using var command = connection.CreateCommand();
            command.CommandText = SqlServerCatalogQuery.Text;
            command.CommandTimeout = CommandTimeoutSeconds;
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.Default, cancellationToken);

            var catalog = await SqlServerCatalog.ReadAsync(reader, cancellationToken);
            return Result.Success(new SqlServerSchemaAssembler(catalog).Build(connection.Database, timeProvider.GetUtcNow()));
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
        {
            throw new OperationCanceledException("The schema read was cancelled.", exception, cancellationToken);
        }
        catch (CatalogChangedException)
        {
            return Result.Failure<DatabaseSchema>(SchemaComparisonErrors.MetadataChanged);
        }
        catch (SqlException exception) when (opened)
        {
            return Result.Failure<DatabaseSchema>(SchemaComparisonErrors.MetadataNotReadable(exception.Number, exception.Message));
        }
        catch (Exception exception) when (!opened
            && exception is not OperationCanceledException
            && SqlServerErrorTranslator.Translate(exception, request.Profile) is { } error)
        {
            return Result.Failure<DatabaseSchema>(error);
        }
    }
}

/// <summary>The catalog result sets disagree because the schema changed while it was read.</summary>
internal sealed class CatalogChangedException : Exception
{
    public CatalogChangedException()
        : base("The catalog changed while it was being read.")
    {
    }

    public CatalogChangedException(string message)
        : base(message)
    {
    }

    public CatalogChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
