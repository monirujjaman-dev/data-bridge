using DataBridge.Core.Connections;
using DataBridge.Shared.Errors;
using DataBridge.Shared.Results;

namespace DataBridge.Core.Synchronization;

/// <summary>
/// A user's approval of one plan's exact content (domain-model §9). Kept apart from the plan: approving never changes
/// the plan, and the approval applies only to <see cref="ApprovedContentHash"/>.
/// </summary>
/// <param name="PlanId">The approved plan.</param>
/// <param name="ApprovedContentHash">The plan's content hash when approved.</param>
/// <param name="ConfirmedWriteTargetIdentityFingerprint">The write target the user confirmed.</param>
/// <param name="ApprovedOperationIds">The High, Critical and destructive operations the user approved one by one.</param>
/// <param name="ApprovedAtUtc">When.</param>
public sealed record PlanApproval(
    Guid PlanId,
    string ApprovedContentHash,
    string ConfirmedWriteTargetIdentityFingerprint,
    IReadOnlyList<Guid> ApprovedOperationIds,
    DateTimeOffset ApprovedAtUtc);

/// <summary>What the user confirmed when approving.</summary>
/// <param name="ConfirmedWriteTargetIdentityFingerprint">The write target identity shown to the user and confirmed.</param>
/// <param name="ApprovedOperationIds">The operations requiring approval that the user ticked.</param>
/// <param name="TypedDatabaseName">The database name the user typed; required when the write target is Production.</param>
public sealed record ApprovalRequest(
    string ConfirmedWriteTargetIdentityFingerprint,
    IReadOnlyCollection<Guid> ApprovedOperationIds,
    string? TypedDatabaseName);

/// <summary>
/// Decides whether a plan may be approved and whether an approval authorizes executing a plan (FR-PLN-010,
/// FR-PLN-011, ADR-0015).
/// </summary>
/// <remarks>
/// Approval requires confirming the write target and approving every High, Critical and destructive operation
/// individually. When the write target was labelled <see cref="EnvironmentLabel.Production"/> at planning, the user
/// must also type its database name exactly, whatever the plan's risk.
/// </remarks>
public static class PlanApprovalPolicy
{
    public static bool RequiresTypedDatabaseName(SynchronizationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.WriteTargetEnvironment == EnvironmentLabel.Production;
    }

    /// <summary>Checks the user's confirmations and returns an approval bound to the plan's content hash.</summary>
    public static Result<PlanApproval> Approve(SynchronizationPlan plan, ApprovalRequest request, DateTimeOffset approvedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<Error>();
        if (plan.Operations.Count == 0)
        {
            errors.Add(SynchronizationErrors.NothingToSynchronize);
        }

        CheckContent(plan, errors);

        if (!string.Equals(request.ConfirmedWriteTargetIdentityFingerprint, plan.WriteTargetIdentityFingerprint, StringComparison.Ordinal))
        {
            errors.Add(SynchronizationErrors.WriteTargetNotConfirmed);
        }

        if (plan.OperationsRequiringApproval.Any(operation => !request.ApprovedOperationIds.Contains(operation.Id)))
        {
            errors.Add(SynchronizationErrors.OperationNotApproved);
        }

        if (RequiresTypedDatabaseName(plan)
            && !string.Equals(request.TypedDatabaseName, plan.WriteTargetDatabaseName, StringComparison.Ordinal))
        {
            errors.Add(SynchronizationErrors.DatabaseNameNotTyped);
        }

        if (errors.Count > 0)
        {
            return Result.Failure<PlanApproval>(errors);
        }

        var approved = plan.OperationsRequiringApproval.Select(operation => operation.Id).ToArray();
        return Result.Success(new PlanApproval(
            plan.Id, plan.ContentHash, plan.WriteTargetIdentityFingerprint, approved, approvedAtUtc));
    }

    /// <summary>
    /// Whether <paramref name="approval"/> authorizes executing <paramref name="plan"/> against
    /// <paramref name="currentWriteTarget"/>, the write target resolved immediately before execution (FR-EXE-002).
    /// </summary>
    public static Result Authorizes(PlanApproval approval, SynchronizationPlan plan, ResolvedDatabaseIdentity currentWriteTarget)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(currentWriteTarget);

        var errors = new List<Error>();
        if (approval.PlanId != plan.Id)
        {
            errors.Add(SynchronizationErrors.WrongPlan);
        }

        if (!string.Equals(approval.ApprovedContentHash, plan.ContentHash, StringComparison.Ordinal))
        {
            errors.Add(SynchronizationErrors.ContentChanged);
        }

        CheckContent(plan, errors);

        if (!string.Equals(approval.ConfirmedWriteTargetIdentityFingerprint, currentWriteTarget.IdentityFingerprint, StringComparison.Ordinal)
            || !string.Equals(plan.WriteTargetIdentityFingerprint, currentWriteTarget.IdentityFingerprint, StringComparison.Ordinal))
        {
            errors.Add(SynchronizationErrors.WriteTargetChanged);
        }

        if (plan.OperationsRequiringApproval.Any(operation => !approval.ApprovedOperationIds.Contains(operation.Id)))
        {
            errors.Add(SynchronizationErrors.OperationNotApproved);
        }

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }

    private static void CheckContent(SynchronizationPlan plan, List<Error> errors)
    {
        if (!string.Equals(plan.ComputeContentHash(), plan.ContentHash, StringComparison.Ordinal))
        {
            errors.Add(SynchronizationErrors.ContentChanged);
        }

        if (plan.HasDestructiveOperations && !plan.Options.AllowDestructiveOperations)
        {
            errors.Add(SynchronizationErrors.DestructiveNotAllowed);
        }
    }
}
