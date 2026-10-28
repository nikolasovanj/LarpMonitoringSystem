/**
 * A stand-in for the IoT Monitor API, for hermetic browser tests.
 *
 * It implements the REST endpoints the dashboard uses plus just enough of the SignalR JSON
 * protocol (negotiate, handshake, invocations, pings) over a WebSocket. Tests steer it through
 * /__mock/* endpoints: emit hub events, drop connections, change the data, read the request log.
 *
 *   tsx e2e/mock/server.ts            (PORT, default 5081)
 */
import http, { type IncomingMessage, type ServerResponse } from 'node:http';
import { WebSocketServer, type WebSocket } from 'ws';

const RS = '\x1e'; // SignalR record separator

interface Sensor { seriesId: number; sensor: string; unit: string; latestValue: number | null; latestTime: string | null }
interface Device { site: string; line: string; device: string; status: string; statusSince: string | null; lastSeen: string | null; sensors: Sensor[] }
interface Alert { id: number; ruleId: number | null; seriesId: number | null; site: string | null; line: string | null; device: string | null; sensor: string | null; severity: string; message: string; value: number | null; openedAt: string; resolvedAt: string | null; acknowledgedAt: string | null; acknowledgedBy: string | null }

const FAULTS = ['spike', 'stuck', 'dropout', 'ramp', 'noisy'];
const sockets = new Set<WebSocket>();
let devices: Device[] = [];
let alerts: Alert[] = [];
let requests: { method: string; url: string; body?: unknown }[] = [];
let negotiates = 0;
/** When set, the next fault command is answered with this instead of the default ok reply. */
let nextReply: { ok: boolean; error?: string } | null = null;
let replyDelayMs = 150;

const iso = (msAgo = 0) => new Date(Date.now() - msAgo).toISOString();

function seed() {
  devices = [
    {
      site: 'site1', line: 'line1', device: 'pump01', status: 'online', statusSince: iso(600_000), lastSeen: iso(1000),
      sensors: [
        { seriesId: 1, sensor: 'temperature', unit: 'C', latestValue: 70.12, latestTime: iso(1000) },
        { seriesId: 2, sensor: 'pressure', unit: 'bar', latestValue: 6.01, latestTime: iso(1000) },
      ],
    },
    {
      site: 'site1', line: 'line2', device: 'motor02', status: 'offline', statusSince: iso(120_000), lastSeen: iso(120_000),
      sensors: [{ seriesId: 3, sensor: 'vibration', unit: 'mm/s', latestValue: 2.5, latestTime: iso(120_000) }],
    },
  ];
  alerts = [];
  requests = [];
  negotiates = 0;
  nextReply = null;
  replyDelayMs = 150;
}
seed();

function broadcast(target: string, ...args: unknown[]) {
  const frame = JSON.stringify({ type: 1, target, arguments: args }) + RS;
  for (const ws of sockets) if (ws.readyState === ws.OPEN) ws.send(frame);
}

function readBody(req: IncomingMessage): Promise<string> {
  return new Promise((resolve) => {
    let data = '';
    req.on('data', (c) => (data += c));
    req.on('end', () => resolve(data));
  });
}

