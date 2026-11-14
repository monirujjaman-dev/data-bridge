using DataBridge.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataBridge.Application.Connections;

/// <summary>A password written by <see cref="ProfileSecretStore.SaveAsync"/> but not yet committed.</summary>
/// <param name="ProfileId">The profile the password belongs to.</param>
/// <param name="CredentialReference">
/// The new credential store entry; <see langword="null"/> when the password is kept for the session only.
/// </param>
/// <param name="Storage">Where the password went.</param>
/// <param name="PreviousSessionSecret">The session-only password this save replaced, restored on undo.</param>
public sealed record PendingSecret(
    Guid ProfileId,
    string? CredentialReference,
    SecretStorage Storage,
    Secret? PreviousSessionSecret);

/// <summary>
/// Decides where a profile's password goes: the OS credential store when it is available, otherwise
/// the in-memory <see cref="SessionSecretCache"/>. Never writes a secret anywhere else.
/// </summary>
/// <remarks>
/// Saving is two-phase so that a failed profile write never loses the previous password: every save
/// writes a new store entry (<see cref="SaveAsync"/>); once the profile row points at it,
/// <see cref="CommitAsync"/> deletes the old entry, and if the row could not be written,
/// <see cref="UndoAsync"/> deletes the new one.
/// </remarks>
public sealed class ProfileSecretStore(
    ICredentialStore credentialStore,
    SessionSecretCache sessionSecrets,
    ILogger<ProfileSecretStore> logger)
{
    /// <summary>The prefix of every credential store entry DataBridge creates.</summary>
    public const string ReferencePrefix = "DataBridge/connection-profile/";

    /// <summary>
    /// A new credential store key for a profile. Contains only the profile id and a random suffix, never
    /// user data; the suffix makes every saved password a separate entry.
    /// </summary>
    public static string NewReferenceFor(Guid profileId) =>
        $"{ReferencePrefix}{profileId:D}/{Guid.NewGuid():N}";

    /// <summary>Whether <paramref name="reference"/> is a store entry of <paramref name="profileId"/>.</summary>
    public static bool BelongsTo(string reference, Guid profileId) =>
        reference.StartsWith($"{ReferencePrefix}{profileId:D}/", StringComparison.Ordinal);

    /// <summary>
    /// Writes <paramref name="password"/> to a new store entry, or to the session cache when the store is
    /// unavailable. Follow with <see cref="CommitAsync"/> or <see cref="UndoAsync"/>.
    /// </summary>
    public async Task<PendingSecret> SaveAsync(Guid profileId, Secret password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);

        var status = await credentialStore.GetStatusAsync(cancellationToken);
        if (!status.IsAvailable)
        {
            var previous = sessionSecrets.Find(profileId);
            sessionSecrets.Set(profileId, password);
            ConnectionProfileLog.PasswordKeptForSession(logger, profileId, status.UnavailableReason!);
            return new PendingSecret(profileId, null, SecretStorage.SessionOnly(status.UnavailableReason!), previous);
        }

        var reference = NewReferenceFor(profileId);
        await credentialStore.SaveAsync(reference, password, cancellationToken);
        return new PendingSecret(profileId, reference, SecretStorage.StoredSecurely, null);
    }

    /// <summary>
    /// Called after the profile row points at the pending password: drops the superseded password.
    /// Not cancellable, because the profile change is already saved. Returns a user-facing warning when the
    /// previous stored password could not be removed; otherwise <see langword="null"/>.
    /// </summary>
    public async Task<string?> CommitAsync(PendingSecret pending, string? previousReference)
    {
        ArgumentNullException.ThrowIfNull(pending);

        if (pending.CredentialReference is not null)
        {
            sessionSecrets.Remove(pending.ProfileId);
        }

        if (previousReference is null || previousReference == pending.CredentialReference)
        {
            return null;
        }

        return await TryDeleteAsync(previousReference);
    }

    /// <summary>Called when the profile row could not be written: removes the pending password. Best effort.</summary>
    public async Task UndoAsync(PendingSecret pending)
    {
        ArgumentNullException.ThrowIfNull(pending);

        if (pending.CredentialReference is null)
        {
            if (pending.PreviousSessionSecret is { } previous)
            {
                sessionSecrets.Set(pending.ProfileId, previous);
            }
            else
            {
                sessionSecrets.Remove(pending.ProfileId);
            }

            return;
        }

        await TryDeleteAsync(pending.CredentialReference);
    }

    /// <summary>
    /// Removes a profile's password from the session cache and, when <paramref name="credentialReference"/>
    /// is set, from the credential store. Not cancellable: callers use it once the decision to remove is
    /// made. Returns a user-facing warning when the stored password could not be removed; otherwise
    /// <see langword="null"/>.
    /// </summary>
    public async Task<string?> RemoveAsync(Guid profileId, string? credentialReference)
    {
        sessionSecrets.Remove(profileId);
        return credentialReference is null ? null : await TryDeleteAsync(credentialReference);
    }

    private async Task<string?> TryDeleteAsync(string reference)
    {
        try
        {
            var status = await credentialStore.GetStatusAsync(CancellationToken.None);
            if (!status.IsAvailable)
            {
                ConnectionProfileLog.PasswordNotRemoved(logger, null, reference, status.UnavailableReason!);
                return NotRemovedWarning(reference, status.UnavailableReason!);
            }

            await credentialStore.DeleteAsync(reference, CancellationToken.None);
            return null;
        }
#pragma warning disable CA1031 // The profile change is already saved; a cleanup failure becomes a warning.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            ConnectionProfileLog.PasswordNotRemoved(logger, exception, reference, "the credential store reported an error");
            return NotRemovedWarning(reference, "The OS credential store reported an error.");
        }
    }

    private static string NotRemovedWarning(string reference, string reason) =>
        $"A saved password could not be removed from the OS credential store. {reason} "
        + $"Remove the entry \"{reference}\" manually.";
}
