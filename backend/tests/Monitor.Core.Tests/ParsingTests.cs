using System.Text;
using Monitor.Core;
using Xunit;

public class TopicParserTests
{
    [Fact]
    public void Parses_telemetry_topic()
    {
        Assert.True(TopicParser.TryParse("factory/site1/line1/pump01/temperature/telemetry", out var p));
        Assert.Equal(TopicKind.Telemetry, p.Kind);
        Assert.Equal(new SeriesKey("site1", "line1", "pump01", "temperature"), p.Series);
    }

    [Fact]
    public void Parses_status_topic()
    {
        Assert.True(TopicParser.TryParse("factory/site1/line1/pump01/status", out var p));
        Assert.Equal(TopicKind.Status, p.Kind);
        Assert.Equal(new DeviceKey("site1", "line1", "pump01"), p.Device);
    }

    [Theory]
    [InlineData("factory/s/l/d/cmd")]
    [InlineData("factory/s/l/d/cmd/result")]
    [InlineData("factory/s/l/d/temperature/status")]
    [InlineData("factory/s/l/d/temperature/telemetry/extra")]
    [InlineData("other/s/l/d/status")]
    [InlineData("factory//l/d/status")]
    [InlineData("factory/s/l/d/status/")]
    [InlineData("factory/s/l/d e/status")]
    [InlineData("factory/s/l/d/temp erature/telemetry")]
    [InlineData("")]
    public void Rejects_everything_else(string topic) =>
        Assert.False(TopicParser.TryParse(topic, out _));
}

public class PayloadTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000);

    private static bool Parse(string json, out TelemetryReading r) =>
        TelemetryPayload.TryParse(Encoding.UTF8.GetBytes(json), Now, out r, out _);

    [Fact]
    public void Seconds_timestamp()
    {
        Assert.True(Parse("""{"value":70.5,"unit":"C","ts":1760000000}""", out var r));
        Assert.Equal(70.5, r.Value);
        Assert.Equal("C", r.Unit);
        Assert.Equal(Now, r.Time);
    }

    [Fact]
    public void Milliseconds_timestamp()
    {
        Assert.True(Parse("""{"value":1,"ts":1760000000123}""", out var r));
        Assert.Equal(Now.AddMilliseconds(123), r.Time);
    }

    [Fact]
    public void Missing_ts_uses_now()
    {
        Assert.True(Parse("""{"value":1}""", out var r));
        Assert.Equal(Now, r.Time);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"value":"1"}""")]
    [InlineData("""{"value":null}""")]
    [InlineData("""{"value":1,"ts":0}""")]
    [InlineData("""{"value":1,"ts":-5}""")]
    [InlineData("""{"value":1,"ts":"1760000000"}""")]
    [InlineData("""{"value":1,"ts":1760007200}""")]            // 2 h in the future
    [InlineData("""{"value":1,"ts":1759308800}""")]            // 8 days old
    [InlineData("""{"value":1,"ts":1760000000000000}""")]      // overflows DateTimeOffset
    public void Rejects_bad_payloads(string json) => Assert.False(Parse(json, out _));

    [Fact]
    public void Status_payload()
    {
        Assert.True(StatusPayload.TryParse("online"u8, out var s1) && s1 == "online");
        Assert.True(StatusPayload.TryParse("offline"u8, out var s2) && s2 == "offline");
        Assert.False(StatusPayload.TryParse("Online"u8, out _));
        Assert.False(StatusPayload.TryParse(""u8, out _));      // retained-message clear
    }
}