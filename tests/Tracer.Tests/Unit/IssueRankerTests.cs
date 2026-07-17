using Tracer.Domain;

namespace Tracer.Tests.Unit;

public class IssueRankerTests
{
    [Fact]
    public void Between_two_neighbours_returns_their_midpoint()
    {
        Assert.Equal(1.5, IssueRanker.Between(1.0, 2.0));
        Assert.Equal(1.25, IssueRanker.Between(1.0, 1.5));
    }

    [Fact]
    public void Open_ends_step_beyond_the_edge_of_the_column()
    {
        Assert.Equal(4.0, IssueRanker.Between(3.0, null)); // appended to the end
        Assert.Equal(0.0, IssueRanker.Between(null, 1.0)); // dropped on top
        Assert.Equal(IssueRanker.Step, IssueRanker.Between(null, null)); // first card in an empty column
    }

    [Fact]
    public void Ranks_before_the_first_issue_are_allowed_to_go_negative()
    {
        // Dropping repeatedly onto the top of a column must keep working;
        // a rank is an ordering key, not a row number.
        var rank = IssueRanker.Between(null, 1.0);
        rank = IssueRanker.Between(null, rank);

        Assert.True(rank < 0);
    }

    [Fact]
    public void Between_stays_strictly_inside_its_bounds()
    {
        var (lower, upper) = (1.0, 1.0 + (IssueRanker.MinGap * 2));

        var rank = IssueRanker.Between(lower, upper);

        Assert.True(rank > lower);
        Assert.True(rank < upper);
    }

    [Fact]
    public void NeedsRebalance_only_once_neighbours_are_closer_than_the_minimum_gap()
    {
        // Deliberately not asserted at exactly MinGap: 1.0 + 1e-6 - 1.0 does not
        // round-trip to 1e-6, so the exact boundary is a property of binary
        // floating point rather than a contract worth pinning down.
        Assert.False(IssueRanker.NeedsRebalance(1.0, 2.0));
        Assert.False(IssueRanker.NeedsRebalance(1.0, 1.0 + (IssueRanker.MinGap * 10)));
        Assert.True(IssueRanker.NeedsRebalance(1.0, 1.0 + (IssueRanker.MinGap / 2)));
        Assert.True(IssueRanker.NeedsRebalance(1.0, 1.0)); // already collided
    }

    [Fact]
    public void NeedsRebalance_is_false_at_open_ends()
    {
        // There is always room past the edge of a column, however tight the middle got.
        Assert.False(IssueRanker.NeedsRebalance(null, 1.0));
        Assert.False(IssueRanker.NeedsRebalance(1.0, null));
        Assert.False(IssueRanker.NeedsRebalance(null, null));
    }

    [Fact]
    public void Repeatedly_splitting_the_same_gap_asks_for_a_rebalance_before_ranks_collide()
    {
        var lower = 1.0;
        var upper = 2.0;
        var splits = 0;

        while (!IssueRanker.NeedsRebalance(lower, upper))
        {
            upper = IssueRanker.Between(lower, upper);
            Assert.True(upper > lower, "a rank collided with its neighbour before a rebalance was requested");
            splits++;
            Assert.True(splits < 1000, "the gap should exhaust long before this");
        }

        // Doubles survive far more than a handful of splits, so rebalancing
        // stays a rare event rather than something every drag pays for.
        Assert.True(splits > 15, $"expected many splits before rebalancing, got {splits}");
    }

    [Fact]
    public void RankAt_spreads_a_rebalanced_column_into_even_steps()
    {
        Assert.Equal([1.0, 2.0, 3.0], Enumerable.Range(0, 3).Select(IssueRanker.RankAt).ToArray());
    }
}
