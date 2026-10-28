using Monitor.Core;
using Xunit;

public class HistoryPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static HistoryPlan Plan(TimeSpan range, int points, TimeSpan? age = null)
    {
        var to = Now - (age ?? TimeSpan.Zero);
        return HistoryPlanner.Plan(to - range, to, Now, points);
    }

    [Fact] public void One_hour_uses_raw_data() =>
        Assert.Equal(new HistoryPlan(HistorySource.Raw, 3_600), Plan(TimeSpan.FromHours(1), 1000));

    [Fact] public void Five_minutes_keeps_sub_second_buckets() =>
        Assert.Equal(new HistoryPlan(HistorySource.Raw, 300), Plan(TimeSpan.FromMinutes(5), 1000));

    [Fact] public void Tiny_range_never_gets_a_zero_bucket() =>
        Assert.Equal(new HistoryPlan(HistorySource.Raw, 1),
                     HistoryPlanner.Plan(Now.AddMilliseconds(-1), Now, Now, 1000));

    [Fact] public void Exactly_one_minute_buckets_switch_to_the_rollup() =>
        Assert.Equal(new HistoryPlan(HistorySource.Rollup1m, 60_000), Plan(TimeSpan.FromHours(1), 60));

    [Fact] public void Rollup_buckets_are_rounded_up_to_whole_minutes() =>
        Assert.Equal(new HistoryPlan(HistorySource.Rollup1m, 120_000), Plan(TimeSpan.FromHours(2), 100));

    [Fact] public void Thirty_days_uses_the_rollup() =>
        Assert.Equal(new HistoryPlan(HistorySource.Rollup1m, 2_640_000), Plan(TimeSpan.FromDays(30), 1000));

    [Fact] public void Old_data_must_come_from_the_rollup_even_for_narrow_windows() =>
        Assert.Equal(new HistoryPlan(HistorySource.Rollup1m, 60_000),
                     Plan(TimeSpan.FromHours(1), 1000, age: TimeSpan.FromDays(100)));

    [Fact] public void Inverted_range_is_rejected() =>
        Assert.Throws<ArgumentException>(() => HistoryPlanner.Plan(Now, Now.AddHours(-1), Now, 100));
}