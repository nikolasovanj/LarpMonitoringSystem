using System.Net;
using System.Net.Http.Json;
using System.Text;
using Monitor.Contracts;
using Xunit;

namespace Monitor.Api.Tests;

[Collection("api")]
public class DevicesTests(ApiFixture f)
{
    [Fact]
    public async Task Lists_sensors_latest_values_and_status()
    {
        var dev = f.NewDevice();
        var temp = await f.CreateSeriesAsync(dev, "temperature", "C");
        var pres = await f.CreateSeriesAsync(dev, "pressure", "bar");

        var t0 = ApiFixture.Ago(TimeSpan.FromMinutes(10));
        await f.ExecAsync("""
            INSERT INTO telemetry (time, series_id, value) VALUES
              (@a, @t, 70.0), (@b, @t, 71.5), (@c, @p, 6.1)
            """,
            ("a", t0.AddSeconds(-60)), ("b", t0), ("c", t0.AddSeconds(-30)), ("t", temp), ("p", pres));
        // last_seen is OLDER than the newest sample, so the sample time must win
        await f.ExecAsync("""
            INSERT INTO device_status (site, line, device, status, status_since, last_seen)
            VALUES ('it', 'line1', @d, 'online', @s, @s)
            """, ("d", dev), ("s", t0.AddMinutes(-5)));

        var devices = await f.Http.GetFromJsonAsync<List<DeviceDto>>("/api/devices");
        var d = Assert.Single(devices!, x => x.Device == dev);

        Assert.Equal("online", d.Status);
        Assert.Equal(t0.AddMinutes(-5), d.StatusSince!.Value.UtcDateTime);
        Assert.Equal(t0, d.LastSeen!.Value.UtcDateTime);
        Assert.Equal(new[] { "pressure", "temperature" }, d.Sensors.Select(s => s.Sensor));   // ordered by name

        var t = d.Sensors.Single(s => s.Sensor == "temperature");
        Assert.Equal(temp, t.SeriesId);
        Assert.Equal(71.5, t.LatestValue);                                                    // the newest, not the first
        Assert.Equal("C", t.Unit);
        Assert.Equal(t0, t.LatestTime!.Value.UtcDateTime);

        var p = d.Sensors.Single(s => s.Sensor == "pressure");
        Assert.Equal(6.1, p.LatestValue);
        Assert.Equal("bar", p.Unit);
    }

    [Fact]
    public async Task A_device_without_status_or_samples_is_unknown_with_null_values()
    {
        var dev = f.NewDevice();
        await f.CreateSeriesAsync(dev, "flow", "L/min");

        var devices = await f.Http.GetFromJsonAsync<List<DeviceDto>>("/api/devices");
        var d = Assert.Single(devices!, x => x.Device == dev);

        Assert.Equal("unknown", d.Status);
        Assert.Null(d.StatusSince);
        Assert.Null(d.LastSeen);
        var s = Assert.Single(d.Sensors);
        Assert.Null(s.LatestValue);
        Assert.Null(s.LatestTime);
    }
}

[Collection("api")]
public class HistoryTests(ApiFixture f)
{
    private Task<HttpResponseMessage> Get(int id, string query) => f.Http.GetAsync($"/api/series/{id}/history?{query}");

