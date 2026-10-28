namespace Monitor.Core;

public enum HistorySource { Raw, Rollup1m }

public readonly record struct HistoryPlan(HistorySource Source, long BucketMs);

/// Chooses raw rows or the 1-minute rollup, and a bucket size, so a query returns
/// roughly `maxPoints` rows no matter how wide the range is.
public static class HistoryPlanner
{
    // Retention is 90 days and drops whole chunks, so stop trusting raw data a day early.
    public static readonly TimeSpan RawSafeAge = TimeSpan.FromDays(89);
    private const long MinuteMs = 60_000;

    public static HistoryPlan Plan(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now, int maxPoints)
    {
        if (to <= from) throw new ArgumentException("'to' must be after 'from'");
        if (maxPoints < 1) throw new ArgumentOutOfRangeException(nameof(maxPoints));

        var rangeMs = (long)(to - from).TotalMilliseconds;
        var bucket = Math.Max(1, (rangeMs + maxPoints - 1) / maxPoints);

        if (from < now - RawSafeAge || bucket >= MinuteMs)
        {
            // The rollup has 1-minute resolution, so buckets must be whole minutes.
            var b = Math.Max(MinuteMs, (bucket + MinuteMs - 1) / MinuteMs * MinuteMs);
            return new HistoryPlan(HistorySource.Rollup1m, b);
        }
        return new HistoryPlan(HistorySource.Raw, bucket);
    }
}