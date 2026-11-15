using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;
using DataBridge.Application.DataComparison;
using DataBridge.Application.SchemaComparison;
using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Schema;
using DataBridge.Core.Synchronization;
using DataBridge.Shared.Errors;
using DataBridge.Shared.Results;
using Microsoft.Extensions.Logging;

namespace DataBridge.Application.Synchronization;

/// <summary>What to plan: the comparisons, the selected differences, the direction and options.</summary>
/// <param name="SourceProfileId">The compared source profile.</param>
/// <param name="TargetProfileId">The compared target profile.</param>
/// <param name="Direction">Which side to make match the other (FR-PLN-001).</param>
/// <param name="Schema">The schema comparison, when schema differences are selected.</param>
/// <param name="SchemaObjects">Selected schema differences, by comparison identity.</param>
/// <param name="Data">The data comparison, when tables are selected.</param>
/// <param name="DataTables">Selected tables, as named in the data comparison.</param>
/// <param name="ChangeSets">The rows recorded per table during the data comparison.</param>
/// <param name="Options">Destructive opt-in, batch size and large-change threshold.</param>
public sealed record SynchronizationPlanRequest(
    Guid SourceProfileId,
    Guid TargetProfileId,
    SynchronizationDirection Direction,
    SchemaComparisonReport? Schema,
    IReadOnlyCollection<DatabaseObjectId> SchemaObjects,
    DataComparisonReport? Data,
    IReadOnlyCollection<DatabaseObjectId> DataTables,
    IReadOnlyDictionary<DatabaseObjectId, RecordedChangeSet> ChangeSets,
    SynchronizationPlanOptions Options);

