using DataBridge.Application.Connections;
using DataBridge.Core.Comparison;
using DataBridge.Shared.Errors;

namespace DataBridge.Application.SchemaComparison;

/// <summary>Schema comparison failures. Messages never contain passwords, connection strings or row values.</summary>
public static class SchemaComparisonErrors
{
    public static readonly Error SameProfile =
        new("SchemaComparison.SameProfile", "Choose two different connection profiles for the source and the target.");

    /// <summary>The login cannot read the catalog at all, for example because it lacks <c>CONNECT</c> on the database.</summary>
    public static Error MetadataNotReadable(int number, string message) =>
        new("SchemaComparison.MetadataNotReadable", $"The schema could not be read (server error {number}): {message}");

    /// <summary>The metadata changed while it was being read, so it would not be consistent.</summary>
    public static readonly Error MetadataChanged =
        new("SchemaComparison.MetadataChanged", "The schema changed while it was being read. Run the comparison again.");

    /// <summary>Prefixes an error with the side it happened on, keeping its code.</summary>
    public static Error OnSide(ComparisonSide side, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var message = error.Code == ConnectionErrors.PasswordRequired.Code
            ? "No password is saved for this profile for this session. Open it in Connections, enter the password and save it."
            : error.Message;
        return error with { Message = $"{side}: {message}" };
    }
}
