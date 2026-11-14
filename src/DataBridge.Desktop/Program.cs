using Avalonia;

namespace DataBridge.Desktop;

internal static class Program
{
    // Avalonia is not ready until AppMain runs: do not touch Avalonia types or
    // SynchronizationContext-dependent code before BuildAvaloniaApp is called.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
