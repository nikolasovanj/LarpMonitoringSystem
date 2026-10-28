using Monitor.Core;
using Xunit;

public class ConditionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly SeriesKey Key = new("s", "l", "d", "temperature");

    private static SeriesSnapshot Snap(double? min = null, double? max = null, long n = 1,
        DateTimeOffset? last = null, double? mean = null, double? std = null, long bn = 100) =>
        new(1, Key, "C", last ?? T0, min, max, n, mean, std, bn);

    private static AlertRule Rule(RuleKind k, string? op = null, double? th = null, int forS = 0,
                                  string sensor = "temperature", string? device = null) =>
        new(1, "R", k, op, th, forS, "warning", null, null, device, sensor);

    [Fact]
    public void Greater_than_uses_the_window_maximum()
    {
        var r = Rule(RuleKind.Threshold, ">", 80);
        var met = ConditionEvaluator.Evaluate(r, Snap(min: 70, max: 85), T0);
        Assert.Equal(ConditionState.Met, met.State);
        Assert.Equal(85, met.Value);
        Assert.Equal(ConditionState.Clear, ConditionEvaluator.Evaluate(r, Snap(min: 70, max: 79.9), T0).State);
    }

    [Fact]
    public void Less_than_uses_the_window_minimum()
    {
        var r = Rule(RuleKind.Threshold, "<", 5);
        var met = ConditionEvaluator.Evaluate(r, Snap(min: 4.9, max: 9), T0);
        Assert.Equal(ConditionState.Met, met.State);
        Assert.Equal(4.9, met.Value);
        Assert.Equal(ConditionState.Clear, ConditionEvaluator.Evaluate(r, Snap(min: 5.0, max: 9), T0).State);
    }

    [Fact]
    public void Operator_boundaries()
    {
        Assert.Equal(ConditionState.Clear, ConditionEvaluator.Evaluate(Rule(RuleKind.Threshold, ">", 80), Snap(max: 80, min: 80), T0).State);
        Assert.Equal(ConditionState.Met,   ConditionEvaluator.Evaluate(Rule(RuleKind.Threshold, ">=", 80), Snap(max: 80, min: 80), T0).State);
        Assert.Equal(ConditionState.Clear, ConditionEvaluator.Evaluate(Rule(RuleKind.Threshold, "<", 5), Snap(max: 5, min: 5), T0).State);
        Assert.Equal(ConditionState.Met,   ConditionEvaluator.Evaluate(Rule(RuleKind.Threshold, "<=", 5), Snap(max: 5, min: 5), T0).State);
    }

    [Fact]
    public void Threshold_with_no_data_in_the_window_is_unknown() =>
        Assert.Equal(ConditionState.Unknown,
            ConditionEvaluator.Evaluate(Rule(RuleKind.Threshold, ">", 1), Snap(n: 0), T0).State);

    [Fact]
    public void ZScore_flags_the_farthest_sample_in_either_direction()
    {
        var r = Rule(RuleKind.ZScore, th: 4);
        var up = ConditionEvaluator.Evaluate(r, Snap(min: 70, max: 72.5, mean: 70, std: 0.5), T0);    // z = 5
        Assert.Equal(ConditionState.Met, up.State);
        Assert.Equal(72.5, up.Value);

        var down = ConditionEvaluator.Evaluate(r, Snap(min: 67, max: 70, mean: 70, std: 0.5), T0);    // z = 6
        Assert.Equal(ConditionState.Met, down.State);
        Assert.Equal(67, down.Value);

        Assert.Equal(ConditionState.Clear,
            ConditionEvaluator.Evaluate(r, Snap(min: 69.5, max: 71.5, mean: 70, std: 0.5), T0).State); // z = 3
    }

    [Fact]
    public void ZScore_without_a_usable_baseline_is_unknown()
    {
        var r = Rule(RuleKind.ZScore, th: 4);
        Assert.Equal(ConditionState.Unknown, ConditionEvaluator.Evaluate(r, Snap(min: 0, max: 99, mean: 70, std: 0.5, bn: 29), T0).State);
        Assert.Equal(ConditionState.Unknown, ConditionEvaluator.Evaluate(r, Snap(min: 0, max: 99, mean: 70, std: 0.0), T0).State);
        Assert.Equal(ConditionState.Unknown, ConditionEvaluator.Evaluate(r, Snap(min: 0, max: 99, mean: null, std: null), T0).State);
    }

    [Fact]
    public void Stale_fires_only_after_the_limit()
    {
        var r = Rule(RuleKind.Stale, forS: 30);
        Assert.Equal(ConditionState.Met,   ConditionEvaluator.Evaluate(r, Snap(last: T0.AddSeconds(-31)), T0).State);
        Assert.Equal(ConditionState.Clear, ConditionEvaluator.Evaluate(r, Snap(last: T0.AddSeconds(-30)), T0).State);
        Assert.Equal(ConditionState.Clear, ConditionEvaluator.Evaluate(r, Snap(last: T0.AddSeconds(-29)), T0).State);
    }

    [Fact]
    public void A_series_with_no_data_at_all_is_treated_as_retired() =>
        Assert.Equal(ConditionState.Clear,
            ConditionEvaluator.Evaluate(Rule(RuleKind.Stale, forS: 30), new SeriesSnapshot(1, Key, null, null, null, null, 0, null, null, 0), T0).State);

    [Fact]
    public void Rule_scope_and_wildcards()
    {
        Assert.True(Rule(RuleKind.Stale, forS: 1, sensor: "*").Matches(Key));
        Assert.True(Rule(RuleKind.Stale, forS: 1, sensor: "temperature").Matches(Key));
        Assert.False(Rule(RuleKind.Stale, forS: 1, sensor: "pressure").Matches(Key));
        Assert.True(Rule(RuleKind.Stale, forS: 1, device: "d").Matches(Key));
        Assert.False(Rule(RuleKind.Stale, forS: 1, device: "other").Matches(Key));
    }
}

