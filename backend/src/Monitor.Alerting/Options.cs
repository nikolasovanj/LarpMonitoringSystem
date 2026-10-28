namespace Monitor.Alerting;

public sealed class AlertingOptions
{
    public int IntervalSeconds { get; set; } = 1;
    /// Samples this recent are examined each tick. Keep it at least as long as the interval.
    public int WindowSeconds { get; set; } = 2;
    public int ResolveHoldSeconds { get; set; } = 5;
    public int BaselineMinutes { get; set; } = 10;
    /// Series with no data in this long are treated as retired by "stale" rules.
    public int LookbackHours { get; set; } = 24;
}

public sealed class MqttSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
}