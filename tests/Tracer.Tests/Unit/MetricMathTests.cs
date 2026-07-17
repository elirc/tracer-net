using Tracer.Domain;

namespace Tracer.Tests.Unit;

public class MetricMathTests
{
    // Ten values 10..100 so the ranks are easy to reason about.
    private static readonly double[] Sample = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];

    [Theory]
    [InlineData(50, 50)] // nearest-rank: ceil(0.50*10)=5 -> index 4 -> 50
    [InlineData(75, 80)] // ceil(0.75*10)=8 -> index 7 -> 80
    [InlineData(90, 90)] // ceil(0.90*10)=9 -> index 8 -> 90
    [InlineData(100, 100)]
    public void Percentile_uses_nearest_rank(int percentile, double expected) =>
        Assert.Equal(expected, MetricMath.Percentile(Sample, percentile));

    [Fact]
    public void Percentile_of_a_single_value_is_that_value()
    {
        Assert.Equal(42, MetricMath.Percentile([42], 50));
        Assert.Equal(42, MetricMath.Percentile([42], 90));
    }

    [Fact]
    public void Percentile_never_invents_a_value_between_samples()
    {
        // Interpolation would give 35 for p50 here; nearest-rank returns a value
        // that actually occurred.
        var actual = MetricMath.Percentile([10.0, 30.0, 40.0, 90.0], 50);
        Assert.Contains(actual, new[] { 10.0, 30.0, 40.0, 90.0 });
    }

    [Fact]
    public void Percentile_of_an_empty_sample_throws() =>
        Assert.Throws<ArgumentException>(() => MetricMath.Percentile([], 50));

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Percentile_rejects_a_percentile_out_of_range(int percentile) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MetricMath.Percentile(Sample, percentile));

    [Fact]
    public void BucketStart_by_day_strips_the_time()
    {
        var instant = new DateTimeOffset(2026, 3, 4, 17, 42, 9, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero),
            MetricMath.BucketStart(instant, MetricInterval.Day));
    }

    [Fact]
    public void BucketStart_by_day_normalises_to_utc_first()
    {
        // 2026-03-05 01:00 +05:00 is 2026-03-04 20:00 UTC, so the UTC day is the 4th.
        var instant = new DateTimeOffset(2026, 3, 5, 1, 0, 0, TimeSpan.FromHours(5));
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero),
            MetricMath.BucketStart(instant, MetricInterval.Day));
    }

    [Theory]
    [InlineData(2026, 3, 2)] // Monday -> itself
    [InlineData(2026, 3, 4)] // Wednesday -> back to Monday the 2nd
    [InlineData(2026, 3, 8)] // Sunday -> still the week that began Monday the 2nd
    public void BucketStart_by_week_anchors_on_monday(int year, int month, int day)
    {
        var expected = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        var instant = new DateTimeOffset(year, month, day, 13, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, MetricMath.BucketStart(instant, MetricInterval.Week));
    }

    [Fact]
    public void NextBucket_advances_by_the_interval()
    {
        var monday = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(monday.AddDays(1), MetricMath.NextBucket(monday, MetricInterval.Day));
        Assert.Equal(monday.AddDays(7), MetricMath.NextBucket(monday, MetricInterval.Week));
    }
}
