using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Monitor.Alerting.Tests;

[Collection("alerting")]
public class LifecycleTests(AlertingFixture f)
{
    private sealed record Setup(string Device, int Series, int Rule, FakeTimeProvider Clock);

    /// A device with a "> 80, immediately" rule.
    private async Task<Setup> ArrangeAsync()
    {
        await f.ResetAsync();
        var dev = f.NewDevice();
        var sid = await f.CreateSeriesAsync(dev, "temperature", "C");
        var rule = await f.InsertRuleAsync("High temperature", "threshold", ">", 80, 0, "critical", dev, "temperature");
        return new Setup(dev, sid, rule, new FakeTimeProvider(AlertingFixture.StartTime()));
    }

    [Fact]
    public async Task Disabling_a_rule_resolves_its_open_alerts_and_announces_it()
    {
        var s = await ArrangeAsync();
        await using var rig = new Rig(f, s.Clock);
        await using var tap = await EventTap.ConnectAsync(f.MqttHost, f.MqttPort);

        await Drive.RunAsync(f, s.Series, 1, 90, rig);
        Assert.False(Assert.Single(await f.AlertsAsync(s.Device)).Resolved);

        await f.ExecAsync("UPDATE alert_rules SET enabled = false WHERE id = @i", ("i", s.Rule));
        await rig.TickAsync();

        Assert.True(Assert.Single(await f.AlertsAsync(s.Device)).Resolved);
        await f.EventuallyAsync(() => tap.For(s.Device, "resolved").Count == 1, "the resolved event");
    }

    // Expected to FAIL until the prune fix from the top of this message is applied.
    [Fact]
    public async Task A_deleted_rule_resolves_its_open_alerts()
    {
        var s = await ArrangeAsync();
        await using var rig = new Rig(f, s.Clock);

        await Drive.RunAsync(f, s.Series, 1, 90, rig);
        await f.ExecAsync("DELETE FROM alert_rules WHERE id = @i", ("i", s.Rule));
        Assert.Null(Assert.Single(await f.AlertsAsync(s.Device)).RuleId);   // the FK nulled it

        await rig.TickAsync();

        Assert.True(Assert.Single(await f.AlertsAsync(s.Device)).Resolved);
    }

    [Fact]
    public async Task Orphaned_alerts_are_resolved_on_startup()
    {
        var s = await ArrangeAsync();
        await f.InsertOpenAlertAsync(null, s.Series, "left behind by a deleted rule");
        await using var rig = new Rig(f, s.Clock);                          // a fresh process

        await rig.TickAsync();

        Assert.True(Assert.Single(await f.AlertsAsync(s.Device)).Resolved);
    }

    [Fact]
    public async Task After_a_restart_an_open_alert_is_neither_duplicated_nor_announced_again()
    {
        var s = await ArrangeAsync();
        await using var tap = await EventTap.ConnectAsync(f.MqttHost, f.MqttPort);

        await using (var before = new Rig(f, s.Clock))
            await Drive.RunAsync(f, s.Series, 1, 90, before);
        await f.EventuallyAsync(() => tap.For(s.Device, "opened").Count == 1, "the first opened event");

        // "Restart": a new evaluator with an empty tracker, same database, condition still met.
        await using var after = new Rig(f, s.Clock);
        await Drive.RunAsync(f, s.Series, 3, 90, after);

        Assert.Single(await f.AlertsAsync(s.Device));
        await Task.Delay(500);
        Assert.Single(tap.For(s.Device, "opened"));

        await Drive.RunAsync(f, s.Series, 10, 70, after);                   // and it can still resolve
        Assert.True(Assert.Single(await f.AlertsAsync(s.Device)).Resolved);
        await f.EventuallyAsync(() => tap.For(s.Device, "resolved").Count == 1, "the resolved event");
    }

    [Fact]
    public async Task An_alert_whose_condition_cleared_while_the_worker_was_down_still_resolves()
    {
        var s = await ArrangeAsync();
        await using (var before = new Rig(f, s.Clock))
            await Drive.RunAsync(f, s.Series, 1, 90, before);
        Assert.False(Assert.Single(await f.AlertsAsync(s.Device)).Resolved);

        s.Clock.Advance(TimeSpan.FromMinutes(10));                          // the worker is down; the sensor recovers

        await using var after = new Rig(f, s.Clock);
        await Drive.RunAsync(f, s.Series, 8, 70, after);                    // never "met" in this process

        Assert.True(Assert.Single(await f.AlertsAsync(s.Device)).Resolved);
    }

