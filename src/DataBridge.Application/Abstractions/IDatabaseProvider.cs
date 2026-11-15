using DataBridge.Application.Connections;
using DataBridge.Core.Connections;
using DataBridge.Shared.Results;

namespace DataBridge.Application.Abstractions;

/// <summary>A profile plus the password to use with it. The password is never stored by a provider.</summary>
/// <param name="Profile">The connection settings.</param>
/// <param name="Password">The SQL authentication password; <see langword="null"/> for integrated authentication.</param>
public sealed record DatabaseConnectionRequest(DatabaseConnection Profile, Secret? Password);

/// <summary>
/// Provider identity and connection information (architecture §6). One implementation per database
/// engine; SQL Server is the only v1 provider. Schema, data and execution capabilities are separate ports.
/// </summary>
public interface IDatabaseProvider
{
    DatabaseProviderType ProviderType { get; }

    /// <summary>
    /// Connects, checks that the server is a supported engine and version, and returns what the server
    /// reports about itself. Never modifies the database.
    /// </summary>
    /// <returns>
    /// The identity, or a failure with an actionable <see cref="ConnectionErrors"/> error (wrong credentials,
    /// unreachable server, timeout, untrusted certificate, unsupported version). Unexpected failures throw.
    /// </returns>
    Task<Result<ResolvedDatabaseIdentity>> ResolveIdentityAsync(
        DatabaseConnectionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Resolves a database engine's provider capabilities.</summary>
public interface IDatabaseProviderFactory
{
    /// <exception cref="NotSupportedException">No provider is registered for <paramref name="providerType"/>.</exception>
    IDatabaseProvider GetProvider(DatabaseProviderType providerType);

    /// <exception cref="NotSupportedException">No schema reader is registered for <paramref name="providerType"/>.</exception>
    ISchemaReader GetSchemaReader(DatabaseProviderType providerType);

    /// <exception cref="NotSupportedException">No data reader is registered for <paramref name="providerType"/>.</exception>
    IDataReaderProvider GetDataReader(DatabaseProviderType providerType);

    /// <exception cref="NotSupportedException">No SQL generator is registered for <paramref name="providerType"/>.</exception>
    ISqlGenerator GetSqlGenerator(DatabaseProviderType providerType);

    /// <exception cref="NotSupportedException">No target inspector is registered for <paramref name="providerType"/>.</exception>
    ISynchronizationTargetInspector GetTargetInspector(DatabaseProviderType providerType);
}
