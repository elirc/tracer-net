using Tracer.Domain.Entities;

namespace Tracer.Domain;

/// <summary>
/// Scheduling rules for cycles.
///
/// A cycle covers the half-open interval <c>[StartsAt, EndsAt)</c>: the start
/// instant belongs to the cycle, the end instant is the first instant that does
/// not. Half-open intervals let consecutive cycles share a boundary
/// (cycle 1 ending Monday 09:00, cycle 2 starting Monday 09:00) without
/// overlapping and without leaving a gap.
///
/// Status is always derived from the dates and the current time; it is never
/// stored, so a cycle cannot drift out of sync with the calendar.
/// </summary>
public static class CycleSchedule
{
    public static CycleStatus StatusAt(DateTimeOffset startsAt, DateTimeOffset endsAt, DateTimeOffset now)
    {
        if (now < startsAt)
        {
            return CycleStatus.Upcoming;
        }

        return now < endsAt ? CycleStatus.Active : CycleStatus.Completed;
    }

    /// <summary>A cycle must cover a non-empty span of time.</summary>
    public static bool IsValidRange(DateTimeOffset startsAt, DateTimeOffset endsAt) => endsAt > startsAt;

    /// <summary>
    /// True when two half-open intervals share at least one instant. Touching
    /// intervals (one ending exactly where the next starts) do not overlap.
    /// </summary>
    public static bool Overlaps(
        DateTimeOffset firstStart,
        DateTimeOffset firstEnd,
        DateTimeOffset secondStart,
        DateTimeOffset secondEnd) =>
        firstStart < secondEnd && secondStart < firstEnd;
}
