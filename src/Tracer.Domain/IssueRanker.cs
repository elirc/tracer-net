namespace Tracer.Domain;

/// <summary>
/// Fractional ranking for issue ordering within a workflow state column.
///
/// Moving an issue only rewrites that one issue's rank: a new rank is the
/// midpoint of its two new neighbours. That keeps a reorder to a single-row
/// update instead of renumbering the whole column, which matters when several
/// clients drag cards around concurrently.
///
/// The trade-off is precision. Every midpoint halves the gap, so a pathological
/// run of drops into the same slot eventually exhausts what a double can
/// represent (~15-16 significant digits) and neighbours collapse onto the same
/// rank. <see cref="NeedsRebalance"/> detects that *before* it happens, and the
/// caller renumbers the column with <see cref="RankAt"/> to restore full gaps.
/// </summary>
public static class IssueRanker
{
    /// <summary>Gap between issues in a freshly ranked column.</summary>
    public const double Step = 1.0;

    /// <summary>
    /// Smallest gap worth splitting. Well above the point where doubles lose
    /// the ability to represent a distinct midpoint, so rebalancing always
    /// happens with room to spare rather than after ranks have already collided.
    /// </summary>
    public const double MinGap = 1e-6;

    /// <summary>
    /// A rank strictly between <paramref name="lower"/> and <paramref name="upper"/>.
    /// A null bound means an open end: null <paramref name="lower"/> ranks before
    /// everything, null <paramref name="upper"/> ranks after everything.
    /// Callers must check <see cref="NeedsRebalance"/> first.
    /// </summary>
    public static double Between(double? lower, double? upper) => (lower, upper) switch
    {
        (null, null) => Step,
        (null, { } u) => u - Step,
        ({ } l, null) => l + Step,
        ({ } l, { } u) => l + ((u - l) / 2),
    };

    /// <summary>
    /// True when two neighbours sit too close together to place anything
    /// between them reliably, so the column must be renumbered first.
    /// Open ends never need a rebalance: there is always room beyond the edge.
    /// </summary>
    public static bool NeedsRebalance(double? lower, double? upper) =>
        lower is { } l && upper is { } u && u - l < MinGap;

    /// <summary>Evenly spaced rank for the issue at <paramref name="index"/> when renumbering a column.</summary>
    public static double RankAt(int index) => (index + 1) * Step;
}
