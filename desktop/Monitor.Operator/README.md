# Monitor: operator console (Avalonia)

Desktop client for the same API the web dashboard uses: device list with live status, live charts per
sensor, open alerts with acknowledge, and fault injection with the device's own reply.

.NET 8, Avalonia 11, CommunityToolkit.Mvvm, SignalR client. No other UI libraries: the chart is a small
custom control (`Controls/LineChart.cs`).

## Run

```bash
cd desktop/Monitor.Operator
dotnet run                              # API at http://localhost:8080 (the compose api service)
dotnet run -- http://localhost:5080     # or pass the base URL
MONITOR_API=http://host:8080 dotnet run # or set it in the environment
```

Start the stack first (`docker compose up -d`). The console keeps retrying if the API is not up yet and
reloads its snapshot after every reconnect.

## Tests

```bash
dotnet test desktop/IotMonitor.Operator.Tests
```

xUnit tests for the view models. The REST client and the hub connection sit behind `IMonitorApi` and
`ILiveClient`, so the tests use fakes, run the "UI thread" inline and move the clock by hand. No window is
created and no display is needed, so they run in CI.

## Run in Docker (Linux)

The console is a desktop app, so the container draws on your X server:

```bash
xhost +local:docker                                   # allow containers to use your display
docker compose --profile desktop up --build operator  # repo root
```

The `desktop` profile keeps it out of a plain `docker compose up`. Inside the compose network it talks to
`http://api:8080`. This works with X11 and with XWayland; on Windows and macOS run it natively instead.

## Structure

```
Api/          Models (wire DTOs), MonitorApi (REST), LiveClient (SignalR /hubs/live)
ViewModels/   MainViewModel, DeviceViewModel, SensorViewModel, AlertViewModel
Views/        MainWindow
Controls/     LineChart
Assets/       Styles.axaml
```

## How it works

- `MainViewModel` loads `/api/devices` and `/api/alerts?openOnly=true`, then connects to the hub.
- Hub events (`Telemetry`, `DeviceStatus`, `CommandResult`, `Alert`) are marshalled to the UI thread and
  update the view models. A new device or sensor triggers a snapshot reload, at most every 5 s.
- Selecting a device fetches 15 minutes of history (`/api/series/{id}/history`) and puts it in front of the
  live points. Each sensor keeps its last 600 points.
- Fault injection posts to `/api/devices/{site}/{line}/{device}/faults`. A 202 only means the command was
  published; the result of "accepted" or "rejected: reason" arrives as a `CommandResult` event.
- Acknowledging an alert posts `{ "by": "<OS user name>" }`.

## Shared contracts

`Api/Models.cs` mirrors the API's JSON by hand. To use the backend's own types instead, add a project
reference to `Monitor.Contracts` and delete that file (type names and namespaces may need a `using`).
