using System.Net;
using System.Text.Json;
using Monitor.Contracts;
using Xunit;

namespace Monitor.Api.Tests;

[Collection("api")]
public class LiveHubTests(ApiFixture f)
{
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string Topic(string dev, string sensor) => $"factory/it/line1/{dev}/{sensor}/telemetry";

    private static string Reading(string device, string sensor, double value, long? ts = null, string unit = "C") =>
        JsonSerializer.Serialize(new { device, sensor, value, unit, ts = ts ?? NowMs() });

    private static async Task PublishUntilAsync(MqttTestClient pub, string topic, Func<string> payload,
                                                Func<bool> seen, string what, int seconds = 25)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (seen()) return;
            await pub.PublishAsync(topic, payload());
            await Task.Delay(250);
        }
        if (!seen()) throw new TimeoutException($"timed out waiting for: {what}");
    }

    [Fact]
    public async Task Telemetry_reaches_hub_clients_and_the_topic_decides_the_identity()
    {
        var dev = f.NewDevice();
        await using var pub = await MqttTestClient.ConnectAsync(f.MqttHost, f.MqttPort);
        await using var hub = await HubProbe.ConnectAsync(f.Factory);

        // The payload claims to be another device and sensor.
        await PublishUntilAsync(pub, Topic(dev, "temperature"),
            () => Reading("spoofed", "pressure", 71.25), () => hub.ReadingsFor(dev).Any(), "a reading on the hub");

        var r = hub.ReadingsFor(dev).First();
        Assert.Equal(("it", "line1", dev, "temperature"), (r.Site, r.Line, r.Device, r.Sensor));
        Assert.Equal(71.25, r.Value);
        Assert.Equal("C", r.Unit);
        Assert.InRange((DateTimeOffset.UtcNow - r.Time).TotalSeconds, -2, 30);
    }

    [Fact]
    public async Task Readings_are_batched_into_few_messages()
    {
        const int N = 40;
        var dev = f.NewDevice();
        await using var pub = await MqttTestClient.ConnectAsync(f.MqttHost, f.MqttPort);
        await using var hub = await HubProbe.ConnectAsync(f.Factory);

        await PublishUntilAsync(pub, Topic(dev, "warm"), () => Reading(dev, "warm", 0),
            () => hub.ReadingsFor(dev).Any(), "subscription to be active");

        var t0 = NowMs();
        for (var i = 0; i < N; i++)
            await pub.PublishAsync(Topic(dev, "burst"), Reading(dev, "burst", i, t0 + i));

        await f.EventuallyAsync(() => hub.ReadingsFor(dev).Count(r => r.Sensor == "burst") == N, $"all {N} readings");
        var sizes = hub.Batches.ToArray().Select(b => b.Count(r => r.Device == dev && r.Sensor == "burst")).Where(n => n > 0).ToList();
        Assert.True(sizes.Max() > 1, "readings should be grouped, not sent one per message");
        Assert.True(sizes.Count < N);
    }

    [Fact]
    public async Task Invalid_telemetry_is_never_broadcast()
    {
        var dev = f.NewDevice();
        await using var pub = await MqttTestClient.ConnectAsync(f.MqttHost, f.MqttPort);
        await using var hub = await HubProbe.ConnectAsync(f.Factory);

        // Make sure the gateway is subscribed first, otherwise the bad messages below could be lost
        // and the test would pass for the wrong reason.
        await PublishUntilAsync(pub, Topic(dev, "warm"), () => Reading(dev, "warm", 0),
            () => hub.ReadingsFor(dev).Any(), "subscription to be active");

        var t = Topic(dev, "temperature");
        await pub.PublishAsync(t, "not json");
        await pub.PublishAsync(t, """{"value":"abc"}""");
        await pub.PublishAsync(t, Reading(dev, "temperature", 1, NowMs() - 8 * 86_400_000L));   // 8 days old
        await pub.PublishAsync(t, Reading(dev, "temperature", 2, NowMs() + 2 * 3_600_000L));    // 2 h ahead
        await pub.PublishAsync($"factory/it/line1/{dev}/bad sensor/telemetry", Reading(dev, "x", 3));

        await PublishUntilAsync(pub, Topic(dev, "marker"), () => Reading(dev, "marker", 9),
            () => hub.ReadingsFor(dev).Any(r => r.Sensor == "marker"), "the marker reading");

        Assert.DoesNotContain(hub.ReadingsFor(dev), r => r.Sensor is "temperature" or "bad sensor");
    }

    [Fact]
    public async Task Status_changes_are_pushed()
    {
        var dev = f.NewDevice();
        await using var pub = await MqttTestClient.ConnectAsync(f.MqttHost, f.MqttPort);
        await using var hub = await HubProbe.ConnectAsync(f.Factory);
        var topic = $"factory/it/line1/{dev}/status";

        await PublishUntilAsync(pub, topic, () => "online",
            () => hub.StatusesFor(dev).Any(s => s.Status == "online"), "online event");
        await pub.PublishAsync(topic, "banana");                                   // rejected, must not surface
        await PublishUntilAsync(pub, topic, () => "offline",
            () => hub.StatusesFor(dev).Any(s => s.Status == "offline"), "offline event");

        Assert.All(hub.StatusesFor(dev), s => Assert.Contains(s.Status, new[] { "online", "offline" }));
        Assert.Equal(("it", "line1"), (hub.StatusesFor(dev).First().Site, hub.StatusesFor(dev).First().Line));
    }

    [Fact]
    public async Task Command_results_are_pushed_truncated_and_survive_garbage()
    {
        var dev = f.NewDevice();
        await using var pub = await MqttTestClient.ConnectAsync(f.MqttHost, f.MqttPort);
        await using var hub = await HubProbe.ConnectAsync(f.Factory);
        var topic = $"factory/it/line1/{dev}/cmd/result";

        await PublishUntilAsync(pub, topic, () => """{"ok":false,"error":"device has no sensor"}""",
            () => hub.ResultsFor(dev).Any(), "a rejection");
        var rejected = hub.ResultsFor(dev).First();
        Assert.False(rejected.Ok);
        Assert.Equal("device has no sensor", rejected.Error);
        Assert.Equal(dev, rejected.Device);

        // A device echoing huge input must not flood every dashboard.
        var huge = new string('x', 1000);
        await PublishUntilAsync(pub, topic, () => $$"""{"ok":false,"error":"{{huge}}"}""",
            () => hub.ResultsFor(dev).Any(r => r.Error?.StartsWith("xxx") == true), "a long error");
        Assert.Equal(300, hub.ResultsFor(dev).First(r => r.Error?.StartsWith("xxx") == true).Error!.Length);

        // Garbage, then a valid reply: the gateway must still be alive.
        await pub.PublishAsync(topic, "{broken");
        await pub.PublishAsync(topic, "[]");
        await PublishUntilAsync(pub, topic, () => """{"ok":true}""",
            () => hub.ResultsFor(dev).Any(r => r.Ok), "an ok result after garbage");
    }

    [Fact]
    public async Task Command_round_trip_from_http_through_the_device_back_to_the_hub()
    {
        var dev = f.NewDevice();
        await using var device = await MqttTestClient.ConnectAsync(f.MqttHost, f.MqttPort);   // plays the simulator
        await using var hub = await HubProbe.ConnectAsync(f.Factory);
        await device.SubscribeAsync($"factory/it/line1/{dev}/cmd");

        var resp = await f.PostFaultAsync(dev, """{"sensor":"pressure","fault":"ramp","duration":50}""");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        await f.EventuallyAsync(() => !device.Received.IsEmpty, "the command to reach the device");

        using var cmd = JsonDocument.Parse(device.Received.First().Payload);
        Assert.Equal("ramp", cmd.RootElement.GetProperty("fault").GetString());

        // The "device" answers the way the C simulator does.
        await PublishUntilAsync(device, $"factory/it/line1/{dev}/cmd/result", () => """{"ok":true}""",
            () => hub.ResultsFor(dev).Any(), "the result on the hub");
        Assert.True(hub.ResultsFor(dev).First().Ok);
    }
}