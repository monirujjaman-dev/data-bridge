using System.Collections.Concurrent;

namespace DataBridge.Application.Connections;

/// <summary>
/// Holds profile passwords in memory for the current application session only. Used when the OS
/// credential store is unavailable (FR-CON-009); nothing here is ever written to disk.
/// </summary>
public sealed class SessionSecretCache
{
    private readonly ConcurrentDictionary<Guid, Secret> _secrets = new();

    public void Set(Guid profileId, Secret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        _secrets[profileId] = secret;
    }

    public Secret? Find(Guid profileId) => _secrets.TryGetValue(profileId, out var secret) ? secret : null;

    public bool Remove(Guid profileId) => _secrets.TryRemove(profileId, out _);
}
