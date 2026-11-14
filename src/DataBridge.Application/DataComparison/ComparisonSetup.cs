using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;
using DataBridge.Application.SchemaComparison;
using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Schema;
using DataBridge.Shared.Errors;
using DataBridge.Shared.Results;

namespace DataBridge.Application.DataComparison;

/// <summary>Both sides of a comparison, connected and with their schemas read.</summary>
internal sealed record PreparedComparison(
    DatabaseConnectionRequest Source,
    DatabaseConnectionRequest Target,
    ResolvedDatabaseIdentity SourceIdentity,
    ResolvedDatabaseIdentity TargetIdentity,
    DatabaseSchema SourceSchema,
    DatabaseSchema TargetSchema)
{
    /// <summary>Ordinal, unless both databases compare names case-insensitively.</summary>
    public StringComparer Names => SourceSchema.IdentifiersAreCaseSensitive || TargetSchema.IdentifiersAreCaseSensitive
        ? StringComparer.Ordinal
        : StringComparer.OrdinalIgnoreCase;

    public IEnumerable<TableDefinition> SourceTables => SourceSchema.Schemas.SelectMany(schema => schema.Tables);

    public IEnumerable<TableDefinition> TargetTables => TargetSchema.Schemas.SelectMany(schema => schema.Tables);

    /// <summary>Every table on either side, paired by schema and name.</summary>
    public IReadOnlyList<(DatabaseObjectId Id, TableDefinition? Source, TableDefinition? Target)> PairTables()
    {
        var names = Names;
        var targets = TargetTables.ToDictionary(table => (table.Id.SchemaName, table.Id.ObjectName), new NamePairComparer(names));
        var pairs = new List<(DatabaseObjectId, TableDefinition?, TableDefinition?)>();
        foreach (var source in SourceTables)
        {
            targets.Remove((source.Id.SchemaName, source.Id.ObjectName), out var target);
            pairs.Add((source.Id, source, target));
        }

        pairs.AddRange(targets.Values.Select(target => (target.Id, (TableDefinition?)null, (TableDefinition?)target)));
        return pairs;
    }

    private sealed class NamePairComparer(StringComparer names) : IEqualityComparer<(string Schema, string Name)>
    {
        public bool Equals((string Schema, string Name) x, (string Schema, string Name) y) =>
            names.Equals(x.Schema, y.Schema) && names.Equals(x.Name, y.Name);

        public int GetHashCode((string Schema, string Name) obj) => HashCode.Combine(names.GetHashCode(obj.Schema), names.GetHashCode(obj.Name));
    }
}

/// <summary>Connects to both saved profiles, resolves their identities and reads both schemas. Read-only.</summary>
public sealed class ComparisonSetup(SavedConnectionRequests connections, IDatabaseProviderFactory providers)
{
    internal async Task<Result<PreparedComparison>> PrepareAsync(Guid sourceProfileId, Guid targetProfileId, CancellationToken cancellationToken)
    {
        if (sourceProfileId == targetProfileId)
        {
            return Result.Failure<PreparedComparison>(SchemaComparisonErrors.SameProfile);
        }

        var requests = await Task.WhenAll(
            connections.ForProfileAsync(sourceProfileId, cancellationToken),
            connections.ForProfileAsync(targetProfileId, cancellationToken));
        if (Failures(requests[0], requests[1]) is { Length: > 0 } requestErrors)
        {
            return Result.Failure<PreparedComparison>(requestErrors);
        }

        var (source, target) = (requests[0].Value, requests[1].Value);
        var identities = await Task.WhenAll(
            providers.GetProvider(source.Profile.Provider).ResolveIdentityAsync(source, cancellationToken),
            providers.GetProvider(target.Profile.Provider).ResolveIdentityAsync(target, cancellationToken));
        if (Failures(identities[0], identities[1]) is { Length: > 0 } identityErrors)
        {
            return Result.Failure<PreparedComparison>(identityErrors);
        }

        var schemas = await Task.WhenAll(
            providers.GetSchemaReader(source.Profile.Provider).ReadSchemaAsync(source, cancellationToken),
            providers.GetSchemaReader(target.Profile.Provider).ReadSchemaAsync(target, cancellationToken));
        if (Failures(schemas[0], schemas[1]) is { Length: > 0 } schemaErrors)
        {
            return Result.Failure<PreparedComparison>(schemaErrors);
        }

        return Result.Success(new PreparedComparison(
            source, target, identities[0].Value, identities[1].Value, schemas[0].Value, schemas[1].Value));
    }

    private static Error[] Failures(Result source, Result target) =>
        source.Errors.Select(error => SchemaComparisonErrors.OnSide(ComparisonSide.Source, error))
            .Concat(target.Errors.Select(error => SchemaComparisonErrors.OnSide(ComparisonSide.Target, error)))
            .ToArray();
}
