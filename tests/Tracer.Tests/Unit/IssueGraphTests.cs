using Tracer.Domain;

namespace Tracer.Tests.Unit;

public class IssueGraphTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();
    private static readonly Guid D = Guid.NewGuid();

    // ---- Reaches ----

    [Fact]
    public void A_node_reaches_itself()
    {
        Assert.True(IssueGraph.Reaches(new Dictionary<Guid, List<Guid>>(), A, A));
    }

    [Fact]
    public void Reaches_follows_a_direct_edge()
    {
        var edges = new Dictionary<Guid, List<Guid>> { [A] = [B] };

        Assert.True(IssueGraph.Reaches(edges, A, B));
    }

    [Fact]
    public void Reaches_follows_a_chain()
    {
        var edges = new Dictionary<Guid, List<Guid>> { [A] = [B], [B] = [C], [C] = [D] };

        Assert.True(IssueGraph.Reaches(edges, A, D));
    }

    [Fact]
    public void Reaches_does_not_walk_edges_backwards()
    {
        var edges = new Dictionary<Guid, List<Guid>> { [A] = [B] };

        Assert.False(IssueGraph.Reaches(edges, B, A));
    }

    [Fact]
    public void Reaches_explores_every_branch()
    {
        var edges = new Dictionary<Guid, List<Guid>> { [A] = [B, C], [C] = [D] };

        Assert.True(IssueGraph.Reaches(edges, A, D));
    }

    /// <summary>
    /// The traversal must terminate even on a graph that already contains a
    /// cycle. Relying on the invariant this function exists to enforce would mean
    /// a single bad row — from a restored backup, a migration, a future import —
    /// turns into a request that never returns.
    /// </summary>
    [Fact]
    public void Reaches_terminates_on_a_graph_that_already_loops()
    {
        var edges = new Dictionary<Guid, List<Guid>> { [A] = [B], [B] = [C], [C] = [A] };

        Assert.False(IssueGraph.Reaches(edges, A, D));
    }

    // ---- WouldCycle (parent chains) ----

    [Fact]
    public void An_issue_cannot_be_its_own_parent()
    {
        Assert.True(IssueGraph.WouldCycle(new Dictionary<Guid, Guid>(), A, A));
    }

    [Fact]
    public void A_fresh_parent_is_fine()
    {
        Assert.False(IssueGraph.WouldCycle(new Dictionary<Guid, Guid>(), A, B));
    }

    [Fact]
    public void A_direct_child_cannot_become_the_parent()
    {
        // B's parent is A; making A a child of B would close a two-node loop.
        var parentOf = new Dictionary<Guid, Guid> { [B] = A };

        Assert.True(IssueGraph.WouldCycle(parentOf, A, B));
    }

    [Fact]
    public void A_distant_descendant_cannot_become_the_parent()
    {
        // A -> B -> C; re-parenting A under C would close the loop.
        var parentOf = new Dictionary<Guid, Guid> { [B] = A, [C] = B };

        Assert.True(IssueGraph.WouldCycle(parentOf, A, C));
    }

    [Fact]
    public void A_sibling_is_not_a_cycle()
    {
        // B and C both sit under A; nesting C under B is legal.
        var parentOf = new Dictionary<Guid, Guid> { [B] = A, [C] = A };

        Assert.False(IssueGraph.WouldCycle(parentOf, C, B));
    }

    [Fact]
    public void An_unrelated_deep_chain_is_not_a_cycle()
    {
        var parentOf = new Dictionary<Guid, Guid> { [B] = C, [C] = D };

        Assert.False(IssueGraph.WouldCycle(parentOf, A, B));
    }

    [Fact]
    public void WouldCycle_terminates_on_a_chain_that_already_loops()
    {
        var parentOf = new Dictionary<Guid, Guid> { [B] = C, [C] = B };

        Assert.False(IssueGraph.WouldCycle(parentOf, A, B));
    }
}
