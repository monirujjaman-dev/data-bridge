using DataBridge.Core.Connections;
using DataBridge.Shared.Errors;
using DataBridge.Shared.Results;

namespace DataBridge.Application.Connections;

/// <summary>Builds a validated <see cref="DatabaseConnection"/> from user input.</summary>
internal static class ConnectionProfileValidation
{
    public static Result<DatabaseConnection> Build(
        Guid id,
        ConnectionProfileInput input,
        string? credentialReference,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        var integrated = input.AuthenticationType == AuthenticationType.Integrated;
        var profile = DatabaseConnection.Create(
            id,
            input.Name,
            DatabaseProviderType.SqlServer,
            input.Server,
            input.DatabaseName,
            input.AuthenticationType,
            input.UserName,
            integrated ? null : credentialReference,
            input.Settings,
            createdAtUtc,
            updatedAtUtc,
            input.Environment);

        if (!integrated || input.Password is null)
        {
            return profile;
        }

        var errors = new List<Error>(profile.Errors) { ConnectionProfileErrors.PasswordNotAllowed };
        return Result.Failure<DatabaseConnection>(errors);
    }
}
