using DataBridge.Core.Connections;
using DataBridge.Core.Schema;
using DataBridge.Shared.Results;

namespace DataBridge.Application.Abstractions;

/// <summary>
/// Reads a database's schema into the provider-independent model (architecture §6). Read-only: implementations
/// only query catalog metadata and never modify the database.
/// </summary>
public interface ISchemaReader
{
    DatabaseProviderType ProviderType { get; }

    /// <summary>The provider's rules for which SQL text differences have no effect on meaning.</summary>
    ISqlDefinitionNormalizer DefinitionNormalizer { get; }

    /// <summary>
    /// Connects and reads every supported object, and lists the objects it found but cannot compare
    /// (FR-SCH-010).
    /// </summary>
    /// <returns>
    /// The schema, or a failure with an actionable <c>ConnectionErrors</c> or <c>SchemaComparisonErrors</c> error.
    /// Unexpected failures throw.
    /// </returns>
    Task<Result<DatabaseSchema>> ReadSchemaAsync(DatabaseConnectionRequest request, CancellationToken cancellationToken);
}
