using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;
using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Shared.Errors;
using DataBridge.Shared.Results;
using Microsoft.Extensions.Logging;

namespace DataBridge.Application.SchemaComparison;

/// <summary>The outcome of a schema comparison: who was compared, when, and the differences.</summary>
/// <param name="SessionId">Identifies this comparison in logs.</param>
/// <param name="Source">What the source server reported about itself.</param>
/// <param name="Target">What the target server reported about itself.</param>
/// <param name="Result">The differences and unsupported objects.</param>
/// <param name="SourceHasCompleteMetadata">Whether the source login could see every object.</param>
/// <param name="TargetHasCompleteMetadata">Whether the target login could see every object.</param>
/// <param name="StartedAtUtc">When the comparison started.</param>
/// <param name="CompletedAtUtc">When it finished.</param>
public sealed record SchemaComparisonReport(
    Guid SessionId,
    ResolvedDatabaseIdentity Source,
    ResolvedDatabaseIdentity Target,
    SchemaComparisonResult Result,
    bool SourceHasCompleteMetadata,
    bool TargetHasCompleteMetadata,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc)
{
    /// <summary>Whether both profiles resolved to the same database, which makes the comparison meaningless.</summary>
    public bool IsSameDatabase => Source.IsSameDatabaseAs(Target);
}

/// <summary>
/// Compares the schemas of two saved profiles (FR-SCH-001 to FR-SCH-012). Read-only on both databases: it resolves
/// each identity, reads catalog metadata and compares the models in memory. Nothing is persisted.
/// </summary>
public sealed class CompareSchemas(
    SavedConnectionRequests connections,
    IDatabaseProviderFactory providers,
    TimeProvider timeProvider,
    ILogger<CompareSchemas> logger)
{
    /// <param name="sourceProfileId">The profile to compare from.</param>
    /// <param name="targetProfileId">The profile to compare with.</param>
    /// <param name="progress">Receives <see cref="ComparisonStatus.Connecting"/>, <see cref="ComparisonStatus.ReadingMetadata"/> and <see cref="ComparisonStatus.ComparingSchema"/>.</param>
    /// <param name="cancellationToken">Cancels the comparison; nothing is left behind on either database.</param>
    public async Task<Result<SchemaComparisonReport>> ExecuteAsync(
        Guid sourceProfileId,
        Guid targetProfileId,
        IProgress<ComparisonStatus>? progress,
        CancellationToken cancellationToken)
    {
        if (sourceProfileId == targetProfileId)
        {
            return Result.Failure<SchemaComparisonReport>(SchemaComparisonErrors.SameProfile);
        }

        var sessionId = Guid.NewGuid();
        var startedAtUtc = timeProvider.GetUtcNow();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["SessionId"] = sessionId });
        SchemaComparisonLog.Started(logger, sourceProfileId, targetProfileId);

        try
        {
            var result = await CompareAsync(sessionId, startedAtUtc, sourceProfileId, targetProfileId, progress, cancellationToken);
            LogOutcome(result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SchemaComparisonLog.Cancelled(logger);
            throw;
        }
    }

    private void LogOutcome(Result<SchemaComparisonReport> result)
    {
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        if (result.IsFailure)
        {
            var codes = string.Join(",", result.Errors.Select(error => error.Code));
            SchemaComparisonLog.Failed(logger, codes);
            return;
        }

        var report = result.Value;
        var differences = report.Result;
        var objectCount = differences.Differences.Count;
        var notEqualCount = objectCount - differences.Count(DifferenceStatus.Equal);
        var unsupportedCount = differences.SourceUnsupportedObjects.Count + differences.TargetUnsupportedObjects.Count;
        var durationMs = (report.CompletedAtUtc - report.StartedAtUtc).TotalMilliseconds;
        SchemaComparisonLog.Completed(logger, objectCount, notEqualCount, unsupportedCount, durationMs);
    }

    private async Task<Result<SchemaComparisonReport>> CompareAsync(
        Guid sessionId,
        DateTimeOffset startedAtUtc,
        Guid sourceProfileId,
        Guid targetProfileId,
        IProgress<ComparisonStatus>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(ComparisonStatus.Connecting);
        var requests = await Task.WhenAll(
            connections.ForProfileAsync(sourceProfileId, cancellationToken),
            connections.ForProfileAsync(targetProfileId, cancellationToken));
        if (Failures(requests[0], requests[1]) is { Length: > 0 } requestErrors)
        {
            return Result.Failure<SchemaComparisonReport>(requestErrors);
        }

        var (source, target) = (requests[0].Value, requests[1].Value);
        var identities = await Task.WhenAll(
            providers.GetProvider(source.Profile.Provider).ResolveIdentityAsync(source, cancellationToken),
            providers.GetProvider(target.Profile.Provider).ResolveIdentityAsync(target, cancellationToken));
        if (Failures(identities[0], identities[1]) is { Length: > 0 } identityErrors)
        {
            return Result.Failure<SchemaComparisonReport>(identityErrors);
        }

        progress?.Report(ComparisonStatus.ReadingMetadata);
        var sourceReader = providers.GetSchemaReader(source.Profile.Provider);
        var schemas = await Task.WhenAll(
            sourceReader.ReadSchemaAsync(source, cancellationToken),
            providers.GetSchemaReader(target.Profile.Provider).ReadSchemaAsync(target, cancellationToken));
        if (Failures(schemas[0], schemas[1]) is { Length: > 0 } schemaErrors)
        {
            return Result.Failure<SchemaComparisonReport>(schemaErrors);
        }

        progress?.Report(ComparisonStatus.ComparingSchema);
        var (sourceSchema, targetSchema) = (schemas[0].Value, schemas[1].Value);
        var differences = new SchemaComparer(sourceReader.DefinitionNormalizer).Compare(sourceSchema, targetSchema, cancellationToken);

        return Result.Success(new SchemaComparisonReport(
            sessionId,
            identities[0].Value,
            identities[1].Value,
            differences,
            sourceSchema.HasCompleteMetadata,
            targetSchema.HasCompleteMetadata,
            startedAtUtc,
            timeProvider.GetUtcNow()));
    }

    private static Error[] Failures(Result source, Result target) =>
        source.Errors.Select(error => SchemaComparisonErrors.OnSide(ComparisonSide.Source, error))
            .Concat(target.Errors.Select(error => SchemaComparisonErrors.OnSide(ComparisonSide.Target, error)))
            .ToArray();
}

/// <summary>Schema comparison log events: ids, counts and durations only, never names or definitions.</summary>
internal static partial class SchemaComparisonLog
{
    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Schema comparison started: source profile {SourceProfileId}, target profile {TargetProfileId}")]
    public static partial void Started(ILogger logger, Guid sourceProfileId, Guid targetProfileId);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Schema comparison completed: {ObjectCount} objects, {DifferenceCount} not equal, {UnsupportedCount} unsupported, {DurationMs} ms")]
    public static partial void Completed(ILogger logger, int objectCount, int differenceCount, int unsupportedCount, double durationMs);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Schema comparison failed: {ErrorCodes}")]
    public static partial void Failed(ILogger logger, string errorCodes);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Information, Message = "Schema comparison cancelled")]
    public static partial void Cancelled(ILogger logger);
}
