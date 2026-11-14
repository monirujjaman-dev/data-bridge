using CommunityToolkit.Mvvm.ComponentModel;

namespace DataBridge.Desktop.ViewModels;

/// <summary>Base class for view models: property change notification only, no UI types.</summary>
public abstract class ViewModelBase : ObservableObject
{
    /// <summary>
    /// Runs Application work on the thread pool and resumes on the caller's context. Some ports (such as
    /// SQLite) complete synchronously despite an async signature, so awaiting them directly could block the
    /// UI thread (product rule 13).
    /// </summary>
    protected static Task<T> RunInBackgroundAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
        Task.Run(() => work(cancellationToken), cancellationToken);

    /// <inheritdoc cref="RunInBackgroundAsync{T}(Func{CancellationToken, Task{T}}, CancellationToken)"/>
    protected static Task RunInBackgroundAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken) =>
        Task.Run(() => work(cancellationToken), cancellationToken);
}
