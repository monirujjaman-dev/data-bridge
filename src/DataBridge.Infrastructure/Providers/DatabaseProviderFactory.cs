using DataBridge.Application.Abstractions;
using DataBridge.Core.Connections;

namespace DataBridge.Infrastructure.Providers;

/// <summary>Resolves the registered provider for a database engine.</summary>
internal sealed class DatabaseProviderFactory(IEnumerable<IDatabaseProvider> providers) : IDatabaseProviderFactory
{
    private readonly Dictionary<DatabaseProviderType, IDatabaseProvider> _providers =
        providers.ToDictionary(provider => provider.ProviderType);

    public IDatabaseProvider GetProvider(DatabaseProviderType providerType) =>
        _providers.TryGetValue(providerType, out var provider)
            ? provider
            : throw new NotSupportedException($"No database provider is registered for {providerType}.");
}
