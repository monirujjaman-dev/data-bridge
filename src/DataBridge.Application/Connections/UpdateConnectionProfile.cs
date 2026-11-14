using DataBridge.Application.Abstractions;
using DataBridge.Core.Connections;
using DataBridge.Shared.Errors;
using DataBridge.Shared.Results;
using Microsoft.Extensions.Logging;

namespace DataBridge.Application.Connections;

/// <summary>
/// Edits a connection profile (FR-CON-006). A new password replaces the saved one; no password keeps it;
/// switching to integrated authentication removes it.
/// </summary>
/// <remarks>
/// The previous password is removed only after the profile row has been written, so a failed or rejected
/// update leaves both the profile and its saved password as they were.
/// </remarks>
public sealed class UpdateConnectionProfile(
    IConnectionProfileRepository repository,
    ProfileSecretStore secrets,
    TimeProvider timeProvider,
    ILogger<UpdateConnectionProfile> logger)
{
    public async Task<Result<ConnectionProfileSaved>> ExecuteAsync(
        Guid profileId,
        ConnectionProfileInput input,
        CancellationToken cancellationToken)
    {
        var result = await SaveAsync(profileId, input, cancellationToken);
        if (result.IsSuccess)
        {
            ConnectionProfileLog.ProfileUpdated(logger, profileId, result.Value.SecretStorage.Kind);
        }
        else if (logger.IsEnabled(LogLevel.Information))
        {
            var errorCodes = string.Join(", ", result.Errors.Select(error => error.Code));
            ConnectionProfileLog.ProfileRejected(logger, profileId, errorCodes);
        }

        return result;
    }

    private async Task<Result<ConnectionProfileSaved>> SaveAsync(
        Guid profileId,
        ConnectionProfileInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var existing = await repository.FindAsync(profileId, cancellationToken);
        if (existing is null)
        {
            return Result.Failure<ConnectionProfileSaved>(ConnectionProfileErrors.NotFound);
        }

        var now = ProfileWrites.NextUpdateTime(timeProvider, existing);
        var draft = ConnectionProfileValidation.Build(
            existing.Id, input, existing.CredentialReference, existing.CreatedAtUtc, now);
        if (draft.IsFailure)
        {
            return Result.Failure<ConnectionProfileSaved>(draft.Errors);
        }

        var profile = draft.Value;
        if (await repository.NameExistsAsync(profile.Name, profile.Id, cancellationToken))
        {
            return Result.Failure<ConnectionProfileSaved>(ConnectionProfileErrors.NameAlreadyExists);
        }

        PendingSecret? pending = null;
        if (profile.AuthenticationType != AuthenticationType.Integrated && input.Password is not null)
        {
            pending = await secrets.SaveAsync(profile.Id, input.Password, cancellationToken);
            profile = profile.WithCredentialReference(pending.CredentialReference, now).Value;
        }

        Error? error;
        try
        {
            error = await ProfileWrites.UpdateAsync(repository, profile, existing, cancellationToken);
        }
        catch
        {
            await UndoAsync(pending);
            throw;
        }

        if (error is not null)
        {
            await UndoAsync(pending);
            return Result.Failure<ConnectionProfileSaved>(error);
        }

        // The profile is saved; from here on clean up only, without cancellation.
        if (pending is not null)
        {
            var warning = await secrets.CommitAsync(pending, existing.CredentialReference);
            return Result.Success(new ConnectionProfileSaved(profile, pending.Storage, warning));
        }

        if (profile.AuthenticationType == AuthenticationType.Integrated)
        {
            if (existing.AuthenticationType == AuthenticationType.Integrated)
            {
                return Result.Success(new ConnectionProfileSaved(profile, SecretStorage.None));
            }

            var warning = await secrets.RemoveAsync(profile.Id, existing.CredentialReference);
            return Result.Success(new ConnectionProfileSaved(profile, SecretStorage.Removed, warning));
        }

        return Result.Success(new ConnectionProfileSaved(profile, SecretStorage.Unchanged));
    }

    private Task UndoAsync(PendingSecret? pending) =>
        pending is null ? Task.CompletedTask : secrets.UndoAsync(pending);
}
