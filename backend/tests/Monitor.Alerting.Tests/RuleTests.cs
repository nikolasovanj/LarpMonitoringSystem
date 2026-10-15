using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Monitor.Alerting.Tests;

[Collection("alerting")]
public class ThresholdTests(AlertingFixture f)
{
    [Fact]
    public async Task Opens_after_for_seconds_and_resolves_after_the_resolve_hold()
    {
        await f.ResetAsync();
        var dev = f.NewDevice();
        var sid = await f.CreateSeriesAsync(dev, "temperature", "C");
        await f.InsertRuleAsync("High temperature", "threshold", ">", 80, 3, "critical", dev, "temperature");
        await using var rig = new Rig(f, new FakeTimeProvider(AlertingFixture.StartTime()));

        await Drive.RunAsync(f, sid, 3, 90, rig);                   // ticks at t0, t1, t2: held 0, 1, 2 s
        Assert.Empty(await f.AlertsAsync(dev));

        await Drive.RunAsync(f, sid, 1, 90, rig);                   // t3: held 3 s -> opens
        var alert = Assert.Single(await f.AlertsAsync(dev));
        Assert.False(alert.Resolved);
        Assert.Equal("critical", alert.Severity);
        Assert.Equal(90, alert.Value);
        Assert.Contains("High temperature", alert.Message);
        Assert.Contains($"[{dev}]", alert.Message);

        // Last high sample is at H. The 2 s window still contains it through H+2, so the condition
        // clears at H+3 and the 5 s hold ends at H+8. After 7 ticks it must still be open.
        await Drive.RunAsync(f, sid, 7, 70, rig);
        Assert.False(Assert.Single(await f.AlertsAsync(dev)).Resolved);

        await Drive.RunAsync(f, sid, 3, 70, rig);
        Assert.True(Assert.Single(await f.AlertsAsync(dev)).Resolved);
    }

    [Theory]
    [InlineData(0.2, true)]
    [InlineData(0.6, true)]
    [InlineData(1.8, true)]
    [InlineData(2.2, false)]
    public async Task A_lone_spike_is_caught_while_it_is_inside_the_window(double secondsAgo, bool expectAlert)
    {
        await f.ResetAsync();
        var dev = f.NewDevice();
        var sid = await f.CreateSeriesAsync(dev, "temperature", "C");
        await f.InsertRuleAsync("Spike", "threshold", ">", 80, 0, "warning", dev, "temperature");
        var clock = new FakeTimeProvider(AlertingFixture.StartTime());
        await using var rig = new Rig(f, clock);

        var now = clock.GetUtcNow();
        await f.InsertSampleAsync(sid, now.AddSeconds(-3), 70);
        await f.InsertSampleAsync(sid, now.AddSeconds(-secondsAgo), 90);   // the only high sample
        await f.InsertSampleAsync(sid, now, 70);
        await rig.TickAsync();

        Assert.Equal(expectAlert ? 1 : 0, (await f.AlertsAsync(dev)).Count);
    }

    [Fact]
    public async Task A_less_than_rule_looks_at_the_window_minimum()
    {
        await f.ResetAsync();
        var dev = f.NewDevice();
        var sid = await f.CreateSeriesAsync(dev, "pressure", "bar");
        await f.InsertRuleAsync("Low pressure", "threshold", "<", 5.2, 0, "warning", dev, "pressure");
        var clock = new FakeTimeProvider(AlertingFixture.StartTime());
        await using var rig = new Rig(f, clock);

        var now = clock.GetUtcNow();
        await f.InsertSampleAsync(sid, now.AddSeconds(-1), 5.0);            // dip
        await f.InsertSampleAsync(sid, now, 6.0);                           // newest is healthy
        await rig.TickAsync();

        var alert = Assert.Single(await f.AlertsAsync(dev));
        Assert.Equal(5.0, alert.Value);
    }

    [Fact]
    public async Task A_silent_sensor_leaves_an_open_threshold_alert_open()
    {
        await f.ResetAsync();
        var dev = f.NewDevice();
        var sid = await f.CreateSeriesAsync(dev, "temperature", "C");
        await f.InsertRuleAsync("High temperature", "threshold", ">", 80, 0, "critical", dev, "temperature");
        await using var rig = new Rig(f, new FakeTimeProvider(AlertingFixture.StartTime()));

        await Drive.RunAsync(f, sid, 1, 90, rig);
        Assert.Single(await f.AlertsAsync(dev));

        await Drive.RunAsync(f, sid, 30, null, rig);                        // no data: "unknown", not "clear"
        Assert.False(Assert.Single(await f.AlertsAsync(dev)).Resolved);
    }
}