    [Fact]
    public async Task Two_instances_ticking_at_once_create_one_alert_and_one_event_each_way()
    {
        var s = await ArrangeAsync();
        await using var a = new Rig(f, s.Clock);
        await using var b = new Rig(f, s.Clock);
        await using var tap = await EventTap.ConnectAsync(f.MqttHost, f.MqttPort);

        await Drive.RunAsync(f, s.Series, 1, 90, a, b);                     // both decide to open at the same instant
        Assert.Single(await f.AlertsAsync(s.Device));
        await f.EventuallyAsync(() => tap.For(s.Device, "opened").Count >= 1, "an opened event");
        await Task.Delay(500);
        Assert.Single(tap.For(s.Device, "opened"));

        await Drive.RunAsync(f, s.Series, 10, 70, a, b);                    // both try to resolve
        Assert.True(Assert.Single(await f.AlertsAsync(s.Device)).Resolved);
        await f.EventuallyAsync(() => tap.For(s.Device, "resolved").Count >= 1, "a resolved event");
        await Task.Delay(500);
        Assert.Single(tap.For(s.Device, "resolved"));
    }

    [Fact]
    public async Task A_failed_database_write_is_retried_on_the_next_tick()
    {
        var s = await ArrangeAsync();
        await using var rig = new Rig(f, s.Clock);
        await using var tap = await EventTap.ConnectAsync(f.MqttHost, f.MqttPort);

        await Drive.RunAsync(f, s.Series, 1, 70, rig);                      // healthy tick: startup restore is done

        await f.ExecAsync("ALTER TABLE alerts RENAME TO alerts_off");       // make every alert write fail
        try { await Drive.RunAsync(f, s.Series, 3, 90, rig); }              // must not throw
        finally { await f.ExecAsync("ALTER TABLE alerts_off RENAME TO alerts"); }

        Assert.Empty(await f.AlertsAsync(s.Device));
        Assert.Empty(tap.For(s.Device, "opened"));

        await Drive.RunAsync(f, s.Series, 1, 90, rig);                      // the condition is still met
        Assert.Single(await f.AlertsAsync(s.Device));
        await f.EventuallyAsync(() => tap.For(s.Device, "opened").Count == 1, "the opened event");
    }

    [Fact]
    public async Task An_unreachable_broker_does_not_stop_alerts_from_being_stored()
    {
        var s = await ArrangeAsync();
        await using var rig = new Rig(f, s.Clock, mqttPort: AlertingFixture.FreePort());   // nothing listens there

        await Drive.RunAsync(f, s.Series, 3, 90, rig);                      // must not throw

        Assert.False(Assert.Single(await f.AlertsAsync(s.Device)).Resolved);
    }
}

[Collection("alerting")]
public class EventTests(AlertingFixture f)
{
    [Fact]
    public async Task Opened_and_resolved_events_carry_the_alert_details_and_are_not_retained()
    {
        await f.ResetAsync();
        var dev = f.NewDevice();
        var sid = await f.CreateSeriesAsync(dev, "temperature", "C");
        await f.InsertRuleAsync("High temperature", "threshold", ">", 80, 0, "critical", dev, "temperature");
        await using var rig = new Rig(f, new FakeTimeProvider(AlertingFixture.StartTime()));
        await using var tap = await EventTap.ConnectAsync(f.MqttHost, f.MqttPort);

        await Drive.RunAsync(f, sid, 1, 90, rig);
        var row = Assert.Single(await f.AlertsAsync(dev));
        await f.EventuallyAsync(() => tap.For(dev, "opened").Count == 1, "the opened event");

        var opened = tap.For(dev, "opened").Single();
        Assert.Equal("opened", opened.Type);
        Assert.Equal(row.Id, opened.AlertId);
        Assert.Equal("critical", opened.Severity);
        Assert.Equal(("it", "line1", dev, "temperature"), (opened.Site, opened.Line, opened.Device, opened.Sensor));
        Assert.Equal(90, opened.Value);
        Assert.Contains("High temperature", opened.Message);

        await Drive.RunAsync(f, sid, 10, 70, rig);
        await f.EventuallyAsync(() => tap.For(dev, "resolved").Count == 1, "the resolved event");
        var resolved = tap.For(dev, "resolved").Single();
        Assert.Equal("resolved", resolved.Type);
        Assert.Equal(row.Id, resolved.AlertId);
        Assert.Equal(dev, resolved.Device);

        // Alerts are announcements, not state: a late subscriber must not get them replayed.
        await using var late = await EventTap.ConnectAsync(f.MqttHost, f.MqttPort);
        await Task.Delay(1000);
        Assert.Empty(late.Received);
        Assert.All(tap.Received.ToArray(), m => Assert.False(m.Retain));
    }
}