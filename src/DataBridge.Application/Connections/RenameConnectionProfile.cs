using DataBridge.Application.Abstractions;
using DataBridge.Core.Connections;
using DataBridge.Shared.Results;
using Microsoft.Extensions.Logging;

namespace DataBridge.Application.Connections;

/// <summary>Renames a connection profile (FR-CON-006). The password is untouched.</summary>
public sealed class RenameConnectionProfile(
    IConnectionProfileRepository repository,
    TimeProvider timeProvider,
    ILogger<RenameConnectionProfile> logger)
{
    public async Task<Result<DatabaseConnection>> ExecuteAsync(
        Guid profileId,
        string? newName,
        CancellationToken cancellationToken)
    {
        var existing = await repository.FindAsync(profileId, cancellationToken);
        if (existing is null)
        {
            return Result.Failure<DatabaseConnection>(ConnectionProfileErrors.NotFound);
        }

        var renamed = existing.Rename(newName, ProfileWrites.NextUpdateTime(timeProvider, existing));
        if (renamed.IsFailure)
        {
            return renamed;
        }

        if (await repository.NameExistsAsync(renamed.Value.Name, profileId, cancellationToken))
        {
            return Result.Failure<DatabaseConnection>(ConnectionProfileErrors.NameAlreadyExists);
        }

        var error = await ProfileWrites.UpdateAsync(repository, renamed.Value, existing, cancellationToken);
        if (error is not null)
        {
            ConnectionProfileLog.ProfileRejected(logger, profileId, error.Code);
            return Result.Failure<DatabaseConnection>(error);
        }

        ConnectionProfileLog.ProfileRenamed(logger, profileId);
        return renamed;
    }
}
