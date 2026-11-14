using Avalonia;
using Avalonia.Threading;
using DataBridge.Desktop.DependencyInjection;
using DataBridge.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DataBridge.Desktop;

internal static class Program
{
    // Avalonia is not ready until AppMain runs: do not touch Avalonia types or
    // SynchronizationContext-dependent code before BuildAvaloniaApp is called.
    [STAThread]
    public static int Main(string[] args)
    {
        using var host = DesktopServiceCollectionExtensions.BuildHost(args);
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DataBridge.Desktop");
        LogUnhandledExceptions(logger);

        try
        {
            return BuildAvaloniaApp(host.Services).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            DesktopLog.Unhandled(logger, exception, "main");
            throw;
        }
    }

    // Used by the visual designer, which runs without application services.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(services: null);

    private static AppBuilder BuildAvaloniaApp(IServiceProvider? services) =>
        AppBuilder.Configure(() => new App { Services = services })
            .UsePlatformDetect()
            .LogToTrace();

    private static void LogUnhandledExceptions(ILogger logger)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                DesktopLog.Unhandled(logger, exception, "AppDomain");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            DesktopLog.Unhandled(logger, eventArgs.Exception, "unobserved task");
            eventArgs.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
            DesktopLog.Unhandled(logger, eventArgs.Exception, "UI thread");
    }
}
