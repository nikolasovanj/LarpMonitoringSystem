namespace Monitor.Operator.Api;

/// <summary>What the view models need from the REST API. Lets tests substitute a fake.</summary>
public interface IMonitorApi : IDisposable
{
    Task<List<DeviceDto>> GetDevicesAsync(CancellationToken ct = default);
    Task<List<AlertDto>> GetAlertsAsync(bool openOnly, CancellationToken ct = default);
    Task<HistoryDto> GetHistoryAsync(long seriesId, DateTimeOffset from, DateTimeOffset to, int maxPoints,
        CancellationToken ct = default);
    Task AckAlertAsync(long id, string by, CancellationToken ct = default);
    Task InjectFaultAsync(string site, string line, string device, InjectFaultRequest body,
        CancellationToken ct = default);
}
