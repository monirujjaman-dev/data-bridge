using DataBridge.Application.Abstractions;
using DataBridge.Core.Connections;

namespace DataBridge.Application.Connections;

/// <summary>Lists saved connection profiles ordered by name.</summary>
public sealed class ListConnectionProfiles(IConnectionProfileRepository repository)
{
    public Task<IReadOnlyList<DatabaseConnection>> ExecuteAsync(CancellationToken cancellationToken) =>
        repository.ListAsync(cancellationToken);
}
