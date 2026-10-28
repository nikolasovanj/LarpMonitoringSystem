import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr';
import { API_BASE } from '../api/client';
import type {
  AlertEvent,
  CommandResultDto,
  DeviceStatusChanged,
  LiveReading,
} from '../api/types';

export type ConnectionState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

export interface LivePoint {
  /** ms since epoch */
  t: number;
  v: number;
}

export interface LiveEvent {
  id: number;
  kind: 'alert-opened' | 'alert-resolved' | 'command-ok' | 'command-failed';
  text: string;
  at: number;
}

export const MAX_POINTS = 1200; // about 4 minutes at 5 Hz, 20 minutes at 1 Hz
const MAX_EVENTS = 20;
const EMPTY_POINTS: readonly LivePoint[] = Object.freeze([]);

export const deviceKey = (site: string, line: string, device: string) =>
  `${site}/${line}/${device}`;
export const seriesKey = (site: string, line: string, device: string, sensor: string) =>
  `${site}/${line}/${device}/${sensor}`;

const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

/**
 * Holds everything the hub pushes. React reads it through useSyncExternalStore, so every
 * snapshot getter must return the SAME reference until the data it covers changes. That is why
 * buffers and maps are replaced (copy on write) instead of mutated.
 */
export class LiveStore {
  private listeners = new Set<() => void>();
  private buffers = new Map<string, readonly LivePoint[]>();
  private latest = new Map<string, LiveReading>();
  private statuses: ReadonlyMap<string, DeviceStatusChanged> = new Map();
  private results = new Map<string, CommandResultDto>();
  private events: readonly LiveEvent[] = [];
  private state: ConnectionState = 'connecting';
  private conn: HubConnection | null = null;
  private nextEventId = 1;

  onReconnected: (() => void) | null = null;
  onAlert: ((e: AlertEvent) => void) | null = null;

  // ---- subscription (arrow properties: stable identity for useSyncExternalStore) ----
  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };
  private emit() {
    this.listeners.forEach((l) => l());
  }

  // ---- snapshots ----
  getConnectionState = () => this.state;
  getBuffer = (key: string): readonly LivePoint[] => this.buffers.get(key) ?? EMPTY_POINTS;
  getLatest = (key: string) => this.latest.get(key);
  getStatuses = () => this.statuses;
  getResult = (deviceKeyValue: string) => this.results.get(deviceKeyValue);
  getEvents = () => this.events;

  // ---- ingest (public so the tests can drive it without a server) ----
  ingest(batch: readonly LiveReading[]) {
    if (!batch.length) return;

    const grouped = new Map<string, LivePoint[]>();
    const newest = new Map<string, { t: number; reading: LiveReading }>();
    for (const r of batch) {
      const t = Date.parse(r.time);
      if (!Number.isFinite(t) || !Number.isFinite(r.value)) continue;
      const key = seriesKey(r.site, r.line, r.device, r.sensor);
      let g = grouped.get(key);
      if (!g) grouped.set(key, (g = []));
      g.push({ t, v: r.value });
      const prev = newest.get(key);
      if (!prev || t >= prev.t) newest.set(key, { t, reading: r });
    }

    let changed = false;
    for (const [key, pts] of grouped) {
      pts.sort((a, b) => a.t - b.t);
      const old = this.buffers.get(key) ?? EMPTY_POINTS;
      const lastT = old.length ? old[old.length - 1].t : -Infinity;
      const fresh = pts.filter((p) => p.t > lastT); // ignore replays and out-of-order leftovers
      if (!fresh.length) continue;
      const merged = [...old, ...fresh];
      this.buffers.set(key, merged.length > MAX_POINTS ? merged.slice(-MAX_POINTS) : merged);
      changed = true;
    }
    for (const [key, { t, reading }] of newest) {
      const cur = this.latest.get(key);
      if (!cur || t >= Date.parse(cur.time)) {
        this.latest.set(key, reading);
        changed = true;
      }
    }
    if (changed) this.emit();
  }

  handleStatus(s: DeviceStatusChanged) {
    const next = new Map(this.statuses);
    next.set(deviceKey(s.site, s.line, s.device), s);
    this.statuses = next;
    this.emit();
  }

  handleCommandResult(r: CommandResultDto) {
    this.results.set(deviceKey(r.site, r.line, r.device), r);
    this.pushEvent(
      r.ok ? 'command-ok' : 'command-failed',
      r.ok ? `${r.device}: command accepted` : `${r.device}: ${r.error ?? 'command rejected'}`,
    );
  }

  handleAlert(a: AlertEvent) {
    const where = a.device ? ` (${a.device})` : '';
    this.pushEvent(
      a.type === 'opened' ? 'alert-opened' : 'alert-resolved',
      a.type === 'opened' ? a.message : `Resolved: ${a.message}${where}`,
    );
    this.onAlert?.(a);
  }

  private pushEvent(kind: LiveEvent['kind'], text: string) {
    const ev: LiveEvent = { id: this.nextEventId++, kind, text, at: Date.now() };
    this.events = [...this.events, ev].slice(-MAX_EVENTS);
    this.emit();
  }

  private setState(s: ConnectionState) {
    if (this.state === s) return;
    this.state = s;
    this.emit();
  }

  // ---- connection lifecycle ----
  start() {
    if (this.conn) return;
    const conn = new HubConnectionBuilder()
      .withUrl(`${API_BASE}/hubs/live`)
      // Never give up: back off 1, 2, 4, 8, 16, 30, 30 ... seconds.
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (ctx) =>
          Math.min(30_000, 1000 * 2 ** Math.min(ctx.previousRetryCount, 5)),
      })
      .configureLogging(LogLevel.Warning)
      .build();

    conn.on('Telemetry', (batch: LiveReading[]) => this.ingest(batch));
    conn.on('DeviceStatus', (s: DeviceStatusChanged) => this.handleStatus(s));
    conn.on('CommandResult', (r: CommandResultDto) => this.handleCommandResult(r));
    conn.on('Alert', (a: AlertEvent) => this.handleAlert(a));

    conn.onreconnecting(() => this.setState('reconnecting'));
    conn.onreconnected(() => {
      this.setState('connected');
      this.onReconnected?.(); // anything missed while disconnected is refetched
    });
    conn.onclose(() => {
      if (this.conn !== conn) return; // closed on purpose by stop()
      this.setState('disconnected');
      void this.connect(conn);
    });

    this.conn = conn;
    void this.connect(conn);
  }

  stop() {
    const conn = this.conn;
    this.conn = null;
    if (conn) void conn.stop();
    this.setState('disconnected');
  }

  /** The first start() does not retry by itself, so we loop until it works or stop() is called. */
  private async connect(conn: HubConnection) {
    let delay = 1000;
    while (this.conn === conn) {
      try {
        await conn.start();
        if (this.conn !== conn) {
          await conn.stop();
          return;
        }
        this.setState('connected');
        this.onReconnected?.();
        return;
      } catch {
        if (this.conn !== conn) return;
        this.setState('reconnecting');
        await sleep(delay);
        delay = Math.min(delay * 2, 30_000);
      }
    }
  }
}
