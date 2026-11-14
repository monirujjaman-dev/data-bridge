using DataBridge.Application.Abstractions;
using DataBridge.Core.Connections;
using DataBridge.Shared.Errors;

namespace DataBridge.Application.Connections;

/// <summary>Optimistic-concurrency helpers shared by the profile use cases that modify a stored profile.</summary>
internal static class ProfileWrites
{
    /// <summary>
    /// The update time for a change to <paramref name="existing"/>: now, but always strictly later than the
    /// stored time, so the concurrency check sees every change even with a coarse or skewed clock.
    /// </summary>
    public static DateTimeOffset NextUpdateTime(TimeProvider timeProvider, DatabaseConnection existing)
    {
        var now = timeProvider.GetUtcNow();
        return now > existing.UpdatedAtUtc ? now : existing.UpdatedAtUtc.AddTicks(1);
    }

    /// <summary>
    /// Writes <paramref name="profile"/> if the stored copy still matches <paramref name="existing"/>.
    /// Returns <see langword="null"/> on success, or the error that explains why nothing was written.
    /// </summary>
    public static async Task<Error?> UpdateAsync(
        IConnectionProfileRepository repository,
        DatabaseConnection profile,
        DatabaseConnection existing,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await repository.UpdateAsync(profile, existing.UpdatedAtUtc, cancellationToken))
            {
                return null;
            }
        }
        catch (DuplicateConnectionProfileNameException)
        {
            return ConnectionProfileErrors.NameAlreadyExists;
        }

        return await repository.FindAsync(profile.Id, cancellationToken) is null
            ? ConnectionProfileErrors.NotFound
            : ConnectionProfileErrors.ModifiedConcurrently;
    }
}
