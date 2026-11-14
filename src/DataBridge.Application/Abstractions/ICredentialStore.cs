using DataBridge.Application.Connections;

namespace DataBridge.Application.Abstractions;

/// <summary>
/// The OS-native secure credential store (Windows Credential Manager, macOS Keychain, Linux Secret Service).
/// There is deliberately no plaintext or obfuscated fallback implementation (FR-CON-009).
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Whether the store can be used now. Callers check this before saving; when it is unavailable the
    /// secret is kept for the session only and the user is told why.
    /// </summary>
    Task<CredentialStoreStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Creates or replaces the secret stored under <paramref name="reference"/>.</summary>
    Task SaveAsync(string reference, Secret secret, CancellationToken cancellationToken);

    /// <summary>Returns the secret stored under <paramref name="reference"/>, or <see langword="null"/> when there is none.</summary>
    Task<Secret?> FindAsync(string reference, CancellationToken cancellationToken);

    /// <summary>Deletes the secret under <paramref name="reference"/>. Deleting a missing secret is not an error.</summary>
    Task DeleteAsync(string reference, CancellationToken cancellationToken);
}

/// <summary>Whether the OS credential store is usable, and if not, a user-facing reason.</summary>
/// <param name="IsAvailable">Whether secrets can be saved.</param>
/// <param name="UnavailableReason">Why the store is unavailable; <see langword="null"/> when available.</param>
public sealed record CredentialStoreStatus(bool IsAvailable, string? UnavailableReason)
{
    public static CredentialStoreStatus Available { get; } = new(true, null);

    public static CredentialStoreStatus Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new CredentialStoreStatus(false, reason);
    }
}
