using Monitor.Contracts;
using Monitor.Core;
using Microsoft.Extensions.Options;

namespace Monitor.Alerting;

public sealed class AlertEvaluator(AlertStore store, AlertPublisher publisher, IOptions<AlertingOptions> options,
    TimeProvider clock, ILogger<AlertEvaluator> log) : BackgroundService
{
    private readonly AlertTracker _tracker = new(TimeSpan.FromSeconds(options.Value.ResolveHoldSeconds));
    private bool _restored;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.IntervalSeconds));
        try
        {
            do
            {
                try { await TickAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { log.LogError(ex, "Evaluation tick failed, will retry on the next tick"); }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        var o = options.Value;

        if (!_restored)    // retried every tick until the database is reachable
        {
            await store.ResolveOrphansAsync(ct);
            foreach (var (rule, series) in await store.OpenAlertsAsync(ct)) _tracker.Restore(rule, series);
            _restored = true;
        }

        var now = clock.GetUtcNow();
        var rules = await store.LoadRulesAsync(ct);
        var windowFrom = now - TimeSpan.FromSeconds(o.WindowSeconds);
        var snaps = await store.LoadSnapshotsAsync(new SnapshotWindow(
            Lookback: now - TimeSpan.FromHours(o.LookbackHours),
            WindowFrom: windowFrom,
            BaselineFrom: now - TimeSpan.FromMinutes(o.BaselineMinutes),
            BaselineTo: windowFrom,                       // the baseline excludes the window being judged
            IncludeBaseline: rules.Any(r => r.Kind == RuleKind.ZScore)), ct);

        var byId = snaps.ToDictionary(s => s.SeriesId);
        var active = new HashSet<(int, int)>();

        foreach (var rule in rules)
            foreach (var snap in snaps)
            {
                if (!rule.Matches(snap.Key)) continue;
                active.Add((rule.Id, snap.SeriesId));

                var cond = ConditionEvaluator.Evaluate(rule, snap, now);
                var hold = rule.Kind == RuleKind.Stale ? 0 : rule.ForSeconds;   // stale already encodes its time limit
                switch (_tracker.Update(rule.Id, snap.SeriesId, hold, cond.State, now))
                {
                    case Transition.Open:
                        if (!await TryOpenAsync(rule, snap, cond, ct))
                            _tracker.Undo(rule.Id, snap.SeriesId, Transition.Open);
                        break;
                    case Transition.Resolve:
                        if (!await TryResolveAsync(rule.Id, snap.SeriesId, snap.Key, ct))
                            _tracker.Undo(rule.Id, snap.SeriesId, Transition.Resolve);
                        break;
                }
            }

        // Alerts whose rule was disabled or deleted, or whose series disappeared.
        var pruned = _tracker.Prune(active);
        if(pruned.Count > 0) await store.ResolveOrphansAsync(ct);
        foreach (var (ruleId, seriesId) in pruned)
            if (await TryResolveAsync(ruleId, seriesId, byId.TryGetValue(seriesId, out var s) ? s.Key : null, ct))
                _tracker.Forget(ruleId, seriesId);
    }

    private async Task<bool> TryOpenAsync(AlertRule rule, SeriesSnapshot snap, Condition cond, CancellationToken ct)
    {
        var message = $"{rule.Name}: {cond.Detail} [{snap.Key.Device}]";
        try
        {
            var id = await store.OpenAsync(rule.Id, snap.SeriesId, rule.Severity, message, cond.Value, ct);
            if (id is null) return true;           // already open (another instance, or restored state was stale)

            log.LogWarning("ALERT opened #{Id} ({Severity}): {Message}", id, rule.Severity, message);
            await publisher.PublishAsync(new AlertEvent("opened", id.Value, rule.Severity, message,
                snap.Key.Site, snap.Key.Line, snap.Key.Device, snap.Key.Sensor, cond.Value, clock.GetUtcNow()), ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not open alert for rule {Rule} series {Series}; will retry", rule.Id, snap.SeriesId);
            return false;
        }
    }

    private async Task<bool> TryResolveAsync(int ruleId, int seriesId, SeriesKey? key, CancellationToken ct)
    {
        try
        {
            var r = await store.ResolveAsync(ruleId, seriesId, ct);
            if (r is null) return true;            // nothing open: fine

            log.LogInformation("ALERT resolved #{Id}: {Message}", r.Id, r.Message);
            await publisher.PublishAsync(new AlertEvent("resolved", r.Id, r.Severity, r.Message,
                key?.Site, key?.Line, key?.Device, key?.Sensor, r.Value, clock.GetUtcNow()), ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not resolve alert for rule {Rule} series {Series}; will retry", ruleId, seriesId);
            return false;
        }
    }
}