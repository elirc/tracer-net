namespace Tracer.Domain;

/// <summary>
/// Reachability over issue links, used to keep two different graphs acyclic.
///
/// <para>
/// <b>Sub-issues.</b> A parent chain that loops is not a tracker with an odd
/// shape, it is a tracker that hangs: every "walk up to the root" — breadcrumbs,
/// rollups, permission checks — spins forever. Nothing in a self-referencing
/// foreign key prevents it, so it is prevented here.
/// </para>
/// <para>
/// <b>Blocking.</b> A cycle in the blocking graph is a deadlock stated as data:
/// A waits on B, B waits on A, and no sequence of work finishes either. It is
/// always a mistake, and it is much cheaper to refuse than to explain later.
/// </para>
/// <para>
/// Both are the same question — "does a path already run from X back to Y?" —
/// so both use <see cref="Reaches"/>.
/// </para>
/// </summary>
public static class IssueGraph
{
    /// <summary>
    /// True when following <paramref name="edges"/> out of <paramref name="start"/>
    /// arrives at <paramref name="target"/>. <paramref name="start"/> itself counts:
    /// a node trivially reaches itself.
    ///
    /// <para>
    /// The visited set is not an optimisation, it is the thing that makes this
    /// terminate. It would be comfortable to assume the stored graph is already
    /// acyclic — this very function is what keeps it so — but a traversal that
    /// relies on the invariant it enforces will hang forever the first time the
    /// invariant is broken by anything else: a bad migration, a restored backup,
    /// a future bulk import. Cheap insurance against an unkillable request.
    /// </para>
    /// </summary>
    public static bool Reaches(IReadOnlyDictionary<Guid, List<Guid>> edges, Guid start, Guid target)
    {
        var visited = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(start);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current == target)
            {
                return true;
            }

            if (!visited.Add(current) || !edges.TryGetValue(current, out var next))
            {
                continue;
            }

            foreach (var neighbour in next)
            {
                pending.Push(neighbour);
            }
        }

        return false;
    }

    /// <summary>
    /// True when making <paramref name="parentId"/> the parent of
    /// <paramref name="childId"/> would close a loop — either because they are the
    /// same issue, or because the proposed parent already descends from the child.
    /// <paramref name="parentOf"/> maps an issue to its current parent.
    /// </summary>
    public static bool WouldCycle(IReadOnlyDictionary<Guid, Guid> parentOf, Guid childId, Guid parentId)
    {
        if (childId == parentId)
        {
            return true;
        }

        var visited = new HashSet<Guid>();
        var current = parentId;

        // Walk up from the proposed parent. Meeting the child on the way to the
        // root means the child is already an ancestor, so this edge would close
        // the loop.
        while (visited.Add(current) && parentOf.TryGetValue(current, out var next))
        {
            if (next == childId)
            {
                return true;
            }

            current = next;
        }

        return false;
    }
}
