using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataBridge.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataBridge.Desktop.ViewModels;

/// <summary>An entry in the navigation rail.</summary>
/// <param name="Title">The rail text.</param>
/// <param name="Page">The page view model it shows.</param>
public sealed record NavigationItem(string Title, ViewModelBase Page);

/// <summary>
/// The application shell: navigation rail and current page. Only screens whose use cases exist are listed
/// (development plan §1); the others arrive with their phases.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly ILocalDataStore _localDataStore;
    private readonly ConnectionsViewModel _connections;
    private readonly ILogger<MainWindowViewModel> _logger;

    public MainWindowViewModel(
        ILocalDataStore localDataStore,
        ConnectionsViewModel connections,
        ILogger<MainWindowViewModel> logger)
    {
        _localDataStore = localDataStore;
        _connections = connections;
        _logger = logger;
        NavigationItems = [new NavigationItem("Connections", connections)];
        SelectedNavigationItem = NavigationItems[0];
    }

    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    public partial NavigationItem? SelectedNavigationItem { get; set; }

    public ViewModelBase? CurrentPage => IsReady ? SelectedNavigationItem?.Page : null;

    /// <summary>Whether startup finished and pages can be shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    public partial bool IsReady { get; private set; }

    /// <summary>Why the app could not start, for example a database written by a newer version.</summary>
    [ObservableProperty]
    public partial string? StartupError { get; private set; }

    /// <summary>Prepares local storage off the UI thread, then loads the first page.</summary>
    [RelayCommand]
    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunInBackgroundAsync(_localDataStore.InitializeAsync, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (InvalidOperationException exception)
        {
            // Thrown for a database from a newer DataBridge version; its message says what to do.
            DesktopLog.StartupFailed(_logger, exception);
            StartupError = exception.Message;
            return;
        }
#pragma warning disable CA1031 // Startup failures are shown and logged instead of crashing the window.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            DesktopLog.StartupFailed(_logger, exception);
            StartupError = "DataBridge could not open its local data. The details are in the log file.";
            return;
        }

        DesktopLog.Started(_logger);
        IsReady = true;
        await _connections.LoadCommand.ExecuteAsync(null);
    }
}
