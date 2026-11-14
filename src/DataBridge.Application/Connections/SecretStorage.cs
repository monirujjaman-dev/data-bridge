namespace DataBridge.Application.Connections;

/// <summary>What happened to a profile's password when the profile was saved.</summary>
public enum SecretStorageKind
{
    /// <summary>The profile has no password (integrated authentication, or none entered).</summary>
    None,

    /// <summary>No new password was entered; the existing one, if any, is kept.</summary>
    Unchanged,

    /// <summary>The password was saved in the OS credential store.</summary>
    StoredSecurely,

    /// <summary>
    /// The OS credential store is unavailable, so the password is kept in memory for this session only
    /// and the user will be asked again next time.
    /// </summary>
    SessionOnly,

    /// <summary>A previously saved password was removed, for example after switching to integrated authentication.</summary>
    Removed,
}

/// <summary>The outcome for a profile's password, with a user-facing reason when it is session-only.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Reason">Why the password could not be saved securely; set only for <see cref="SecretStorageKind.SessionOnly"/>.</param>
public sealed record SecretStorage(SecretStorageKind Kind, string? Reason = null)
{
    public static SecretStorage None { get; } = new(SecretStorageKind.None);

    public static SecretStorage Unchanged { get; } = new(SecretStorageKind.Unchanged);

    public static SecretStorage StoredSecurely { get; } = new(SecretStorageKind.StoredSecurely);

    public static SecretStorage Removed { get; } = new(SecretStorageKind.Removed);

    public static SecretStorage SessionOnly(string reason) => new(SecretStorageKind.SessionOnly, reason);
}
