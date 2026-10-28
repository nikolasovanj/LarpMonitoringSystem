# IoT Monitor: web dashboard

React 18, TypeScript, Vite, TanStack Query, SignalR, ECharts.

## Run

```bash
cd web
npm install
npm run dev            # http://localhost:5173
```

The dev server proxies `/api` and `/hubs` to the backend (default `http://127.0.0.1:5080`, change it with
`VITE_PROXY_TARGET`, see `.env.example`). So the same origin is used and CORS never comes into play.

```bash
npm run typecheck
npm test               # LiveStore unit tests (no server needed)
npm run build
```

## Browser tests (Playwright)

Two suites:

```bash
npm run e2e:install    # once: downloads Chromium and its OS dependencies
npm run e2e            # hermetic: mock backend, no Docker needed (33 tests, about a minute)
npm run e2e:stack      # smoke tests against the real compose stack (see below)
```

**Hermetic suite** (`e2e/specs`): `e2e/mock/server.ts` imitates the API's REST endpoints and speaks enough of
the SignalR JSON protocol (negotiate, handshake, invocations) for the real client to connect. Tests steer it
through `/__mock/*` endpoints (emit a hub event, drop connections, change data, read the request log), see
`e2e/support/mock.ts`. Playwright starts the mock and the Vite dev server itself. It is the suite CI runs.

**Stack suite** (`e2e/stack`): needs the real thing running.

```bash
docker compose up -d --build          # repo root, at least sim-pump01
npm run dev                           # in web/, proxies to the API (VITE_PROXY_TARGET if it is not :5080)
npm run e2e:stack
```

`STACK_URL` (default `http://localhost:5173`), `STACK_DEVICE` (default `pump01`), `STACK_SITE` and
`STACK_LINE` change the target. If Playwright wants a different Chromium than the one installed, set
`CHROMIUM_PATH`. Vite prints `ws proxy socket error: ECONNRESET` while the hermetic suite runs; it is the
browser closing a WebSocket between tests and is harmless.

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