    [Fact]
    public async Task Short_windows_come_from_raw_data_in_fixed_buckets()
    {
        var dev = f.NewDevice();
        var id = await f.CreateSeriesAsync(dev, "vibration", "mm/s");
        var t0 = ApiFixture.Ago(TimeSpan.FromMinutes(20), wholeMinute: true);   // minute-aligned = 6 s-aligned

        // one sample per second, value = seconds since t0
        await f.ExecAsync("""
            INSERT INTO telemetry (time, series_id, value)
            SELECT @t0 + make_interval(secs => i), @id, i FROM generate_series(0, 599) AS i
            """, ("t0", t0), ("id", id));

        var resp = await Get(id, $"from={ApiFixture.Url(t0)}&to={ApiFixture.Url(t0.AddMinutes(10))}&maxPoints=100");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var h = (await resp.Content.ReadFromJsonAsync<HistoryDto>())!;

        Assert.Equal("Raw", h.Source);
        Assert.Equal(6_000, h.BucketMs);
        Assert.Equal(100, h.Points.Count);
        Assert.All(h.Points, p => Assert.Equal(6, p.Samples));
        Assert.Equal(600, h.Points.Sum(p => p.Samples));
        Assert.Equal(t0, h.Points[0].Time.UtcDateTime);
        Assert.Equal(2.5, h.Points[0].Avg);                    // mean of 0..5
        Assert.Equal(0, h.Points[0].Min);
        Assert.Equal(599, h.Points[^1].Max);
        Assert.Equal(h.Points.OrderBy(p => p.Time).Select(p => p.Time), h.Points.Select(p => p.Time));
    }

    [Fact]
    public async Task Wide_windows_use_the_rollup_with_a_sample_weighted_mean()
    {
        var dev = f.NewDevice();
        var id = await f.CreateSeriesAsync(dev, "pressure", "bar");

        // A 15-minute-aligned bucket start 3 h ago (alignment is the same for epoch and Timescale's origin).
        var raw = DateTime.UtcNow.AddHours(-3);
        var bucketStart = new DateTime(raw.Ticks - raw.Ticks % TimeSpan.FromMinutes(15).Ticks, DateTimeKind.Utc);

        // minute 1: 60 samples of 10.  minute 2: ONE sample of 100.  Same 15-minute bucket.
        await f.ExecAsync("""
            INSERT INTO telemetry (time, series_id, value)
            SELECT @b + make_interval(secs => i), @id, 10 FROM generate_series(0, 59) AS i;
            INSERT INTO telemetry (time, series_id, value)
            VALUES (@b + interval '70 seconds', @id, 100)
            """, ("b", bucketStart), ("id", id));
        await f.ExecAsync("CALL refresh_continuous_aggregate('telemetry_1m', NULL, NULL)");

        // 25 h / 100 points = exactly 15-minute buckets
        var to = ApiFixture.Ago(TimeSpan.Zero);
        var resp = await Get(id, $"from={ApiFixture.Url(to.AddHours(-25))}&to={ApiFixture.Url(to)}&maxPoints=100");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var h = (await resp.Content.ReadFromJsonAsync<HistoryDto>())!;

        Assert.Equal("Rollup1m", h.Source);
        Assert.Equal(900_000, h.BucketMs);
        var p = Assert.Single(h.Points);
        Assert.Equal(bucketStart, p.Time.UtcDateTime);
        Assert.Equal(61, p.Samples);
        Assert.Equal(10, p.Min);
        Assert.Equal(100, p.Max);
        Assert.Equal(700.0 / 61, p.Avg, 6);                    // a plain average of minute averages would be 55
    }

    [Fact]
    public async Task Windows_older_than_the_raw_retention_use_the_rollup_even_when_narrow()
    {
        var id = await f.CreateSeriesAsync(f.NewDevice(), "temperature", "C");
        var from = ApiFixture.Ago(TimeSpan.FromDays(100));
        var resp = await Get(id, $"from={ApiFixture.Url(from)}&to={ApiFixture.Url(from.AddDays(1))}&maxPoints=1000");
        var h = (await resp.Content.ReadFromJsonAsync<HistoryDto>())!;

        Assert.Equal("Rollup1m", h.Source);
        Assert.Equal(120_000, h.BucketMs);                     // 86.4 s rounded up to whole minutes
        Assert.Empty(h.Points);
    }

    [Fact]
    public async Task Defaults_are_the_last_hour_from_raw_data()
    {
        var id = await f.CreateSeriesAsync(f.NewDevice(), "temperature", "C");
        var h = (await (await Get(id, "")).Content.ReadFromJsonAsync<HistoryDto>())!;
        Assert.Equal("Raw", h.Source);
        Assert.Equal(3_600, h.BucketMs);
    }