/// <summary>
/// Builds a synchronization plan from comparison results (FR-PLN-001 to FR-PLN-007, AC-08). Never touches either
/// database: the plan is built from the schemas and row change sets the comparisons already read.
/// </summary>
/// <remarks>
/// The plan records the write target's identity and schema fingerprint as compared, so validation and execution can
/// tell when the write target has changed since (FR-PLN-012). When both a schema and a data comparison are given,
/// they must be of the same two databases and see the same schemas.
/// </remarks>
public sealed class CreateSynchronizationPlan(
    IConnectionProfileRepository profiles,
    IDatabaseProviderFactory providers,
    SynchronizationPlanStore store,
    TimeProvider timeProvider,
    ILogger<CreateSynchronizationPlan> logger)
{
    public async Task<Result<PlanRecord>> ExecuteAsync(SynchronizationPlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Options.Validate().ToArray() is { Length: > 0 } invalid)
        {
            return Result.Failure<PlanRecord>(invalid);
        }

        if (Comparison(request) is not { IsSuccess: true } comparison)
        {
            return Result.Failure<PlanRecord>(Comparison(request).Errors);
        }

        var (source, target, sourceSchema, targetSchema, sessionId) = comparison.Value;
        if (source.IsSameDatabaseAs(target))
        {
            return Result.Failure<PlanRecord>(SynchronizationUseCaseErrors.SameDatabase);
        }

        var toTarget = request.Direction == SynchronizationDirection.SourceToTarget;
        var (writeProfileId, writeIdentity, writeSchema) = toTarget
            ? (request.TargetProfileId, target, targetSchema)
            : (request.SourceProfileId, source, sourceSchema);
        var writeProfile = await profiles.FindAsync(writeProfileId, cancellationToken);
        if (writeProfile is null)
        {
            return Result.Failure<PlanRecord>(ConnectionProfileErrors.NotFound);
        }

        var draft = new PlanDraft();
        if (request.Schema is { } schemaReport && request.SchemaObjects.Count > 0)
        {
            SchemaSynchronizationPlanner.Plan(
                new SchemaPlanningInput(sourceSchema, targetSchema, schemaReport.Result, request.Direction, request.SchemaObjects, request.Options.AllowDestructiveOperations),
                draft);
        }

        if (request.Data is { } dataReport && request.DataTables.Count > 0)
        {
            PlanData(request, dataReport, sourceSchema, targetSchema, draft);
        }

        var generator = providers.GetSqlGenerator(writeProfile.Provider);
        var assembled = PlanAssembler.Assemble(
            draft,
            new PlanHeader(
                Guid.NewGuid(),
                sessionId,
                request.Direction,
                writeProfileId,
                writeIdentity,
                writeProfile.Environment,
                SchemaFingerprint.Compute(writeSchema),
                request.Options,
                timeProvider.GetUtcNow()),
            generator.Generate,
            Guid.NewGuid);

        var record = new PlanRecord(assembled, request.SourceProfileId, request.TargetProfileId, PlanStatus.Draft);
        store.Add(record);
        var plan = assembled.Plan;
        var destructive = plan.Operations.Count(operation => operation.IsDestructive);
        SynchronizationLog.PlanCreated(logger, plan.Id, plan.Direction, plan.Operations.Count, destructive, plan.NotIncluded.Count);
        return Result.Success(record);
    }

    private static void PlanData(
        SynchronizationPlanRequest request,
        DataComparisonReport report,
        DatabaseSchema sourceSchema,
        DatabaseSchema targetSchema,
        PlanDraft draft)
    {
        var selections = new List<DataTableSelection>();
        foreach (var table in request.DataTables)
        {
            if (report.Tables.FirstOrDefault(summary => summary.TableId == table) is not { } summary)
            {
                draft.Report(table, $"{table.DisplayName} was not in the data comparison.");
                continue;
            }

            if (summary.Outcome == TableDataComparisonOutcome.Compared && summary.DifferenceCount == 0)
            {
                continue;
            }

            if (summary.Outcome == TableDataComparisonOutcome.Compared && !request.ChangeSets.ContainsKey(table))
            {
                draft.Report(table, $"The differing rows of {table.DisplayName} were not recorded. Compare the data again from the synchronization screen.");
                continue;
            }

            selections.Add(new DataTableSelection(
                summary,
                summary.EffectiveValueColumns,
                request.ChangeSets.GetValueOrDefault(table) ?? new RecordedChangeSet("", new Dictionary<DifferenceStatus, RecordedRows>())));
        }

        DataSynchronizationPlanner.Plan(
            new DataPlanningInput(sourceSchema, targetSchema, request.Direction, selections, request.Options, TablesWithSchemaChanges(draft)),
            draft);
    }

    /// <summary>The tables the planned schema changes touch, directly or through their columns, constraints, indexes and triggers.</summary>
    private static DatabaseObjectId[] TablesWithSchemaChanges(PlanDraft draft) =>
        [.. draft.Changes
            .Select(change => change.ObjectId)
            .Where(id => id.ObjectType == DatabaseObjectType.Table || id.ParentObjectName is not null)
            .Select(id => id.ParentObjectName is { } parent ? new DatabaseObjectId(id.SchemaName, parent, DatabaseObjectType.Table) : id)
            .Distinct()];

    private static Result<(ResolvedDatabaseIdentity Source, ResolvedDatabaseIdentity Target, DatabaseSchema SourceSchema, DatabaseSchema TargetSchema, Guid SessionId)> Comparison(
        SynchronizationPlanRequest request)
    {
        var schema = request.Schema;
        var data = request.Data;
        if (schema is null && data is null)
        {
            return Result.Failure<(ResolvedDatabaseIdentity, ResolvedDatabaseIdentity, DatabaseSchema, DatabaseSchema, Guid)>(SynchronizationUseCaseErrors.NoComparison);
        }

        var source = schema?.Source ?? data!.Source;
        var target = schema?.Target ?? data!.Target;
        var sourceSchema = schema?.SourceSchema ?? data?.SourceSchema;
        var targetSchema = schema?.TargetSchema ?? data?.TargetSchema;
        if (sourceSchema is null || targetSchema is null)
        {
            return Result.Failure<(ResolvedDatabaseIdentity, ResolvedDatabaseIdentity, DatabaseSchema, DatabaseSchema, Guid)>(SynchronizationUseCaseErrors.NoComparison);
        }

        if (schema is not null && data is not null
            && (!schema.Source.IsSameDatabaseAs(data.Source)
                || !schema.Target.IsSameDatabaseAs(data.Target)
                || data.SourceSchema is null || data.TargetSchema is null
                || SchemaFingerprint.Compute(sourceSchema) != SchemaFingerprint.Compute(data.SourceSchema)
                || SchemaFingerprint.Compute(targetSchema) != SchemaFingerprint.Compute(data.TargetSchema)))
        {
            return Result.Failure<(ResolvedDatabaseIdentity, ResolvedDatabaseIdentity, DatabaseSchema, DatabaseSchema, Guid)>(SynchronizationUseCaseErrors.ComparisonsDisagree);
        }

        return Result.Success((source, target, sourceSchema, targetSchema, schema?.SessionId ?? data!.SessionId));
    }
}

