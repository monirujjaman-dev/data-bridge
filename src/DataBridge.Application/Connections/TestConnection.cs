using DataBridge.Application.Abstractions;
using DataBridge.Core.Connections;
using DataBridge.Shared.Results;
using Microsoft.Extensions.Logging;

namespace DataBridge.Application.Connections;

/// <summary>
/// Tests a connection and resolves what the server reports about itself (FR-CON-005, FR-CON-008). Works on
/// the profile as currently entered, saved or not, and never saves anything. Read-only on the database.
/// </summary>
public sealed class TestConnection(
    IConnectionProfileRepository repository,
    ProfileSecretStore secrets,
    IDatabaseProviderFactory providers,
    TimeProvider timeProvider,
    ILogger<TestConnection> logger)
{
    /// <param name="profileId">The saved profile being edited, if any; its saved password is used when none is entered.</param>
    /// <param name="input">The profile fields as entered. A password in it takes precedence over a saved one.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    public async Task<Result<ResolvedDatabaseIdentity>> ExecuteAsync(
        Guid? profileId,
        ConnectionProfileInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        DatabaseConnection? existing = null;
        if (profileId is { } id)
        {
            existing = await repository.FindAsync(id, cancellationToken);
            if (existing is null)
            {
                return Result.Failure<ResolvedDatabaseIdentity>(ConnectionProfileErrors.NotFound);
            }
        }

        var now = timeProvider.GetUtcNow();
        var draft = ConnectionProfileValidation.Build(
            existing?.Id ?? Guid.NewGuid(), input, existing?.CredentialReference, now, now);
        if (draft.IsFailure)
        {
            return Result.Failure<ResolvedDatabaseIdentity>(draft.Errors);
        }

        var profile = draft.Value;
        Secret? password = null;
        if (profile.AuthenticationType == AuthenticationType.SqlAuthentication)
        {
            password = input.Password
                ?? (existing is null ? null : await secrets.FindAsync(existing.Id, existing.CredentialReference, cancellationToken));
            if (password is null)
            {
                return Result.Failure<ResolvedDatabaseIdentity>(ConnectionErrors.PasswordRequired);
            }
        }

        var provider = providers.GetProvider(profile.Provider);
        var result = await provider.ResolveIdentityAsync(new DatabaseConnectionRequest(profile, password), cancellationToken);

        if (result.IsSuccess)
        {
            ConnectionProfileLog.ConnectionTestSucceeded(logger, profileId, result.Value.IdentityFingerprint);
        }
        else
        {
            ConnectionProfileLog.ConnectionTestFailed(logger, profileId, result.Errors[0].Code);
        }

        return result;
    }
}
