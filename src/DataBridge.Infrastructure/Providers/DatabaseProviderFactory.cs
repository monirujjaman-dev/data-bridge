using DataBridge.Application.Abstractions;
using DataBridge.Core.Connections;

namespace DataBridge.Infrastructure.Providers;

/// <summary>Resolves the registered capabilities for a database engine.</summary>
internal sealed class DatabaseProviderFactory(
    IEnumerable<IDatabaseProvider> providers,
    IEnumerable<ISchemaReader> schemaReaders) : IDatabaseProviderFactory
{
    private readonly Dictionary<DatabaseProviderType, IDatabaseProvider> _providers =
        providers.ToDictionary(provider => provider.ProviderType);

    private readonly Dictionary<DatabaseProviderType, ISchemaReader> _schemaReaders =
        schemaReaders.ToDictionary(reader => reader.ProviderType);

    public IDatabaseProvider GetProvider(DatabaseProviderType providerType) =>
        _providers.TryGetValue(providerType, out var provider)
            ? provider
            : throw new NotSupportedException($"No database provider is registered for {providerType}.");

    public ISchemaReader GetSchemaReader(DatabaseProviderType providerType) =>
        _schemaReaders.TryGetValue(providerType, out var reader)
            ? reader
            : throw new NotSupportedException($"No schema reader is registered for {providerType}.");
}
