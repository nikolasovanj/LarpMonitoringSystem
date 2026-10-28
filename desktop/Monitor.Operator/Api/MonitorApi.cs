using System.Net.Http.Json;
using System.Text.Json;

namespace Monitor.Operator.Api;

public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>REST client for the monitor API.</summary>
public sealed class MonitorApi : IMonitorApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Leave optional fields out (a null duration means "the device's default"), like the web client does.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly HttpClient _http;

    public MonitorApi(string baseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
    }

    public Task<List<DeviceDto>> GetDevicesAsync(CancellationToken ct = default) =>
        GetAsync<List<DeviceDto>>("api/devices", ct);

    public Task<List<AlertDto>> GetAlertsAsync(bool openOnly, CancellationToken ct = default) =>
        GetAsync<List<AlertDto>>($"api/alerts?openOnly={(openOnly ? "true" : "false")}", ct);

    public Task<HistoryDto> GetHistoryAsync(long seriesId, DateTimeOffset from, DateTimeOffset to, int maxPoints,
        CancellationToken ct = default)
    {
        var q = $"from={Uri.EscapeDataString(from.UtcDateTime.ToString("o"))}" +
                $"&to={Uri.EscapeDataString(to.UtcDateTime.ToString("o"))}&maxPoints={maxPoints}";
        return GetAsync<HistoryDto>($"api/series/{seriesId}/history?{q}", ct);
    }

    public Task AckAlertAsync(long id, string by, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/alerts/{id}/ack", new { by }, ct);

    /// <summary>Fire-and-forget: 202 means the command was published. The device answers on the hub.</summary>
    public Task InjectFaultAsync(string site, string line, string device, InjectFaultRequest body,
        CancellationToken ct = default)
    {
        var path = string.Join('/', new[] { site, line, device }.Select(Uri.EscapeDataString));
        return SendAsync(HttpMethod.Post, $"api/devices/{path}/faults", body, ct);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var res = await SendCoreAsync(new HttpRequestMessage(HttpMethod.Get, path), ct);
        var value = await res.Content.ReadFromJsonAsync<T>(Json, ct);
        return value ?? throw new ApiException((int)res.StatusCode, "The API returned an empty body.");
    }

    private async Task SendAsync(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: Json) };
        using var _ = await SendCoreAsync(req, ct);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ApiException(0, "Cannot reach the API. Is the backend running?");
        }

        if (res.IsSuccessStatusCode) return res;

        var message = $"{(int)res.StatusCode} {res.ReasonPhrase}";
        try
        {
            // ASP.NET problem details: { title, detail, status }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("detail", out var d) && d.GetString() is { Length: > 0 } detail)
                message = detail;
            else if (doc.RootElement.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } title)
                message = title;
        }
        catch
        {
            /* body was not JSON */
        }

        var status = (int)res.StatusCode;
        res.Dispose();
        throw new ApiException(status, message);
    }

    public void Dispose() => _http.Dispose();
}
