using DataBridge.Core.Connections;

namespace DataBridge.Application.Abstractions;

/// <summary>
/// Persists connection profile metadata. Profiles hold no secrets, so implementations never see a password.
/// </summary>
public interface IConnectionProfileRepository
{
    /// <summary>Returns every profile ordered by name.</summary>
    Task<IReadOnlyList<DatabaseConnection>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Returns the profile with <paramref name="id"/>, or <see langword="null"/> when there is none.</summary>
    Task<DatabaseConnection?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Whether a profile other than <paramref name="excludingId"/> has <paramref name="name"/>.
    /// Names are compared case-insensitively.
    /// </summary>
    Task<bool> NameExistsAsync(string name, Guid? excludingId, CancellationToken cancellationToken);

    /// <summary>Stores a new profile.</summary>
    /// <exception cref="DuplicateConnectionProfileNameException">Another profile already has the name.</exception>
    Task AddAsync(DatabaseConnection profile, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a stored profile, but only if it is unchanged since it was read: its stored
    /// <see cref="DatabaseConnection.UpdatedAtUtc"/> must equal <paramref name="expectedUpdatedAtUtc"/>.
    /// Returns <see langword="false"/> when the profile no longer exists or was changed in the meantime.
    /// </summary>
    /// <exception cref="DuplicateConnectionProfileNameException">Another profile already has the name.</exception>
    Task<bool> UpdateAsync(
        DatabaseConnection profile,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>Deletes a profile. Returns <see langword="false"/> when it did not exist.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>
/// A write was rejected because another profile has the same name, for example when two windows saved
/// the same name at once. Use cases report it as <see cref="ConnectionProfileErrors.NameAlreadyExists"/>.
/// </summary>
public sealed class DuplicateConnectionProfileNameException : Exception
{
    public DuplicateConnectionProfileNameException()
        : base("Another connection profile already has this name.")
    {
    }

    public DuplicateConnectionProfileNameException(string message)
        : base(message)
    {
    }

    public DuplicateConnectionProfileNameException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