function json(res: ServerResponse, status: number, body?: unknown) {
  res.writeHead(status, { 'Content-Type': 'application/json' });
  res.end(body === undefined ? '' : JSON.stringify(body));
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url ?? '/', 'http://localhost');
  const path = url.pathname;
  const raw = req.method === 'POST' ? await readBody(req) : '';
  let body: any;
  try { body = raw ? JSON.parse(raw) : undefined; } catch { body = undefined; }

  if (!path.startsWith('/__mock') && !path.startsWith('/hubs')) {
    requests.push({ method: req.method ?? 'GET', url: req.url ?? '', body });
  }

  // ---- SignalR negotiate ----
  if (path === '/hubs/live/negotiate') {
    negotiates++;
    return json(res, 200, {
      negotiateVersion: 1,
      connectionId: `c${negotiates}`,
      connectionToken: `t${negotiates}`,
      availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
    });
  }

  // ---- REST, shaped like the real API ----
  if (path === '/api/devices') return json(res, 200, devices);

  if (path === '/api/alerts' && req.method === 'GET') {
    const openOnly = url.searchParams.get('openOnly') !== 'false';
    return json(res, 200, alerts.filter((a) => !openOnly || a.resolvedAt === null));
  }

  let m = path.match(/^\/api\/alerts\/(\d+)\/ack$/);
  if (m && req.method === 'POST') {
    const a = alerts.find((x) => x.id === Number(m![1]));
    if (!a) return json(res, 404);
    a.acknowledgedAt ??= iso();
    a.acknowledgedBy ??= body?.by ?? null;
    return json(res, 204);
  }

  m = path.match(/^\/api\/series\/(\d+)\/history$/);
  if (m) {
    const from = Date.parse(url.searchParams.get('from') ?? '') || Date.now() - 3_600_000;
    const to = Date.parse(url.searchParams.get('to') ?? '') || Date.now();
    const n = 60;
    const step = (to - from) / n;
    const points = Array.from({ length: n }, (_, i) => {
      const v = 70 + Math.sin(i / 6) * 2;
      return { time: new Date(from + i * step).toISOString(), avg: v, min: v - 0.5, max: v + 0.5, samples: 10 };
    });
    return json(res, 200, { seriesId: Number(m[1]), source: 'Raw', bucketMs: Math.round(step), points });
  }

  m = path.match(/^\/api\/devices\/([^/]+)\/([^/]+)\/([^/]+)\/faults$/);
  if (m && req.method === 'POST') {
    const [site, line, device] = m.slice(1, 4).map(decodeURIComponent);
    const ok = (s: unknown) => typeof s === 'string' && /^[A-Za-z0-9_-]{1,64}$/.test(s);
    if (!ok(body?.sensor) || !FAULTS.includes(body?.fault) || (body?.duration !== undefined && !(body.duration >= 1)))
      return json(res, 400, { title: 'Bad Request', detail: 'invalid command' });

    // Like the real API: 202 immediately, the device's answer arrives later on the hub.
    json(res, 202);
    const dev = devices.find((d) => d.site === site && d.line === line && d.device === device);
    const reply = nextReply ??
      (dev?.sensors.some((s) => s.sensor === body.sensor)
        ? { ok: true }
        : { ok: false, error: `device has no sensor "${body.sensor}"` });
    nextReply = null;
    setTimeout(() => broadcast('CommandResult', { site, line, device, ok: reply.ok, error: reply.error ?? null, time: iso() }), replyDelayMs);
    return;
  }

  if (path === '/healthz') return json(res, 200, { db: true, mqtt: true });

  // ---- test control ----
  if (path === '/__mock/reset') { seed(); return json(res, 200, { ok: true }); }
  if (path === '/__mock/state') return json(res, 200, { devices, alerts, requests, sockets: sockets.size, negotiates });
  if (path === '/__mock/emit') { broadcast(body.target, ...(body.args ?? [])); return json(res, 200, { ok: true }); }
  if (path === '/__mock/devices') { devices = body; return json(res, 200, { ok: true }); }
  if (path === '/__mock/alerts') { alerts = body; return json(res, 200, { ok: true }); }
  if (path === '/__mock/next-reply') { nextReply = body; return json(res, 200, { ok: true }); }
  if (path === '/__mock/reply-delay') { replyDelayMs = Number(body.ms); return json(res, 200, { ok: true }); }
  if (path === '/__mock/drop') {
    for (const ws of sockets) ws.terminate(); // abrupt close, like a lost connection
    return json(res, 200, { ok: true });
  }

  json(res, 404, { title: 'Not Found' });
});

// ---- SignalR over WebSocket ----
const wss = new WebSocketServer({ noServer: true });
server.on('upgrade', (req, socket, head) => {
  if (!(req.url ?? '').startsWith('/hubs/live')) return socket.destroy();
  wss.handleUpgrade(req, socket, head, (ws) => wss.emit('connection', ws));
});

wss.on('connection', (ws: WebSocket) => {
  let shook = false;
  const ping = setInterval(() => ws.readyState === ws.OPEN && ws.send(JSON.stringify({ type: 6 }) + RS), 10_000);
  ws.on('message', (data) => {
    for (const frame of data.toString().split(RS).filter(Boolean)) {
      if (!shook) {
        // The first frame is the handshake request {"protocol":"json","version":1}.
        shook = true;
        ws.send('{}' + RS);
        sockets.add(ws);
        continue;
      }
      // pings (type 6) and anything else need no answer
      void frame;
    }
  });
  ws.on('close', () => { clearInterval(ping); sockets.delete(ws); });
  ws.on('error', () => ws.terminate());
});

const port = Number(process.env.PORT ?? 5081);
server.listen(port, () => console.log(`mock API listening on http://localhost:${port}`));
