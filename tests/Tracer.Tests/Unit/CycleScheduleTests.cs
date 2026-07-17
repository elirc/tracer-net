using Tracer.Domain;
using Tracer.Domain.Entities;

namespace Tracer.Tests.Unit;

public class CycleScheduleTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 3, 16, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-1, CycleStatus.Upcoming)] // an hour before it opens
    [InlineData(0, CycleStatus.Active)] // the start instant belongs to the cycle
    [InlineData(24, CycleStatus.Active)]
    [InlineData(335, CycleStatus.Active)] // the last hour
    [InlineData(336, CycleStatus.Completed)] // exactly the end instant: already over
    [InlineData(400, CycleStatus.Completed)]
    public void StatusAt_treats_the_cycle_as_a_half_open_interval(int hoursFromStart, CycleStatus expected) =>
        Assert.Equal(expected, CycleSchedule.StatusAt(Start, End, Start.AddHours(hoursFromStart)));

    [Fact]
    public void StatusAt_compares_instants_not_wall_clock_times()
    {
        // 08:00-05:00 is 13:00 UTC, which is inside the cycle even though the
        // local time reads as earlier than the 09:00Z start.
        var insideButLooksEarlier = new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.FromHours(-5));

        Assert.Equal(CycleStatus.Active, CycleSchedule.StatusAt(Start, End, insideButLooksEarlier));
    }

    [Fact]
    public void IsValidRange_requires_a_non_empty_span()
    {
        Assert.True(CycleSchedule.IsValidRange(Start, End));
        Assert.False(CycleSchedule.IsValidRange(Start, Start));
        Assert.False(CycleSchedule.IsValidRange(End, Start));
    }

    [Fact]
    public void Consecutive_cycles_sharing_a_boundary_do_not_overlap()
    {
        // Cycle 1 ends exactly when cycle 2 begins: no gap, no overlap.
        Assert.False(CycleSchedule.Overlaps(Start, End, End, End.AddDays(14)));
        Assert.False(CycleSchedule.Overlaps(End, End.AddDays(14), Start, End));
    }

    [Theory]
    [InlineData(-1, 1)] // straddles the start
    [InlineData(13, 20)] // straddles the end
    [InlineData(1, 13)] // fully contained
    [InlineData(-5, 20)] // fully contains
    [InlineData(0, 14)] // identical
    public void Overlaps_detects_any_shared_instant(int startDayOffset, int endDayOffset) =>
        Assert.True(CycleSchedule.Overlaps(Start, End, Start.AddDays(startDayOffset), Start.AddDays(endDayOffset)));

    [Theory]
    [InlineData(-10, -5)] // entirely before
    [InlineData(20, 30)] // entirely after
    public void Overlaps_is_false_for_disjoint_ranges(int startDayOffset, int endDayOffset) =>
        Assert.False(CycleSchedule.Overlaps(Start, End, Start.AddDays(startDayOffset), Start.AddDays(endDayOffset)));
}
