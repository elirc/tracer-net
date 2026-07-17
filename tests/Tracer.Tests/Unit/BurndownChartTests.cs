using Tracer.Domain;

namespace Tracer.Tests.Unit;

public class BurndownChartTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 3, 16, 0, 0, 0, TimeSpan.Zero); // 14 days

    // A (5pts, in from the start, done day 3), B (3pts, from the start, done day 7),
    // C (2pts, added on day 5, never finished). Total scope 10.
    private static readonly BurndownScopeItem[] Scope =
    [
        new(5, Start, Start.AddDays(3)),
        new(3, Start, Start.AddDays(7)),
        new(2, Start.AddDays(5), null),
    ];

    [Fact]
    public void The_series_runs_daily_from_the_start_to_the_end_when_the_cycle_is_over()
    {
        var series = BurndownChart.Build(Scope, Start, End, End.AddDays(1));

        Assert.Equal(Start, series[0].Date);
        Assert.Equal(End, series[^1].Date);
        Assert.Equal(Start.AddDays(5), series[5].Date);
    }

    [Fact]
    public void The_first_point_shows_the_scope_present_at_the_start()
    {
        var series = BurndownChart.Build(Scope, Start, End, End.AddDays(1));

        // C is not in yet, so scope is A+B=8. The ideal line still burns the full
        // final scope of 10, which is why actual sits below ideal at the open.
        Assert.Equal(8, series[0].ScopePoints);
        Assert.Equal(0, series[0].CompletedPoints);
        Assert.Equal(8, series[0].RemainingPoints);
        Assert.Equal(10, series[0].IdealRemaining);
    }

    [Fact]
    public void Scope_added_mid_cycle_shows_up_on_the_day_it_arrives()
    {
        var series = BurndownChart.Build(Scope, Start, End, End.AddDays(1));

        // Day 5: C (2) has joined, A (5) is done, B not yet. Scope 10, done 5.
        Assert.Equal(10, series[5].ScopePoints);
        Assert.Equal(5, series[5].CompletedPoints);
        Assert.Equal(5, series[5].RemainingPoints);
    }

    [Fact]
    public void The_last_point_carries_final_scope_and_what_actually_remains()
    {
        var series = BurndownChart.Build(Scope, Start, End, End.AddDays(1));

        // A+B done (8), C outstanding (2). Ideal has burned to zero at the end.
        Assert.Equal(10, series[^1].ScopePoints);
        Assert.Equal(8, series[^1].CompletedPoints);
        Assert.Equal(2, series[^1].RemainingPoints);
        Assert.Equal(0, series[^1].IdealRemaining);
    }

    [Fact]
    public void The_ideal_line_never_rises()
    {
        var series = BurndownChart.Build(Scope, Start, End, End.AddDays(1));

        for (var i = 1; i < series.Count; i++)
        {
            Assert.True(series[i].IdealRemaining <= series[i - 1].IdealRemaining);
        }
    }

    [Fact]
    public void An_in_flight_cycle_only_tells_the_story_up_to_now()
    {
        var now = Start.AddDays(3);
        var series = BurndownChart.Build(Scope, Start, End, now);

        // Stops at now, not at the far-off end.
        Assert.Equal(now, series[^1].Date);
        // The ideal at the end of the reported series is still above zero: the
        // cycle is not over.
        Assert.True(series[^1].IdealRemaining > 0);
    }

    [Fact]
    public void An_empty_cycle_is_a_single_flat_point()
    {
        var series = BurndownChart.Build([], Start, End, Start);

        var only = Assert.Single(series);
        Assert.Equal(0, only.ScopePoints);
        Assert.Equal(0, only.RemainingPoints);
    }
}
