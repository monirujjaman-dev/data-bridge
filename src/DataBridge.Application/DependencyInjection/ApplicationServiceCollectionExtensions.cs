using DataBridge.Application.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DataBridge.Application.DependencyInjection;

/// <summary>Registers the Application use cases. Their ports are registered by Infrastructure.</summary>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddDataBridgeApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // One cache per application session: session-only passwords live exactly as long as the process.
        services.AddSingleton<SessionSecretCache>();
        services.AddSingleton<ProfileSecretStore>();

        services.AddTransient<ListConnectionProfiles>();
        services.AddTransient<CreateConnectionProfile>();
        services.AddTransient<UpdateConnectionProfile>();
        services.AddTransient<RenameConnectionProfile>();
        services.AddTransient<DeleteConnectionProfile>();
        services.AddTransient<TestConnection>();

        return services;
    }
}
