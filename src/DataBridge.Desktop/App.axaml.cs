using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DataBridge.Desktop.ViewModels;
using DataBridge.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DataBridge.Desktop;

public sealed partial class App : Avalonia.Application
{
    /// <summary>
    /// The application services, set by <see cref="Program"/> before Avalonia starts. <see langword="null"/>
    /// in the XAML previewer, which then shows the window without data.
    /// </summary>
    public IServiceProvider? Services { get; init; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services?.GetRequiredService<MainWindowViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
