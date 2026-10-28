namespace Monitor.Ingestion;

public sealed class MqttOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    /// Fixed ID + persistent session: the broker queues QoS 1 messages while we are down.
    /// Two instances with the same ID would kick each other off (the bug from the simulators).
    public string ClientId { get; set; } = "iot-ingestion";
    public bool CleanSession { get; set; } = false;
    public string TelemetryTopic { get; set; } = "factory/+/+/+/+/telemetry";
    public string StatusTopic { get; set; } = "factory/+/+/+/status";
}

public sealed class IngestOptions
{
    public int BatchSize { get; set; } = 1000;
    public int FlushIntervalMs { get; set; } = 500;
    public int QueueCapacity { get; set; } = 100_000;
}