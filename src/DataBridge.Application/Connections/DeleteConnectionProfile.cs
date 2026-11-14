using DataBridge.Application.Abstractions;
using DataBridge.Core.Connections;
using DataBridge.Shared.Results;

namespace DataBridge.Application.Connections;

/// <summary>The outcome of deleting a profile.</summary>
/// <param name="Warning">
/// A user-facing warning when the saved password could not be removed from the OS credential store;
/// otherwise <see langword="null"/>.
/// </param>
public sealed record ConnectionProfileDeleted(string? Warning);

/// <summary>Deletes a connection profile and its saved password (FR-CON-006).</summary>
public sealed class DeleteConnectionProfile(IConnectionProfileRepository repository, ProfileSecretStore secrets)
{
    public async Task<Result<ConnectionProfileDeleted>> ExecuteAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var existing = await repository.FindAsync(profileId, cancellationToken);
        if (existing is null)
        {
            return Result.Failure<ConnectionProfileDeleted>(ConnectionProfileErrors.NotFound);
        }

        // Remove the secret first: if deleting the profile then fails, the profile merely asks for its
        // password again, whereas the reverse order could leave an orphaned password behind.
        var warning = await secrets.RemoveAsync(profileId, existing.CredentialReference);

        return await repository.DeleteAsync(profileId, cancellationToken)
            ? Result.Success(new ConnectionProfileDeleted(warning))
            : Result.Failure<ConnectionProfileDeleted>(ConnectionProfileErrors.NotFound);
    }
}