public class TrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int s) => T0.AddSeconds(s);
    private const ConditionState Met = ConditionState.Met, Clear = ConditionState.Clear, Unknown = ConditionState.Unknown;

    private static AlertTracker New() => new(TimeSpan.FromSeconds(5));

    [Fact]
    public void Opens_only_after_the_condition_has_held_for_the_required_time()
    {
        var t = New();
        Assert.Equal(Transition.None, t.Update(1, 1, 3, Met, At(0)));
        Assert.Equal(Transition.None, t.Update(1, 1, 3, Met, At(2)));
        Assert.Equal(Transition.Open, t.Update(1, 1, 3, Met, At(3)));
        Assert.Equal(Transition.None, t.Update(1, 1, 3, Met, At(4)));      // already open
    }

    [Fact]
    public void A_clear_tick_restarts_the_hold_timer()
    {
        var t = New();
        t.Update(1, 1, 3, Met, At(0));
        t.Update(1, 1, 3, Clear, At(1));
        Assert.Equal(Transition.None, t.Update(1, 1, 3, Met, At(2)));
        Assert.Equal(Transition.None, t.Update(1, 1, 3, Met, At(4)));
        Assert.Equal(Transition.Open, t.Update(1, 1, 3, Met, At(5)));
    }

    [Fact]
    public void Zero_hold_opens_immediately() =>
        Assert.Equal(Transition.Open, New().Update(1, 1, 0, Met, At(0)));

    [Fact]
    public void Resolves_only_after_staying_clear_for_the_resolve_hold()
    {
        var t = New();
        t.Update(1, 1, 0, Met, At(0));
        Assert.Equal(Transition.None, t.Update(1, 1, 0, Clear, At(10)));
        Assert.Equal(Transition.None, t.Update(1, 1, 0, Clear, At(14)));
        Assert.Equal(Transition.Resolve, t.Update(1, 1, 0, Clear, At(15)));
        Assert.Equal(Transition.None, t.Update(1, 1, 0, Clear, At(30)));   // nothing left to resolve
    }

    [Fact]
    public void A_flap_during_the_resolve_hold_keeps_the_alert_open()
    {
        var t = New();
        t.Update(1, 1, 0, Met, At(0));
        t.Update(1, 1, 0, Clear, At(10));
        t.Update(1, 1, 0, Met, At(13));                                      // came back
        Assert.Equal(Transition.None, t.Update(1, 1, 0, Clear, At(14)));
        Assert.Equal(Transition.None, t.Update(1, 1, 0, Clear, At(18)));     // only 4 s since the new clear
        Assert.Equal(Transition.Resolve, t.Update(1, 1, 0, Clear, At(19)));
    }

    [Fact]
    public void Unknown_changes_nothing()
    {
        var t = New();
        t.Update(1, 1, 0, Met, At(0));
        Assert.Equal(Transition.None, t.Update(1, 1, 0, Unknown, At(100)));
        t.Update(1, 1, 0, Clear, At(101));
        Assert.Equal(Transition.Resolve, t.Update(1, 1, 0, Clear, At(106)));  // still open until now
    }

    [Fact]
    public void Restored_alerts_are_not_opened_again_but_can_resolve()
    {
        var t = New();
        t.Restore(1, 1);
        Assert.Equal(Transition.None, t.Update(1, 1, 0, Met, At(0)));
        t.Update(1, 1, 0, Clear, At(1));
        Assert.Equal(Transition.Resolve, t.Update(1, 1, 0, Clear, At(6)));
    }

    [Fact]
    public void Undo_makes_a_failed_transition_retry()
    {
        var t = New();
        Assert.Equal(Transition.Open, t.Update(1, 1, 0, Met, At(0)));
        t.Undo(1, 1, Transition.Open);
        Assert.Equal(Transition.Open, t.Update(1, 1, 0, Met, At(1)));

        t.Update(1, 1, 0, Clear, At(2));
        Assert.Equal(Transition.Resolve, t.Update(1, 1, 0, Clear, At(7)));
        t.Undo(1, 1, Transition.Resolve);
        Assert.Equal(Transition.None, t.Update(1, 1, 0, Met, At(8)));        // open again, nothing to do
    }

    [Fact]
    public void Rules_and_series_are_tracked_independently()
    {
        var t = New();
        Assert.Equal(Transition.Open, t.Update(1, 1, 0, Met, At(0)));
        Assert.Equal(Transition.None, t.Update(1, 2, 3, Met, At(0)));
        Assert.Equal(Transition.Open, t.Update(2, 1, 0, Met, At(0)));
    }

    [Fact]
    public void Prune_returns_open_keys_that_are_no_longer_active_until_forgotten()
    {
        var t = New();
        t.Update(1, 1, 0, Met, At(0));       // open
        t.Update(2, 2, 0, Clear, At(0));     // idle
        var gone = t.Prune([]);
        Assert.Equal([(1, 1)], gone);
        Assert.Equal([(1, 1)], t.Prune([]));  // still reported: not forgotten yet
        t.Forget(1, 1);
        Assert.Empty(t.Prune([]));
    }
}