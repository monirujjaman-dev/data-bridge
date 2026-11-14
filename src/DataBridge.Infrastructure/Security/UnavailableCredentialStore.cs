using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;

namespace DataBridge.Infrastructure.Security;

/// <summary>
/// Used when the platform has no usable secure store. Reports itself unavailable so passwords are kept for
/// the session only (FR-CON-009); every write is refused rather than falling back to plaintext.
/// </summary>
internal sealed class UnavailableCredentialStore(string reason) : ICredentialStore
{
    public string Reason { get; } = reason;

    public Task<CredentialStoreStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CredentialStoreStatus.Unavailable(Reason));
    }

    public Task SaveAsync(string reference, Secret secret, CancellationToken cancellationToken) =>
        throw new CredentialStoreException($"Cannot save the password: {Reason}");

    public Task<Secret?> FindAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Secret?>(null);
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken) =>
        throw new CredentialStoreException($"Cannot delete the password: {Reason}");
}
