namespace DataBridge.Core.Synchronization;

/// <summary>The result of ordering nodes by their dependencies.</summary>
/// <param name="Order">Indexes of the nodes that can run, each after everything it depends on.</param>
/// <param name="Cycles">Groups of nodes that depend on each other, directly or indirectly; none of them can run.</param>
/// <param name="Blocked">Nodes outside any cycle that depend, directly or indirectly, on a node in a cycle.</param>
public sealed record DependencyOrder(
    IReadOnlyList<int> Order,
    IReadOnlyList<IReadOnlyList<int>> Cycles,
    IReadOnlyList<int> Blocked)
{
    public bool HasCycles => Cycles.Count > 0;
}

/// <summary>
/// Topological ordering with cycle detection (FR-PLN-005, FR-PLN-006). Deterministic: among nodes whose dependencies
/// are met, the lowest rank goes first, then the lowest index, so the same input always gives the same order.
/// </summary>
public static class DependencyOrdering
{
    /// <param name="count">The number of nodes, indexed from 0.</param>
    /// <param name="dependenciesOf">The nodes a node depends on; indexes out of range are an error.</param>
    /// <param name="rankOf">A tie-breaker: lower ranks run first when the dependencies allow it.</param>
    public static DependencyOrder Sort(int count, Func<int, IEnumerable<int>> dependenciesOf, Func<int, int> rankOf)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentNullException.ThrowIfNull(dependenciesOf);
        ArgumentNullException.ThrowIfNull(rankOf);

        var dependencies = new int[count][];
        var dependents = new List<int>[count];
        for (var node = 0; node < count; node++)
        {
            dependents[node] = [];
        }

        for (var node = 0; node < count; node++)
        {
            dependencies[node] = [.. dependenciesOf(node).Distinct()];
            foreach (var dependency in dependencies[node])
            {
                ArgumentOutOfRangeException.ThrowIfNegative(dependency);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(dependency, count);
                dependents[dependency].Add(node);
            }
        }

        var cycles = FindCycles(count, dependencies);
        var inCycle = new bool[count];
        foreach (var node in cycles.SelectMany(cycle => cycle))
        {
            inCycle[node] = true;
        }

        var blocked = new bool[count];
        var pending = new Stack<int>(cycles.SelectMany(cycle => cycle));
        while (pending.TryPop(out var node))
        {
            foreach (var dependent in dependents[node].Where(dependent => !inCycle[dependent] && !blocked[dependent]))
            {
                blocked[dependent] = true;
                pending.Push(dependent);
            }
        }

        var remaining = new int[count];
        var ready = new PriorityQueue<int, (int Rank, int Index)>();
        for (var node = 0; node < count; node++)
        {
            remaining[node] = dependencies[node].Length;
            if (remaining[node] == 0 && !inCycle[node] && !blocked[node])
            {
                ready.Enqueue(node, (rankOf(node), node));
            }
        }

        var order = new List<int>(count);
        while (ready.TryDequeue(out var node, out _))
        {
            order.Add(node);
            foreach (var dependent in dependents[node])
            {
                if (--remaining[dependent] == 0 && !inCycle[dependent] && !blocked[dependent])
                {
                    ready.Enqueue(dependent, (rankOf(dependent), dependent));
                }
            }
        }

        var blockedNodes = Enumerable.Range(0, count).Where(node => blocked[node]).ToArray();
        return new DependencyOrder(order, cycles, blockedNodes);
    }

    /// <summary>Tarjan's strongly connected components, iteratively; returns components of two or more nodes, and self-loops.</summary>
    private static List<IReadOnlyList<int>> FindCycles(int count, int[][] dependencies)
    {
        var index = new int[count];
        var low = new int[count];
        var onStack = new bool[count];
        Array.Fill(index, -1);
        var stack = new Stack<int>();
        var cycles = new List<IReadOnlyList<int>>();
        var next = 0;

        for (var root = 0; root < count; root++)
        {
            if (index[root] >= 0)
            {
                continue;
            }

            var work = new Stack<(int Node, int Edge)>();
            work.Push((root, 0));
            index[root] = low[root] = next++;
            stack.Push(root);
            onStack[root] = true;

            while (work.TryPop(out var frame))
            {
                var (node, edge) = frame;
                if (edge < dependencies[node].Length)
                {
                    work.Push((node, edge + 1));
                    var child = dependencies[node][edge];
                    if (index[child] < 0)
                    {
                        index[child] = low[child] = next++;
                        stack.Push(child);
                        onStack[child] = true;
                        work.Push((child, 0));
                    }
                    else if (onStack[child])
                    {
                        low[node] = Math.Min(low[node], index[child]);
                    }

                    continue;
                }

                if (work.TryPeek(out var parent))
                {
                    low[parent.Node] = Math.Min(low[parent.Node], low[node]);
                }

                if (low[node] != index[node])
                {
                    continue;
                }

                var component = new List<int>();
                int member;
                do
                {
                    member = stack.Pop();
                    onStack[member] = false;
                    component.Add(member);
                }
                while (member != node);

                if (component.Count > 1 || dependencies[node].Contains(node))
                {
                    component.Sort();
                    cycles.Add(component);
                }
            }
        }

        cycles.Sort((left, right) => left[0].CompareTo(right[0]));
        return cycles;
    }
}
