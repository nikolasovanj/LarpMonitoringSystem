# IoT Monitor: web dashboard

React 18, TypeScript, Vite, TanStack Query, SignalR, ECharts.

## Run

```bash
cd web
npm install
npm run dev            # http://localhost:5173
```

The dev server proxies `/api` and `/hubs` to the backend (default `http://localhost:5080`, change it with
`VITE_PROXY_TARGET`, see `.env.example`). So the same origin is used and CORS never comes into play.

```bash
npm run typecheck
npm test               # LiveStore unit tests (no server needed)
npm run build
```

## Structure

```
src/
  api/        types.ts (mirrors IotMonitor.Contracts), client.ts (fetch), queries.ts (TanStack hooks)
  realtime/   LiveStore (SignalR + buffers), LiveProvider, hooks (useSyncExternalStore)
  lib/        format, ranges, echarts setup and theme, shared 1 s ticker
  components/ one component per file
  pages/      OverviewPage, DeviceDetailPage, AlertsPage, NotFoundPage
```

## How live data flows

SignalR events go into `LiveStore`, which keeps the last 1200 points per series and the latest reading,
device status and command reply. Components subscribe through `useSyncExternalStore`, so a reading for one
sensor re-renders only the components that show that sensor. REST (TanStack Query) provides the first
snapshot and history; the `Alert` event and reconnects invalidate the relevant queries.

## Docker

`Dockerfile` builds the app and serves it with nginx, which also proxies `/api` and `/hubs` to the `api`
service (see `nginx.conf`). Compose service:

```yaml
  web:
    build: ./web
    ports: ["5173:80"]
    depends_on: [api]
```
