using DataBridge.Application.Abstractions;
using DataBridge.Core.Connections;
using DataBridge.Shared.Results;

namespace DataBridge.Application.Connections;

/// <summary>Builds a connection request for a saved profile, with its saved or session password.</summary>
public sealed class SavedConnectionRequests(IConnectionProfileRepository repository, ProfileSecretStore secrets)
{
    /// <returns>
    /// The request, or <see cref="ConnectionProfileErrors.NotFound"/> or <see cref="ConnectionErrors.PasswordRequired"/>.
    /// </returns>
    public async Task<Result<DatabaseConnectionRequest>> ForProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var profile = await repository.FindAsync(profileId, cancellationToken);
        if (profile is null)
        {
            return Result.Failure<DatabaseConnectionRequest>(ConnectionProfileErrors.NotFound);
        }

        if (profile.AuthenticationType != AuthenticationType.SqlAuthentication)
        {
            return Result.Success(new DatabaseConnectionRequest(profile, null));
        }

        var password = await secrets.FindAsync(profile.Id, profile.CredentialReference, cancellationToken);
        return password is null
            ? Result.Failure<DatabaseConnectionRequest>(ConnectionErrors.PasswordRequired)
            : Result.Success(new DatabaseConnectionRequest(profile, password));
    }
}
