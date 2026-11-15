using DataBridge.Application.Abstractions;
using DataBridge.Core.Synchronization;

namespace DataBridge.Application.Synchronization;

/// <summary>What validation found, and when.</summary>
public sealed record ValidationReport(IReadOnlyList<ValidationFinding> Findings, DateTimeOffset ValidatedAtUtc)
{
    public bool HasBlockingFindings => Findings.Any(finding => finding.Severity == ValidationSeverity.Blocking);
}

/// <summary>
/// A plan with its lifecycle state, kept apart from the plan's content (domain-model §9): changing the status,
/// validation or approval never changes the plan or its content hash.
/// </summary>
/// <param name="Assembled">The plan and each operation's detail.</param>
/// <param name="SourceProfileId">The compared source profile.</param>
/// <param name="TargetProfileId">The compared target profile.</param>
/// <param name="Status">Where the plan is in its lifecycle.</param>
/// <param name="StatusReason">Why the plan is invalidated or failed validation, when it is.</param>
/// <param name="Validation">The latest validation, if any.</param>
/// <param name="Approval">The approval, once approved.</param>
public sealed record PlanRecord(
    AssembledPlan Assembled,
    Guid SourceProfileId,
    Guid TargetProfileId,
    PlanStatus Status,
    string? StatusReason = null,
    ValidationReport? Validation = null,
    PlanApproval? Approval = null)
{
    public SynchronizationPlan Plan => Assembled.Plan;

    public Guid WriteProfileId => Plan.WriteTargetConnectionId;
}

/// <summary>
/// The plans of this application session, in memory. Plans are not yet persisted (history is Phase 7), so closing the
/// application discards them; their change sets are deleted when a plan is discarded, or after
/// a week by the change set store.
/// </summary>
public sealed class SynchronizationPlanStore
{
    private readonly Dictionary<Guid, PlanRecord> _plans = [];
    private readonly Lock _lock = new();

    public void Add(PlanRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            _plans.Add(record.Plan.Id, record);
        }
    }

    public PlanRecord? Find(Guid planId)
    {
        lock (_lock)
        {
            return _plans.GetValueOrDefault(planId);
        }
    }

    /// <summary>Replaces a plan's lifecycle state. The plan itself cannot change: the replacement must hold the same plan.</summary>
    public void Update(PlanRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            if (!_plans.TryGetValue(record.Plan.Id, out var current) || !ReferenceEquals(current.Plan, record.Plan))
            {
                throw new InvalidOperationException("Only a stored plan's lifecycle state can be updated.");
            }

            _plans[record.Plan.Id] = record;
        }
    }

    public PlanRecord? Remove(Guid planId)
    {
        lock (_lock)
        {
            return _plans.Remove(planId, out var record) ? record : null;
        }
    }

    /// <summary>Whether another stored plan uses a change set, so discarding one plan must keep it.</summary>
    public bool IsChangeSetInUse(string storeId)
    {
        lock (_lock)
        {
            return _plans.Values.Any(record => record.Plan.Operations.Any(operation => operation.Rows?.StoreId == storeId));
        }
    }
}
