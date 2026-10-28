using Xunit;

namespace Monitor.Ingestion.Tests;

[Collection("stack")]
public class IngestionTests(StackFixture s)
{
    [Fact]
    public async Task Telemetry_is_stored_with_series_unit_and_ms_precision()
    {
        var dev = s.NewDevice();
        var ts = StackFixture.NowMs() - 5_000 + 123;           // not a whole second on purpose
        await s.PublishAsync(s.Topic(dev, "temperature"), StackFixture.Sample(dev, "temperature", 71.25, ts, "C"));

        await s.EventuallyAsync(async () => await s.CountAsync(dev) == 1, "the row to be stored");

        await using var cmd = s.Db.CreateCommand("""
            SELECT t.time, t.value, sr.unit, sr.site, sr.line, sr.sensor
            FROM telemetry t JOIN series sr ON sr.id = t.series_id WHERE sr.device = @d
            """);
        cmd.Parameters.AddWithValue("d", dev);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime, r.GetFieldValue<DateTime>(0));
        Assert.Equal(71.25, r.GetDouble(1));
        Assert.Equal("C", r.GetString(2));
        Assert.Equal("it", r.GetString(3));
        Assert.Equal("line1", r.GetString(4));
        Assert.Equal("temperature", r.GetString(5));
    }

    [Fact]
    public async Task Topic_decides_the_series_not_the_payload()
    {
        var dev = s.NewDevice();
        var victim = s.NewDevice();
        // Payload claims to be another device and sensor.
        await s.PublishAsync(s.Topic(dev, "pressure"),
            StackFixture.Sample(victim, "temperature", 5, StackFixture.NowMs()));

        await s.EventuallyAsync(async () => await s.CountAsync(dev) == 1, "row for the topic's device");
        Assert.Equal(0, await s.ScalarAsync<long>("SELECT count(*) FROM series WHERE device = @d", ("d", victim)));
        Assert.Equal("pressure", await s.ScalarAsync<string>("SELECT sensor FROM series WHERE device = @d", ("d", dev)));
    }

    [Fact]
    public async Task Bad_input_never_reaches_the_database()
    {
        var dev = s.NewDevice();
        var now = StackFixture.NowMs();
        var t = s.Topic(dev, "temperature");

        await s.PublishAsync(t, "not json");
        await s.PublishAsync(t, """{"value":"abc","ts":%d}""".Replace("%d", now.ToString()));
        await s.PublishAsync(t, """{"unit":"C"}""");                                              // no value
        await s.PublishAsync(t, StackFixture.Sample(dev, "temperature", 1, now - 8L * 86_400_000)); // 8 days old
        await s.PublishAsync(t, StackFixture.Sample(dev, "temperature", 2, now + 2L * 3_600_000));  // 2 h ahead
        await s.PublishAsync($"factory/it/line1/{dev}/cmd", StackFixture.Sample(dev, "temperature", 3, now));
        await s.PublishAsync($"factory/it/line1/{dev}/cmd/result", """{"ok":true}""");
        await s.PublishAsync($"factory/it/line1/{dev}/bad sensor/telemetry", StackFixture.Sample(dev, "x", 4, now));

        // One valid marker after them: when it lands, everything before it has been processed.
        await s.PublishAsync(t, StackFixture.Sample(dev, "temperature", 99, now));
        await s.EventuallyAsync(async () => await s.CountAsync(dev) >= 1, "the marker sample");

        Assert.Equal(1, await s.CountAsync(dev));
        Assert.Equal(99.0, await s.ScalarAsync<double>(
            "SELECT t.value FROM telemetry t JOIN series s ON s.id = t.series_id WHERE s.device = @d", ("d", dev)));
        Assert.Equal(1, await s.ScalarAsync<long>("SELECT count(*) FROM series WHERE device = @d", ("d", dev)));
    }

    [Fact]
    public async Task Duplicate_messages_store_one_row()
    {
        var dev = s.NewDevice();
        var now = StackFixture.NowMs();
        var t = s.Topic(dev, "flow");
        var same = StackFixture.Sample(dev, "flow", 40.5, now - 1000, "L/min");

        await s.PublishAsync(t, same);
        await s.PublishAsync(t, same);                                      // what a QoS 1 redelivery looks like
        await s.PublishAsync(t, same);
        await s.PublishAsync(t, StackFixture.Sample(dev, "flow", 41, now)); // marker, different ts

        await s.EventuallyAsync(async () => await s.CountAsync(dev) >= 2, "both distinct samples");
        await Task.Delay(500);                                              // allow any late duplicate to (wrongly) land
        Assert.Equal(2, await s.CountAsync(dev));
    }

    [Fact]
    public async Task Status_changes_are_recorded_once()
    {
        var dev = s.NewDevice();
        var t = s.StatusTopic(dev);
        try
        {
            await s.PublishAsync(t, "online", retain: true);
            await s.PublishAsync(t, "online", retain: true);    // repeat: no new event
            await s.PublishAsync(t, "offline", retain: true);
            await s.PublishAsync(t, "offline", retain: true);   // repeat: no new event
            await s.PublishAsync(t, "banana");                  // rejected
            await s.PublishAsync(t, "online", retain: true);

            await s.EventuallyAsync(async () =>
                await s.ScalarAsync<long>("SELECT count(*) FROM device_status_events WHERE device = @d", ("d", dev)) >= 3,
                "three status events");
            await Task.Delay(500);

            var events = new List<string>();
            await using (var cmd = s.Db.CreateCommand(
                "SELECT status FROM device_status_events WHERE device = @d ORDER BY time"))
            {
                cmd.Parameters.AddWithValue("d", dev);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) events.Add(r.GetString(0));
            }
            Assert.Equal(new[] { "online", "offline", "online" }, events);
            Assert.Equal("online", await s.ScalarAsync<string>(
                "SELECT status FROM device_status WHERE device = @d", ("d", dev)));
        }
        finally
        {
            await s.PublishAsync(t, "", retain: true);          // clear the retained message on the broker
        }
    }

    [Fact]
    public async Task A_burst_of_3000_samples_is_stored_completely()
    {
        const int N = 3000;
        var dev = s.NewDevice();
        var t = s.Topic(dev, "vibration");
        var baseTs = StackFixture.NowMs() - 60_000;

        for (var i = 0; i < N; i += 50)
            await Task.WhenAll(Enumerable.Range(i, Math.Min(50, N - i)).Select(k =>
                s.PublishAsync(t, StackFixture.Sample(dev, "vibration", k, baseTs + k, "mm/s"))));

        await s.EventuallyAsync(async () => await s.CountAsync(dev) == N, $"all {N} samples", 30);
        Assert.Equal(N, await s.CountAsync(dev));
        Assert.Equal(N, await s.ScalarAsync<long>(
            "SELECT count(DISTINCT t.time) FROM telemetry t JOIN series s ON s.id = t.series_id WHERE s.device = @d",
            ("d", dev)));
    }

    [Fact]
    public async Task Stale_series_cache_is_recovered_after_the_database_is_wiped()
    {
        var dev = s.NewDevice();
        var t = s.Topic(dev, "current");
        await s.PublishAsync(t, StackFixture.Sample(dev, "current", 12, StackFixture.NowMs() - 2000, "A"));
        await s.EventuallyAsync(async () => await s.CountAsync(dev) == 1, "first sample");
        var oldId = await s.ScalarAsync<int>("SELECT id FROM series WHERE device = @d", ("d", dev));

        // The worker still has the old series id cached. Pull the rug out from under it.
        await s.ExecAsync("TRUNCATE telemetry; DELETE FROM series;");

        await s.PublishAsync(t, StackFixture.Sample(dev, "current", 13, StackFixture.NowMs() - 1000, "A"));
        // FK violation -> cache cleared -> 1 s backoff -> series re-registered -> row written.
        await s.EventuallyAsync(async () => await s.CountAsync(dev) == 1, "recovery after FK violation", 25);

        var newId = await s.ScalarAsync<int>("SELECT id FROM series WHERE device = @d", ("d", dev));
        Assert.True(newId > oldId, "series should have been re-created with a new id");
    }
}