[Collection("alerting")]
public class ZScoreTests(AlertingFixture f)
{
    // Baseline alternates 70 +/- alt (std ~ 0.5 when alt = 0.5). The window then contains a sample of 75.
    [Theory]
    [InlineData(298, 0.5, true)]    // plenty of baseline: 75 is ~10 std devs away
    [InlineData(10, 0.5, false)]    // fewer than 30 baseline samples: not enough to judge
    [InlineData(298, 0.0, false)]   // constant baseline: std = 0, a z-score is meaningless
    public async Task Anomaly_detection(int baselineSamples, double alt, bool expectAlert)
    {
        await f.ResetAsync();
        var dev = f.NewDevice();
        var sid = await f.CreateSeriesAsync(dev, "temperature", "C");
        await f.InsertRuleAsync("Statistical anomaly", "zscore", null, 5, 0, "info", dev, "*");
        var clock = new FakeTimeProvider(AlertingFixture.StartTime());
        await using var rig = new Rig(f, clock);

        var now = clock.GetUtcNow();
        // Baseline ends at now-3 s, so it excludes the 2 s window being judged.
        await f.SeedAsync(sid, now.AddSeconds(-(baselineSamples + 2)), baselineSamples, 1, 70, alt);
        await f.InsertSampleAsync(sid, now.AddSeconds(-2), 70);
        await f.InsertSampleAsync(sid, now.AddSeconds(-1), 70);
        await f.InsertSampleAsync(sid, now, 75);
        await rig.TickAsync();

        var alerts = await f.AlertsAsync(dev);
        if (!expectAlert) { Assert.Empty(alerts); return; }

        var a = Assert.Single(alerts);
        Assert.Equal(75, a.Value);
        Assert.Contains("std devs", a.Message);
        Assert.Equal("info", a.Severity);
    }
}

[Collection("alerting")]
public class StaleTests(AlertingFixture f)
{
    [Fact]
    public async Task Opens_just_after_the_limit_and_resolves_when_data_returns()
    {
        await f.ResetAsync();
        var dev = f.NewDevice();
        var sid = await f.CreateSeriesAsync(dev, "temperature", "C");
        await f.InsertRuleAsync("Sensor silent", "stale", null, null, 30, "warning", dev, "temperature");
        await using var rig = new Rig(f, new FakeTimeProvider(AlertingFixture.StartTime()));

        await Drive.RunAsync(f, sid, 1, 70, rig);                   // last sample at t0, tick at t0
        await Drive.RunAsync(f, sid, 30, null, rig);                // ticks at t0+1 .. t0+30: age is 30 s at most
        Assert.Empty(await f.AlertsAsync(dev));                     // exactly 30 s is not "more than 30 s"

        await Drive.RunAsync(f, sid, 1, null, rig);                 // t0+31: age 31 s
        var alert = Assert.Single(await f.AlertsAsync(dev));
        Assert.False(alert.Resolved);
        Assert.Contains("no data for 31s", alert.Message);

        await Drive.RunAsync(f, sid, 1, 70, rig);                   // data is back at t0+32
        await Drive.RunAsync(f, sid, 4, 70, rig);                   // clear since t0+32: hold ends at t0+37
        Assert.False(Assert.Single(await f.AlertsAsync(dev)).Resolved);
        await Drive.RunAsync(f, sid, 3, 70, rig);
        Assert.True(Assert.Single(await f.AlertsAsync(dev)).Resolved);
    }

    [Fact]
    public async Task Series_with_no_data_in_the_lookback_are_treated_as_retired()
    {
        await f.ResetAsync();
        var dev = f.NewDevice();
        var retired = await f.CreateSeriesAsync(dev, "old_sensor", "C");
        var quiet = await f.CreateSeriesAsync(dev, "quiet_sensor", "C");
        await f.InsertRuleAsync("Sensor silent", "stale", null, null, 30, "warning", dev, "*");
        var clock = new FakeTimeProvider(AlertingFixture.StartTime());
        await using var rig = new Rig(f, clock);

        var now = clock.GetUtcNow();
        await f.InsertSampleAsync(retired, now.AddHours(-25), 1);   // outside the 24 h lookback
        await f.InsertSampleAsync(quiet, now.AddHours(-23), 1);     // inside it, and long silent
        await rig.TickAsync();

        var alert = Assert.Single(await f.AlertsAsync(dev));
        Assert.Contains("no data for", alert.Message);
        // exactly one of the two series has an alert, and it is the quiet one
        Assert.Equal(1, await CountForSeriesAsync(quiet));
        Assert.Equal(0, await CountForSeriesAsync(retired));
    }

    [Fact]
    public async Task Wildcard_sensor_matches_every_sensor_of_the_scoped_device_only()
    {
        await f.ResetAsync();
        var dev = f.NewDevice();
        var other = f.NewDevice();
        var temp = await f.CreateSeriesAsync(dev, "temperature", "C");
        var pres = await f.CreateSeriesAsync(dev, "pressure", "bar");
        var foreign = await f.CreateSeriesAsync(other, "temperature", "C");
        await f.InsertRuleAsync("Sensor silent", "stale", null, null, 30, "warning", dev, "*");
        var clock = new FakeTimeProvider(AlertingFixture.StartTime());
        await using var rig = new Rig(f, clock);

        var old = clock.GetUtcNow().AddSeconds(-60);
        foreach (var s in new[] { temp, pres, foreign }) await f.InsertSampleAsync(s, old, 1);
        await rig.TickAsync();

        Assert.Equal(2, (await f.AlertsAsync(dev)).Count);
        Assert.Empty(await f.AlertsAsync(other));
    }

    private async Task<long> CountForSeriesAsync(int seriesId)
    {
        await using var cmd = f.Db.CreateCommand("SELECT count(*) FROM alerts WHERE series_id = @s");
        cmd.Parameters.AddWithValue("s", seriesId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}