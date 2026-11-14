using DataBridge.Application.Abstractions;
using DataBridge.Infrastructure.Persistence.Sqlite;
using DataBridge.Infrastructure.Providers;
using DataBridge.Infrastructure.Providers.SqlServer;
using DataBridge.Infrastructure.Providers.SqlServer.Schema;
using DataBridge.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DataBridge.Infrastructure.DependencyInjection;

/// <summary>Where Infrastructure keeps local state.</summary>
/// <param name="DatabasePath">The local SQLite database file; see <see cref="LocalDatabase.DefaultPath"/>.</param>
public sealed record InfrastructureOptions(string DatabasePath)
{
    /// <summary>The default locations under the user's local application data folder.</summary>
    public static InfrastructureOptions Default() => new(LocalDatabase.DefaultPath());
}

/// <summary>Registers the Infrastructure implementations of the Application ports.</summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddDataBridgeInfrastructure(
        this IServiceCollection services,
        InfrastructureOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(new LocalDatabase(options.DatabasePath));
        services.AddSingleton<SqliteMigrator>();
        services.AddSingleton<ILocalDataStore, SqliteLocalDataStore>();
        services.AddSingleton<IConnectionProfileRepository, SqliteConnectionProfileRepository>();

        // The OS-native store, or one that reports itself unavailable; never a plaintext fallback.
        services.AddSingleton(_ => CredentialStoreFactory.CreateForCurrentPlatform());

        services.AddSingleton<IDatabaseProvider, SqlServerDatabaseProvider>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISchemaReader, SqlServerSchemaReader>();
        services.AddSingleton<IDatabaseProviderFactory, DatabaseProviderFactory>();

        return services;
    }
}
