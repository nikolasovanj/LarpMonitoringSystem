using System.Text.RegularExpressions;
using Monitor.Contracts;
using Monitor.Core;
using Npgsql;

namespace Monitor.Api;

public static partial class Endpoints
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")] private static partial Regex IdPattern();
    private static readonly HashSet<string> Faults = ["spike", "stuck", "dropout", "ramp", "noisy"];

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/devices", (Queries q, CancellationToken ct) => q.GetDevicesAsync(ct))
           .Produces<List<DeviceDto>>().WithName("GetDevices");

        api.MapGet("/series", (Queries q, CancellationToken ct) => q.ListSeriesAsync(ct))
           .Produces<List<SeriesDto>>().WithName("GetSeries");

        api.MapGet("/series/{id:int}/history", async (int id, DateTimeOffset? from, DateTimeOffset? to,
            int? maxPoints, Queries q, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var end = to ?? now;
            var start = from ?? end.AddHours(-1);
            var points = maxPoints ?? 1000;

            if (start >= end) return Results.Problem("'from' must be before 'to'", statusCode: 400);
            if (end - start > TimeSpan.FromDays(800)) return Results.Problem("range too large (max 800 days)", statusCode: 400);
            if (points is < 10 or > 5000) return Results.Problem("maxPoints must be 10..5000", statusCode: 400);
            if (await q.GetSeriesAsync(id, ct) is null) return Results.NotFound();

            var plan = HistoryPlanner.Plan(start, end, now, points);
            var data = await q.GetHistoryAsync(id, start, end, plan, ct);
            return Results.Ok(new HistoryDto(id, plan.Source.ToString(), plan.BucketMs, data));
        }).Produces<HistoryDto>().WithName("GetHistory");

        api.MapGet("/alerts", (bool? openOnly, Queries q, CancellationToken ct) =>
                q.GetAlertsAsync(openOnly ?? true, ct))
           .Produces<List<AlertDto>>().WithName("GetAlerts");

        api.MapPost("/alerts/{id:long}/ack", async (long id, AcknowledgeRequest? body, Queries q, CancellationToken ct) =>
                await q.AcknowledgeAsync(id, body?.By, ct) ? Results.NoContent() : Results.NotFound())
           .WithName("AcknowledgeAlert");

        api.MapPost("/devices/{site}/{line}/{device}/faults", async (string site, string line, string device,
            InjectFaultRequest req, MqttGateway mqtt, CancellationToken ct) =>
        {
            // These become MQTT topic segments: no '/', '+', '#' may get through.
            if (!IdPattern().IsMatch(site) || !IdPattern().IsMatch(line) || !IdPattern().IsMatch(device))
                return Results.Problem("invalid site, line or device", statusCode: 400);
            if (req.Sensor is null  || !IdPattern().IsMatch(req.Sensor))
                return Results.Problem("invalid sensor", statusCode: 400);
            if (req.Fault is null || !Faults.Contains(req.Fault))
                return Results.Problem($"fault must be one of: {string.Join(", ", Faults)}", statusCode: 400);
            if (req.Duration is < 1 or > 1_000_000)
                return Results.Problem("duration must be 1..1000000 samples", statusCode: 400);

            try { await mqtt.PublishCommandAsync(site, line, device, req.Sensor, req.Fault, req.Duration, ct); }
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 503); }

            // The device's answer arrives later as a "CommandResult" event on the hub.
            return Results.Accepted();
        }).WithName("InjectFault");

        app.MapGet("/healthz", async (NpgsqlDataSource db, MqttGateway mqtt) =>
        {
            var dbOk = true;
            try { await using var c = db.CreateCommand("SELECT 1"); await c.ExecuteScalarAsync(); }
            catch { dbOk = false; }
            return Results.Json(new { db = dbOk, mqtt = mqtt.IsConnected },
                                statusCode: dbOk && mqtt.IsConnected ? 200 : 503);
        });
    }
}