/// <summary>
/// Checks a plan against the write target as it is now (FR-PLN-008, FR-PLN-012): the same database, the same schema
/// as compared, intact change sets, permissions, and no existing rows that would make an operation fail. Read-only.
/// </summary>
public sealed class ValidateSynchronizationPlan(
    SavedConnectionRequests connections,
    IDatabaseProviderFactory providers,
    IChangeSetStore changeSets,
    SynchronizationPlanStore store,
    TimeProvider timeProvider,
    ILogger<ValidateSynchronizationPlan> logger)
{
    public async Task<Result<PlanRecord>> ExecuteAsync(Guid planId, CancellationToken cancellationToken)
    {
        if (store.Find(planId) is not { } record)
        {
            return Result.Failure<PlanRecord>(SynchronizationUseCaseErrors.PlanNotFound);
        }

        if (record.Status is not (PlanStatus.Draft or PlanStatus.Validated or PlanStatus.ValidationFailed))
        {
            return Result.Success(record);
        }

        var plan = record.Plan;
        var request = await connections.ForProfileAsync(record.WriteProfileId, cancellationToken);
        if (request.IsFailure)
        {
            return Result.Failure<PlanRecord>(request.Errors);
        }

        var provider = request.Value.Profile.Provider;
        var identity = await providers.GetProvider(provider).ResolveIdentityAsync(request.Value, cancellationToken);
        if (identity.IsFailure)
        {
            return Result.Failure<PlanRecord>(identity.Errors);
        }

        if (identity.Value.IdentityFingerprint != plan.WriteTargetIdentityFingerprint)
        {
            return Invalidate(record, "The write target profile now connects to a different database than the one compared.");
        }

        var schema = await providers.GetSchemaReader(provider).ReadSchemaAsync(request.Value, cancellationToken);
        if (schema.IsFailure)
        {
            return Result.Failure<PlanRecord>(schema.Errors);
        }

        if (SchemaFingerprint.Compute(schema.Value) != plan.WriteTargetSchemaFingerprint)
        {
            return Invalidate(record, "The write target's schema changed since it was compared.");
        }

        foreach (var rows in plan.Operations.Select(operation => operation.Rows).OfType<RowChangeSet>())
        {
            if (!await changeSets.VerifyAsync(rows, cancellationToken))
            {
                return Invalidate(record, SynchronizationUseCaseErrors.ChangeSetCorrupt.Message);
            }
        }

        var findings = await providers.GetTargetInspector(provider).InspectAsync(request.Value, record.Assembled, cancellationToken);
        if (findings.IsFailure)
        {
            return Result.Failure<PlanRecord>(findings.Errors);
        }

        var validation = new ValidationReport(findings.Value, timeProvider.GetUtcNow());
        var updated = record with
        {
            Status = validation.HasBlockingFindings ? PlanStatus.ValidationFailed : PlanStatus.Validated,
            StatusReason = validation.HasBlockingFindings ? SynchronizationUseCaseErrors.ValidationFailed.Message : null,
            Validation = validation,
        };
        store.Update(updated);
        SynchronizationLog.PlanValidated(logger, plan.Id, updated.Status, findings.Value.Count);
        return Result.Success(updated);
    }

    private Result<PlanRecord> Invalidate(PlanRecord record, string reason)
    {
        var updated = record with { Status = PlanStatus.Invalidated, StatusReason = reason, Validation = null };
        store.Update(updated);
        SynchronizationLog.PlanValidated(logger, record.Plan.Id, PlanStatus.Invalidated, 0);
        return Result.Success(updated);
    }
}

