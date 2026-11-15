using DataBridge.Application.Abstractions;
using DataBridge.Core.Connections;

namespace DataBridge.Infrastructure.Providers;

/// <summary>Resolves the registered capabilities for a database engine.</summary>
internal sealed class DatabaseProviderFactory(
    IEnumerable<IDatabaseProvider> providers,
    IEnumerable<ISchemaReader> schemaReaders,
    IEnumerable<IDataReaderProvider> dataReaders,
    IEnumerable<ISqlGenerator> sqlGenerators,
    IEnumerable<ISynchronizationTargetInspector> inspectors) : IDatabaseProviderFactory
{
    private readonly Dictionary<DatabaseProviderType, IDatabaseProvider> _providers =
        providers.ToDictionary(provider => provider.ProviderType);

    private readonly Dictionary<DatabaseProviderType, ISchemaReader> _schemaReaders =
        schemaReaders.ToDictionary(reader => reader.ProviderType);

    private readonly Dictionary<DatabaseProviderType, IDataReaderProvider> _dataReaders =
        dataReaders.ToDictionary(reader => reader.ProviderType);

    private readonly Dictionary<DatabaseProviderType, ISqlGenerator> _sqlGenerators =
        sqlGenerators.ToDictionary(generator => generator.ProviderType);

    private readonly Dictionary<DatabaseProviderType, ISynchronizationTargetInspector> _inspectors =
        inspectors.ToDictionary(inspector => inspector.ProviderType);

    public IDatabaseProvider GetProvider(DatabaseProviderType providerType) =>
        _providers.TryGetValue(providerType, out var provider)
            ? provider
            : throw new NotSupportedException($"No database provider is registered for {providerType}.");

    public ISchemaReader GetSchemaReader(DatabaseProviderType providerType) =>
        _schemaReaders.TryGetValue(providerType, out var reader)
            ? reader
            : throw new NotSupportedException($"No schema reader is registered for {providerType}.");

    public IDataReaderProvider GetDataReader(DatabaseProviderType providerType) =>
        _dataReaders.TryGetValue(providerType, out var reader)
            ? reader
            : throw new NotSupportedException($"No data reader is registered for {providerType}.");

    public ISqlGenerator GetSqlGenerator(DatabaseProviderType providerType) =>
        _sqlGenerators.TryGetValue(providerType, out var generator)
            ? generator
            : throw new NotSupportedException($"No SQL generator is registered for {providerType}.");

    public ISynchronizationTargetInspector GetTargetInspector(DatabaseProviderType providerType) =>
        _inspectors.TryGetValue(providerType, out var inspector)
            ? inspector
            : throw new NotSupportedException($"No target inspector is registered for {providerType}.");
}
