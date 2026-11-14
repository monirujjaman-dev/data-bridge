using DataBridge.Application.Abstractions;
using DataBridge.Core.Connections;
using DataBridge.Shared.Results;

namespace DataBridge.Application.Connections;

/// <summary>The saved profile and what happened to its password.</summary>
/// <param name="Profile">The profile as stored.</param>
/// <param name="SecretStorage">Where the password went.</param>
/// <param name="Warning">
/// A user-facing warning when a superseded password could not be removed from the OS credential store;
/// otherwise <see langword="null"/>. The profile itself was saved.
/// </param>
public sealed record ConnectionProfileSaved(DatabaseConnection Profile, SecretStorage SecretStorage, string? Warning = null);

/// <summary>Creates a connection profile (FR-CON-006) and stores its password securely (FR-CON-007, FR-CON-009).</summary>
public sealed class CreateConnectionProfile(
    IConnectionProfileRepository repository,
    ProfileSecretStore secrets,
    TimeProvider timeProvider)
{
    public async Task<Result<ConnectionProfileSaved>> ExecuteAsync(
        ConnectionProfileInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var now = timeProvider.GetUtcNow();
        var draft = ConnectionProfileValidation.Build(Guid.NewGuid(), input, credentialReference: null, now, now);
        if (draft.IsFailure)
        {
            return Result.Failure<ConnectionProfileSaved>(draft.Errors);
        }

        var profile = draft.Value;
        if (await repository.NameExistsAsync(profile.Name, excludingId: null, cancellationToken))
        {
            return Result.Failure<ConnectionProfileSaved>(ConnectionProfileErrors.NameAlreadyExists);
        }

        PendingSecret? pending = null;
        if (input.Password is not null)
        {
            pending = await secrets.SaveAsync(profile.Id, input.Password, cancellationToken);
            profile = profile.WithCredentialReference(pending.CredentialReference, now).Value;
        }

        try
        {
            await repository.AddAsync(profile, cancellationToken);
        }
        catch (DuplicateConnectionProfileNameException)
        {
            await UndoAsync(pending);
            return Result.Failure<ConnectionProfileSaved>(ConnectionProfileErrors.NameAlreadyExists);
        }
        catch
        {
            await UndoAsync(pending);
            throw;
        }

        if (pending is null)
        {
            return Result.Success(new ConnectionProfileSaved(profile, SecretStorage.None));
        }

        var warning = await secrets.CommitAsync(pending, previousReference: null);
        return Result.Success(new ConnectionProfileSaved(profile, pending.Storage, warning));
    }

    private Task UndoAsync(PendingSecret? pending) =>
        pending is null ? Task.CompletedTask : secrets.UndoAsync(pending);
}