    [Fact]
    public async Task Bad_requests_are_rejected()
    {
        var id = await f.CreateSeriesAsync(f.NewDevice(), "temperature", "C");
        var now = ApiFixture.Ago(TimeSpan.Zero);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await Get(id, $"from={ApiFixture.Url(now)}&to={ApiFixture.Url(now.AddHours(-1))}")).StatusCode);   // inverted
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Get(id, $"from={ApiFixture.Url(now)}&to={ApiFixture.Url(now)}")).StatusCode);                // empty
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(id, "maxPoints=5")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(id, "maxPoints=5001")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Get(id, $"from={ApiFixture.Url(now.AddDays(-801))}&to={ApiFixture.Url(now)}")).StatusCode);  // > 800 days
        Assert.Equal(HttpStatusCode.NotFound, (await Get(int.MaxValue, "")).StatusCode);
    }
}

[Collection("api")]
public class AlertsTests(ApiFixture f)
{
    [Fact]
    public async Task Open_filter_and_series_details()
    {
        var dev = f.NewDevice();
        var sid = await f.CreateSeriesAsync(dev, "temperature", "C");
        var now = ApiFixture.Ago(TimeSpan.Zero);
        var open = await f.InsertAlertAsync(sid, "too hot", now);
        var closed = await f.InsertAlertAsync(sid, "was hot", now, resolved: now);

        var onlyOpen = (await f.Http.GetFromJsonAsync<List<AlertDto>>("/api/alerts"))!;      // default: open only
        Assert.Contains(onlyOpen, a => a.Id == open);
        Assert.DoesNotContain(onlyOpen, a => a.Id == closed);

        var all = (await f.Http.GetFromJsonAsync<List<AlertDto>>("/api/alerts?openOnly=false"))!;
        Assert.Contains(all, a => a.Id == open);
        Assert.Contains(all, a => a.Id == closed);

        var a = all.Single(x => x.Id == open);
        Assert.Equal(("it", "line1", dev, "temperature"), (a.Site, a.Line, a.Device, a.Sensor));
        Assert.Equal("warning", a.Severity);
        Assert.Equal("too hot", a.Message);
        Assert.Null(a.AcknowledgedAt);
    }

    [Fact]
    public async Task Acknowledging_is_idempotent_and_keeps_the_first_acknowledgement()
    {
        var sid = await f.CreateSeriesAsync(f.NewDevice(), "temperature", "C");
        var id = await f.InsertAlertAsync(sid, "ack me", ApiFixture.Ago(TimeSpan.Zero));
        var url = $"/api/alerts/{id}/ack";

        var first = await f.Http.PostAsJsonAsync(url, new AcknowledgeRequest("alice"));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        var at1 = await f.ScalarAsync<DateTime>("SELECT acknowledged_at FROM alerts WHERE id = @i", ("i", id));

        var second = await f.Http.PostAsJsonAsync(url, new AcknowledgeRequest("bob"));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        Assert.Equal("alice", await f.ScalarAsync<string>("SELECT acknowledged_by FROM alerts WHERE id = @i", ("i", id)));
        Assert.Equal(at1, await f.ScalarAsync<DateTime>("SELECT acknowledged_at FROM alerts WHERE id = @i", ("i", id)));
    }

    [Fact]
    public async Task Acknowledging_without_a_body_works_and_unknown_ids_are_404()
    {
        var sid = await f.CreateSeriesAsync(f.NewDevice(), "temperature", "C");
        var id = await f.InsertAlertAsync(sid, "anonymous ack", ApiFixture.Ago(TimeSpan.Zero));

        Assert.Equal(HttpStatusCode.NoContent, (await f.Http.PostAsync($"/api/alerts/{id}/ack", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await f.Http.PostAsJsonAsync("/api/alerts/999999999/ack", new AcknowledgeRequest("x"))).StatusCode);
    }
}