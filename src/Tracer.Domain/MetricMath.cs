namespace Tracer.Domain;

/// <summary>The grain a time series is bucketed at.</summary>
public enum MetricInterval
{
    Day = 0,
    Week = 1,
}

/// <summary>
/// The small, exact pieces of arithmetic the metric endpoints share. Kept apart
/// from the controllers because a percentile computed one way in one place and
/// another way in another is how two screens quoting "p90 cycle time" end up
/// disagreeing.
/// </summary>
public static class MetricMath
{
    /// <summary>
    /// The nearest-rank percentile of an already-ascending list.
    ///
    /// <para>
    /// Nearest-rank, not interpolation: a cycle-time percentile should be a
    /// duration that actually occurred, and for the small samples a team produces
    /// — a few dozen issues in a window — interpolating between two of them
    /// invents a number no issue ever took. The caller sorts once and may ask for
    /// several percentiles off the same list.
    /// </para>
    /// </summary>
    public static double Percentile(IReadOnlyList<double> sortedAscending, int percentile)
    {
        if (sortedAscending.Count == 0)
        {
            throw new ArgumentException("Percentile is undefined for an empty sample.", nameof(sortedAscending));
        }

        if (percentile is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Percentile must be between 0 and 100.");
        }

        // rank = ceil(p/100 * n), then to a 0-based index; p=0 and rounding both
        // land on the first element, so clamp keeps the index in range.
        var rank = (int)Math.Ceiling(percentile / 100.0 * sortedAscending.Count);
        var index = Math.Clamp(rank - 1, 0, sortedAscending.Count - 1);
        return sortedAscending[index];
    }

    /// <summary>
    /// The start instant of the bucket an instant falls in. Buckets are aligned to
    /// UTC day boundaries, and weeks start on Monday, so consecutive buckets tile
    /// the timeline without gaps or overlaps.
    /// </summary>
    public static DateTimeOffset BucketStart(DateTimeOffset instant, MetricInterval interval)
    {
        var day = new DateTimeOffset(instant.UtcDateTime.Date, TimeSpan.Zero);
        if (interval == MetricInterval.Week)
        {
            // DayOfWeek is Sunday=0..Saturday=6; shift so Monday is the anchor.
            var sinceMonday = ((int)day.DayOfWeek + 6) % 7;
            return day.AddDays(-sinceMonday);
        }

        return day;
    }

    /// <summary>Advances a bucket start to the next bucket of the same interval.</summary>
    public static DateTimeOffset NextBucket(DateTimeOffset bucketStart, MetricInterval interval) =>
        interval == MetricInterval.Week ? bucketStart.AddDays(7) : bucketStart.AddDays(1);
}
