namespace Tracer.Domain;

/// <summary>One issue's contribution to a cycle's burndown.</summary>
/// <param name="Points">Its estimate (unestimated issues contribute 0).</param>
/// <param name="EnteredAt">
/// When it entered the cycle's scope. Reconstructing the exact moment an issue was
/// dragged into a cycle is not possible from what is recorded, so its creation
/// time stands in: an issue added mid-cycle shows up as scope from the day it was
/// created, which is the closest honest approximation available.
/// </param>
/// <param name="CompletedAt">When it reached a Done state, or null if it has not.</param>
public readonly record struct BurndownScopeItem(int Points, DateTimeOffset EnteredAt, DateTimeOffset? CompletedAt);

/// <summary>A single day on the burndown/scope-change series.</summary>
public readonly record struct BurndownPoint(
    DateTimeOffset Date,
    int ScopePoints,
    int CompletedPoints,
    int RemainingPoints,
    double IdealRemaining);

/// <summary>
/// Builds a cycle's burndown as a daily series, carrying the scope line alongside
/// the remaining line so that scope <i>changes</i> are visible rather than hidden
/// inside the burndown: a flat remaining line while scope climbs is a cycle taking
/// on work as fast as it finishes it, and only showing both lines tells that
/// story.
///
/// <para>
/// The ideal guide burns the cycle's full scope down to zero linearly across its
/// dates. It is a reference, not a promise — the actual remaining line is what the
/// board says.
/// </para>
/// </summary>
public static class BurndownChart
{
    public static List<BurndownPoint> Build(
        IReadOnlyList<BurndownScopeItem> scope,
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset now)
    {
        var totalScope = scope.Sum(i => i.Points);
        var span = (end - start).TotalSeconds;

        // The series runs to where the story can honestly be told so far: the
        // cycle's end once it is over, otherwise now. Never before the start.
        var last = now < end ? now : end;
        if (last < start)
        {
            last = start;
        }

        var points = new List<BurndownPoint>();
        for (var at = start; ; at = at.AddDays(1))
        {
            if (at > last)
            {
                at = last; // final, partial day
            }

            var scopeAsOf = scope.Where(i => i.EnteredAt <= at).Sum(i => i.Points);
            var completedAsOf = scope.Where(i => i.CompletedAt is { } c && c <= at).Sum(i => i.Points);

            var idealRemaining = span <= 0
                ? 0
                : Math.Clamp(totalScope * (1 - (at - start).TotalSeconds / span), 0, totalScope);

            points.Add(new BurndownPoint(
                at,
                scopeAsOf,
                completedAsOf,
                scopeAsOf - completedAsOf,
                Math.Round(idealRemaining, 2)));

            if (at >= last)
            {
                break;
            }
        }

        return points;
    }
}
