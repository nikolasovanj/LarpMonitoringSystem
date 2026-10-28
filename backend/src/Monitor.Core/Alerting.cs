namespace Monitor.Core;

public enum RuleKind { Threshold, ZScore, Stale }
public enum ConditionState { Clear, Met, Unknown }
public enum Transition { None, Open, Resolve }

public sealed record AlertRule(int Id, string Name, RuleKind Kind, string? Operator, double? Threshold,
    int ForSeconds, string Severity, string? Site, string? Line, string? Device, string Sensor)
{
    /// null scope fields and sensor "*" are wildcards.
    public bool Matches(SeriesKey k) =>
        (Site is null || Site == k.Site) && (Line is null || Line == k.Line) &&
        (Device is null || Device == k.Device) && (Sensor == "*" || Sensor == k.Sensor);
}

/// What the database says about one series right now.
public sealed record SeriesSnapshot(int SeriesId, SeriesKey Key, string? Unit, DateTimeOffset? LastTime,
    double? WindowMin, double? WindowMax, long WindowCount,
    double? BaselineMean, double? BaselineStd, long BaselineCount);

public readonly record struct Condition(ConditionState State, double? Value = null, string Detail = "")
{
    public static readonly Condition Clear = new(ConditionState.Clear);
    public static readonly Condition Unknown = new(ConditionState.Unknown);
}

public static class ConditionEvaluator
{
    public const long MinBaselineSamples = 30;

    public static Condition Evaluate(AlertRule rule, SeriesSnapshot s, DateTimeOffset now) => rule.Kind switch
    {
        RuleKind.Stale => Stale(rule, s, now),
        RuleKind.Threshold => Threshold(rule, s),
        RuleKind.ZScore => ZScore(rule, s),
        _ => Condition.Unknown,
    };

    // A series with no data at all in the lookback is treated as retired, not as an outage.
    private static Condition Stale(AlertRule rule, SeriesSnapshot s, DateTimeOffset now)
    {
        if (s.LastTime is not { } last) return Condition.Clear;
        var age = (now - last).TotalSeconds;
        return age > rule.ForSeconds
            ? new Condition(ConditionState.Met, null, $"no data for {age:F0}s")
            : Condition.Clear;
    }

    private static Condition Threshold(AlertRule rule, SeriesSnapshot s)
    {
        if (rule.Threshold is not { } th || rule.Operator is null || s.WindowCount == 0 ||
            s.WindowMin is null || s.WindowMax is null) return Condition.Unknown;

        var v = rule.Operator is ">" or ">=" ? s.WindowMax.Value : s.WindowMin.Value;
        var met = rule.Operator switch
        {
            ">" => v > th, ">=" => v >= th, "<" => v < th, "<=" => v <= th, _ => false,
        };
        return met
            ? new Condition(ConditionState.Met, v, $"{s.Key.Sensor} {v:G5} {rule.Operator} {th:G5}")
            : Condition.Clear;
    }

    private static Condition ZScore(AlertRule rule, SeriesSnapshot s)
    {
        if (rule.Threshold is not { } limit || s.WindowCount == 0 ||
            s.WindowMin is not { } lo || s.WindowMax is not { } hi ||
            s.BaselineMean is not { } mean || s.BaselineStd is not { } std ||
            s.BaselineCount < MinBaselineSamples || !(std > 1e-9)) return Condition.Unknown;

        var worst = Math.Abs(hi - mean) >= Math.Abs(lo - mean) ? hi : lo;
        var z = Math.Abs(worst - mean) / std;
        return z > limit
            ? new Condition(ConditionState.Met, worst,
                $"{s.Key.Sensor} {worst:G5} is {z:F1} std devs from its baseline {mean:G5}")
            : Condition.Clear;
    }
}

/// Turns a stream of condition results into Open/Resolve transitions.
/// Not thread-safe: one evaluator loop owns it.
public sealed class AlertTracker(TimeSpan resolveHold)
{
    private sealed class State
    {
        public DateTimeOffset? PendingSince, ClearSince;
        public bool Open;
    }

    private readonly Dictionary<(int Rule, int Series), State> _states = new();

    private State Get(int rule, int series)
    {
        if (!_states.TryGetValue((rule, series), out var st)) _states[(rule, series)] = st = new State();
        return st;
    }

    /// Mark an alert that is already open in the database.
    public void Restore(int ruleId, int seriesId) => Get(ruleId, seriesId).Open = true;

    public Transition Update(int ruleId, int seriesId, int holdSeconds, ConditionState c, DateTimeOffset now)
    {
        if (c == ConditionState.Unknown) return Transition.None;     // no data: keep whatever state we had
        var st = Get(ruleId, seriesId);

        if (c == ConditionState.Met)
        {
            st.ClearSince = null;
            if (st.Open) return Transition.None;
            st.PendingSince ??= now;
            if (now - st.PendingSince.Value < TimeSpan.FromSeconds(holdSeconds)) return Transition.None;
            st.Open = true;
            st.PendingSince = null;
            return Transition.Open;
        }

        st.PendingSince = null;
        if (!st.Open) return Transition.None;
        st.ClearSince ??= now;
        if (now - st.ClearSince.Value < resolveHold) return Transition.None;
        st.Open = false;
        st.ClearSince = null;
        return Transition.Resolve;
    }

    /// The database write for a transition failed: go back so the next tick retries it.
    public void Undo(int ruleId, int seriesId, Transition t)
    {
        var st = Get(ruleId, seriesId);
        if (t == Transition.Open) st.Open = false;
        else if (t == Transition.Resolve) st.Open = true;
    }

    /// Drops idle state for keys that no longer exist and returns OPEN keys that are no longer
    /// active (rule deleted or disabled). Call Forget on each once its alert is resolved.
    public IReadOnlyList<(int Rule, int Series)> Prune(HashSet<(int, int)> active)
    {
        var open = new List<(int, int)>();
        foreach (var (key, st) in _states.ToArray())
        {
            if (active.Contains(key)) continue;
            if (st.Open) open.Add(key); else _states.Remove(key);
        }
        return open;
    }

    public void Forget(int ruleId, int seriesId) => _states.Remove((ruleId, seriesId));
}