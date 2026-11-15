using DataBridge.Core.Connections;
using DataBridge.Shared.Results;

namespace DataBridge.Core.Synchronization;

/// <summary>The fields of a plan that do not come from its changes.</summary>
public sealed record PlanHeader(
    Guid PlanId,
    Guid ComparisonSessionId,
    SynchronizationDirection Direction,
    Guid WriteTargetConnectionId,
    ResolvedDatabaseIdentity WriteTarget,
    EnvironmentLabel WriteTargetEnvironment,
    string WriteTargetSchemaFingerprint,
    SynchronizationPlanOptions Options,
    DateTimeOffset CreatedAtUtc,
    ResolvedDatabaseIdentity? CopySide = null);

/// <summary>A plan and, for each operation, the detail its SQL was generated from (used to validate the write target).</summary>
public sealed record AssembledPlan(SynchronizationPlan Plan, IReadOnlyDictionary<Guid, SchemaChangeDetail> Details);

/// <summary>
/// Turns a <see cref="PlanDraft"/> into a <see cref="SynchronizationPlan"/>: generates each change's SQL, orders the
/// changes by their dependencies (FR-PLN-005) and reports what cannot be included (FR-PLN-006).
/// </summary>
/// <remarks>
/// A change whose SQL cannot be generated is left out, and so is every change that depends on it, directly or
/// indirectly. Changes in a dependency cycle, and changes that depend on one, are left out and the cycle is reported
/// with its members. Nothing that is left out is silently dropped: each gets a <see cref="PlanIssue"/>.
/// </remarks>
public static class PlanAssembler
{
    /// <param name="draft">The planned changes and issues.</param>
    /// <param name="header">Plan identity, write target and options.</param>
    /// <param name="generate">The provider's SQL for a change, or a failure explaining why it cannot be generated.</param>
    /// <param name="newId">Creates operation ids.</param>
    public static AssembledPlan Assemble(
        PlanDraft draft,
        PlanHeader header,
        Func<PlannedChange, Result<IReadOnlyList<string>>> generate,
        Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(generate);
        ArgumentNullException.ThrowIfNull(newId);

        var changes = draft.Changes;
        var issues = new List<PlanIssue>(draft.Issues);
        var sql = new IReadOnlyList<string>?[changes.Count];
        var excluded = new bool[changes.Count];
        for (var index = 0; index < changes.Count; index++)
        {
            var generated = generate(changes[index]);
            if (generated.IsSuccess)
            {
                sql[index] = generated.Value;
                continue;
            }

            excluded[index] = true;
            issues.Add(new PlanIssue(changes[index].ObjectId, $"{changes[index].Description}: not included. {generated.Errors[0].Message}"));
        }

        ExcludeDependents(draft, excluded, issues);

        // Leaving out a change in a cycle can leave out a partner or dependent elsewhere, so repeat until stable.
        int[] included;
        DependencyOrder order;
        while (true)
        {
            included = [.. Enumerable.Range(0, changes.Count).Where(index => !excluded[index])];
            var positions = new Dictionary<int, int>();
            for (var index = 0; index < included.Length; index++)
            {
                positions[included[index]] = index;
            }

            var nodes = included;
            order = DependencyOrdering.Sort(
                nodes.Length,
                node => draft.DependenciesOf(nodes[node]).Where(positions.ContainsKey).Select(dependency => positions[dependency]),
                node => changes[nodes[node]].Rank);
            if (!order.HasCycles && order.Blocked.Count == 0)
            {
                break;
            }

            foreach (var cycle in order.Cycles)
            {
                var members = cycle.Select(node => changes[nodes[node]]).ToArray();
                issues.Add(new PlanIssue(
                    members[0].ObjectId,
                    $"These changes depend on each other in a cycle, so none of them is included: {string.Join("; ", members.Select(member => member.Description))}."));
                foreach (var node in cycle)
                {
                    excluded[nodes[node]] = true;
                }
            }

            foreach (var node in order.Blocked)
            {
                var change = changes[nodes[node]];
                issues.Add(new PlanIssue(change.ObjectId, $"{change.Description}: not included because it depends on changes in a dependency cycle."));
                excluded[nodes[node]] = true;
            }

            ExcludeDependents(draft, excluded, issues);
        }

        var ids = new Dictionary<int, Guid>();
        var details = new Dictionary<Guid, SchemaChangeDetail>();
        var operations = new List<SynchronizationOperation>(order.Order.Count);
        foreach (var node in order.Order)
        {
            var index = included[node];
            var change = changes[index];
            var id = newId();
            ids[index] = id;
            details[id] = change.Detail;
            var rows = change.Detail is RowsDetail detail ? detail.Rows : null;
            operations.Add(new SynchronizationOperation(
                id,
                operations.Count,
                change.ObjectId,
                change.Type,
                change.Risk,
                change.IsDestructive,
                [.. draft.DependenciesOf(index).Where(ids.ContainsKey).Select(dependency => ids[dependency]).Order()],
                change.Description,
                sql[index]!,
                change.EstimatedAffectedRows,
                change.Warnings,
                rows));
        }

        var plan = SynchronizationPlan.Create(
            header.PlanId,
            header.ComparisonSessionId,
            header.Direction,
            header.WriteTargetConnectionId,
            header.WriteTarget,
            header.WriteTargetEnvironment,
            header.WriteTargetSchemaFingerprint,
            header.Options,
            operations,
            issues,
            header.CreatedAtUtc,
            header.CopySide);
        return new AssembledPlan(plan, details);
    }

    /// <summary>Excludes every change that depends on an excluded one, or is bound to one, directly or indirectly.</summary>
    private static void ExcludeDependents(PlanDraft draft, bool[] excluded, List<PlanIssue> issues)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var index = 0; index < excluded.Length; index++)
            {
                if (excluded[index])
                {
                    continue;
                }

                var dependsOnExcluded = draft.DependenciesOf(index).Any(dependency => excluded[dependency]);
                if (!dependsOnExcluded && !draft.PartnersOf(index).Any(partner => excluded[partner]))
                {
                    continue;
                }

                excluded[index] = true;
                changed = true;
                var change = draft.Changes[index];
                issues.Add(new PlanIssue(
                    change.ObjectId,
                    dependsOnExcluded
                        ? $"{change.Description}: not included because a change it depends on is not included."
                        : $"{change.Description}: not included because the change it belongs with is not included."));
            }
        }
    }
}
