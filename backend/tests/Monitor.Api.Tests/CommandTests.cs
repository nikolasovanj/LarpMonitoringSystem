using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Monitor.Api.Tests;

[Collection("api")]
public class CommandTests(ApiFixture f)
{
    public static readonly string[] BadBodies =
    [
        """{"sensor":"a/b","fault":"spike"}""",              // '/', '+', '#' would corrupt the MQTT topic or filter
        """{"sensor":"a+b","fault":"spike"}""",
        """{"sensor":"a#","fault":"spike"}""",
        """{"sensor":"","fault":"spike"}""",
        """{"fault":"spike"}""",
        """{"sensor":"pressure"}""",
        """{"sensor":"pressure","fault":"explode"}""",
        """{"sensor":"pressure","fault":"SPIKE"}""",         // case-sensitive, like the device
        """{"sensor":"pressure","fault":"spike","duration":0}""",
        """{"sensor":"pressure","fault":"spike","duration":-1}""",
        """{"sensor":"pressure","fault":"spike","duration":1000001}""",
        """{"sensor":"pressure","fault":"spike","duration":1.5}""",
        "not json",
    ];
    public static IEnumerable<object[]> BadBodyCases => BadBodies.Select(b => new object[] { b });

    [Theory, MemberData(nameof(BadBodyCases))]
    public async Task Malformed_commands_are_rejected_with_400(string body) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await f.PostFaultAsync(f.NewDevice(), body)).StatusCode);

    [Theory]
    [InlineData("bad%2Bid/line1/dev")]      // '+' in the site
    [InlineData("it/bad%23id/dev")]         // '#' in the line
    [InlineData("it/line1/bad%20id")]       // a space in the device
    public async Task Invalid_path_ids_are_rejected_with_400(string siteLineDevice)
    {
        var body = new StringContent("""{"sensor":"pressure","fault":"spike"}""", Encoding.UTF8, "application/json");
        var resp = await f.Http.PostAsync($"/api/devices/{siteLineDevice}/faults", body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Rejected_commands_publish_nothing()
    {
        var dev = f.NewDevice();
        await using var tap = await MqttTestClient.ConnectAsync(f.MqttHost, f.MqttPort);
        await tap.SubscribeAsync($"factory/it/line1/{dev}/cmd");

        foreach (var body in BadBodies) await f.PostFaultAsync(dev, body);

        // One valid command as a marker: if any bad one had been published, it would arrive first.
        Assert.Equal(HttpStatusCode.Accepted,
            (await f.PostFaultAsync(dev, """{"sensor":"pressure","fault":"spike"}""")).StatusCode);
        await f.EventuallyAsync(() => !tap.Received.IsEmpty, "the marker command");
        await Task.Delay(500);
        Assert.Single(tap.Received);
    }

    [Fact]
    public async Task Valid_commands_reach_the_device_topic_as_unretained_json()
    {
        var dev = f.NewDevice();
        var topic = $"factory/it/line1/{dev}/cmd";
        await using var tap = await MqttTestClient.ConnectAsync(f.MqttHost, f.MqttPort);
        await tap.SubscribeAsync(topic);

        Assert.Equal(HttpStatusCode.Accepted,
            (await f.PostFaultAsync(dev, """{"sensor":"pressure","fault":"ramp","duration":100}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,
            (await f.PostFaultAsync(dev, """{"sensor":"temperature","fault":"stuck"}""")).StatusCode);

        await f.EventuallyAsync(() => tap.Received.Count == 2, "both commands");
        var msgs = tap.Received.ToArray();

        using var first = JsonDocument.Parse(msgs[0].Payload);
        Assert.Equal("pressure", first.RootElement.GetProperty("sensor").GetString());
        Assert.Equal("ramp", first.RootElement.GetProperty("fault").GetString());
        Assert.Equal(100, first.RootElement.GetProperty("duration").GetInt32());

        using var second = JsonDocument.Parse(msgs[1].Payload);
        Assert.Equal("stuck", second.RootElement.GetProperty("fault").GetString());
        Assert.False(second.RootElement.TryGetProperty("duration", out _));   // omitted = device default

        // Not retained: a client that subscribes afterwards must not receive the commands again.
        await using var late = await MqttTestClient.ConnectAsync(f.MqttHost, f.MqttPort);
        await late.SubscribeAsync(topic);
        await Task.Delay(1000);
        Assert.Empty(late.Received);
    }
}

[Collection("api")]
public class AvailabilityTests(ApiFixture f)
{
    [Fact]
    public async Task Healthy_when_broker_and_database_are_up()
    {
        var resp = await f.Http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("db").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("mqtt").GetBoolean());
    }

    [Fact]
    public async Task Reports_503_when_the_broker_is_unreachable()
    {
        // A second API instance pointed at a port where nothing listens.
        await using var factory = new ApiFactory("127.0.0.1", ApiFixture.FreePort(), f.PgConnectionString);
        using var http = factory.CreateClient();

        var fault = await f.PostFaultAsync(f.NewDevice(), """{"sensor":"pressure","fault":"spike"}""", http);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, fault.StatusCode);

        var health = await http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        using var doc = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("db").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("mqtt").GetBoolean());
    }
}