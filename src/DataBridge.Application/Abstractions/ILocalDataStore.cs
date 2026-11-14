namespace DataBridge.Application.Abstractions;

/// <summary>The local application data store (profiles, history, preferences); never holds secrets.</summary>
public interface ILocalDataStore
{
    /// <summary>
    /// Creates the store or upgrades it to the current schema. Call once at startup, before any repository
    /// is used.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The store was written by a newer DataBridge version and cannot be opened by this one.
    /// </exception>
    Task InitializeAsync(CancellationToken cancellationToken);
}
