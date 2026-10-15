namespace Monitor.Contracts;

public interface IMonitorClient
{
    Task Telemetry(IReadOnlyList<LiveReading> batch);
    Task DeviceStatus(DeviceStatusChanged change);
    Task CommandResult(CommandResultDto result);
    Task Alert(AlertEvent alert);
}