/// <summary>
/// Approves a validated plan (FR-PLN-010, FR-PLN-011, AC-09). The approval is bound to the plan's content hash and the
/// confirmed write target; it does not execute anything (execution is Phase 6).
/// </summary>
public sealed class ApproveSynchronizationPlan(
    SynchronizationPlanStore store,
    TimeProvider timeProvider,
    ILogger<ApproveSynchronizationPlan> logger)
{
    public Result<PlanRecord> Execute(Guid planId, ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (store.Find(planId) is not { } record)
        {
            return Result.Failure<PlanRecord>(SynchronizationUseCaseErrors.PlanNotFound);
        }

        Error? notReady = record.Status switch
        {
            PlanStatus.Validated => null,
            PlanStatus.Invalidated => SynchronizationUseCaseErrors.Invalidated,
            PlanStatus.ValidationFailed => SynchronizationUseCaseErrors.ValidationFailed,
            _ => SynchronizationUseCaseErrors.NotValidated,
        };
        if (notReady is not null)
        {
            return Result.Failure<PlanRecord>(notReady);
        }

        var approval = PlanApprovalPolicy.Approve(record.Plan, request, timeProvider.GetUtcNow());
        if (approval.IsFailure)
        {
            return Result.Failure<PlanRecord>(approval.Errors);
        }

        var updated = record with { Status = PlanStatus.Approved, Approval = approval.Value };
        store.Update(updated);
        SynchronizationLog.PlanApproved(logger, record.Plan.Id, approval.Value.ApprovedOperationIds.Count);
        return Result.Success(updated);
    }
}

/// <summary>Exports a plan's script for review (FR-PLN-009, FR-PLN-013), and discards plans with their stored rows.</summary>
public sealed class SynchronizationScripts(
    SynchronizationPlanStore store,
    IDatabaseProviderFactory providers,
    IChangeSetStore changeSets,
    IConnectionProfileRepository profiles)
{
    public async Task<Result<string>> ScriptAsync(Guid planId, CancellationToken cancellationToken)
    {
        if (store.Find(planId) is not { } record)
        {
            return Result.Failure<string>(SynchronizationUseCaseErrors.PlanNotFound);
        }

        var profile = await profiles.FindAsync(record.WriteProfileId, cancellationToken);
        var provider = profile?.Provider ?? DatabaseProviderType.SqlServer;
        return Result.Success(providers.GetSqlGenerator(provider).BuildScript(record.Plan));
    }

    /// <summary>Forgets a plan and deletes the change sets no other plan uses.</summary>
    public async Task DiscardAsync(Guid planId, CancellationToken cancellationToken)
    {
        if (store.Remove(planId) is not { } record)
        {
            return;
        }

        foreach (var storeId in record.Plan.Operations.Select(operation => operation.Rows?.StoreId).OfType<string>().Distinct())
        {
            if (!store.IsChangeSetInUse(storeId))
            {
                await changeSets.DeleteAsync(storeId, cancellationToken);
            }
        }
    }
}

/// <summary>Synchronization log events: ids, counts and statuses only, never names, SQL or row values.</summary>
internal static partial class SynchronizationLog
{
    [LoggerMessage(EventId = 3200, Level = LogLevel.Information, Message = "Synchronization plan {PlanId} created: {Direction}, {OperationCount} operations, {DestructiveCount} destructive, {NotIncludedCount} not included")]
    public static partial void PlanCreated(ILogger logger, Guid planId, SynchronizationDirection direction, int operationCount, int destructiveCount, int notIncludedCount);

    [LoggerMessage(EventId = 3201, Level = LogLevel.Information, Message = "Synchronization plan {PlanId} validated: {Status}, {FindingCount} findings")]
    public static partial void PlanValidated(ILogger logger, Guid planId, PlanStatus status, int findingCount);

    [LoggerMessage(EventId = 3202, Level = LogLevel.Information, Message = "Synchronization plan {PlanId} approved with {ApprovedOperationCount} individually approved operations")]
    public static partial void PlanApproved(ILogger logger, Guid planId, int approvedOperationCount);
}
