using DataBridge.Application.DependencyInjection;
using DataBridge.Desktop.Services;
using DataBridge.Desktop.ViewModels;
using DataBridge.Infrastructure.DependencyInjection;
using DataBridge.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataBridge.Desktop.DependencyInjection;

/// <summary>The composition root: wires Application, Infrastructure, logging and view models.</summary>
public static class DesktopServiceCollectionExtensions
{
    /// <summary>Registers the view models. Pages are singletons so their state survives navigation.</summary>
    public static IServiceCollection AddDataBridgeDesktop(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<ConnectionsViewModel>();
        services.AddSingleton<SchemaComparisonViewModel>();
        services.AddSingleton<DataComparisonViewModel>();
        services.AddSingleton<SynchronizationViewModel>();
        services.AddSingleton<IFileSaver, AvaloniaFileSaver>();
        return services;
    }

    /// <summary>Builds the host with the default local data and log locations.</summary>
    public static IHost BuildHost(string[] args) =>
        BuildHost(args, InfrastructureOptions.Default(), new LoggingOptions(LoggingOptions.DefaultDirectory()));

    /// <summary>
    /// Builds the host. The locations are parameters, and <paramref name="configureServices"/> runs last, so
    /// tests can use throwaway folders and replace services such as the credential store.
    /// </summary>
    public static IHost BuildHost(
        string[] args,
        InfrastructureOptions infrastructure,
        LoggingOptions logging,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ApplicationName = "DataBridge",

            // A desktop app has no appsettings files or environment-specific configuration yet.
            DisableDefaults = true,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddDataBridgeLogging(logging);

        builder.Services
            .AddDataBridgeApplication()
            .AddDataBridgeInfrastructure(infrastructure)
            .AddDataBridgeDesktop();
        configureServices?.Invoke(builder.Services);

        // Catch missing registrations at startup rather than on first use.
        builder.ConfigureContainer(
            new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));

        return builder.Build();
    }
}
