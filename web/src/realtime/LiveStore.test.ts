import { describe, expect, it, vi } from 'vitest';
import type { LiveReading } from '../api/types';
import { LiveStore, MAX_POINTS, seriesKey } from './LiveStore';

const KEY = seriesKey('s', 'l', 'd', 'temperature');

function reading(t: number, value: number, over: Partial<LiveReading> = {}): LiveReading {
  return {
    site: 's',
    line: 'l',
    device: 'd',
    sensor: 'temperature',
    value,
    unit: 'C',
    time: new Date(t).toISOString(),
    ...over,
  };
}

describe('LiveStore.ingest', () => {
  it('stores points per series, sorted by time, and tracks the newest reading', () => {
    const store = new LiveStore();
    store.ingest([reading(3000, 3), reading(1000, 1), reading(2000, 2)]);

    expect(store.getBuffer(KEY).map((p) => p.v)).toEqual([1, 2, 3]);
    expect(store.getLatest(KEY)?.value).toBe(3);
  });

  it('keeps different series apart', () => {
    const store = new LiveStore();
    store.ingest([reading(1000, 1), reading(1000, 9, { sensor: 'pressure' })]);

    expect(store.getBuffer(KEY)).toHaveLength(1);
    expect(store.getBuffer(seriesKey('s', 'l', 'd', 'pressure'))[0].v).toBe(9);
  });

  it('ignores replayed and out-of-order points that are not newer than the buffer', () => {
    const store = new LiveStore();
    store.ingest([reading(2000, 2), reading(3000, 3)]);
    store.ingest([reading(1000, 1), reading(3000, 99), reading(4000, 4)]);

    expect(store.getBuffer(KEY).map((p) => p.v)).toEqual([2, 3, 4]);
  });

  it('drops invalid readings', () => {
    const store = new LiveStore();
    store.ingest([
      reading(1000, Number.NaN),
      reading(1500, 1, { time: 'not a date' }),
      reading(2000, 2),
    ]);

    expect(store.getBuffer(KEY).map((p) => p.v)).toEqual([2]);
  });

  it('caps the buffer at MAX_POINTS, keeping the newest', () => {
    const store = new LiveStore();
    const batch = Array.from({ length: MAX_POINTS + 50 }, (_, i) => reading(1000 + i, i));
    store.ingest(batch);

    const buf = store.getBuffer(KEY);
    expect(buf).toHaveLength(MAX_POINTS);
    expect(buf[buf.length - 1].v).toBe(MAX_POINTS + 49);
  });

  it('returns the same array reference until the data changes (required by useSyncExternalStore)', () => {
    const store = new LiveStore();
    store.ingest([reading(1000, 1)]);
    const a = store.getBuffer(KEY);
    expect(store.getBuffer(KEY)).toBe(a);

    store.ingest([reading(1000, 1)]); // pure replay: nothing changes
    expect(store.getBuffer(KEY)).toBe(a);

    store.ingest([reading(2000, 2)]);
    expect(store.getBuffer(KEY)).not.toBe(a);
  });

  it('returns a stable empty buffer for unknown series', () => {
    const store = new LiveStore();
    expect(store.getBuffer('nope')).toBe(store.getBuffer('also-nope'));
  });

  it('notifies subscribers once per batch, and not for no-op batches', () => {
    const store = new LiveStore();
    const listener = vi.fn();
    store.subscribe(listener);

    store.ingest([reading(1000, 1), reading(2000, 2), reading(3000, 3)]);
    expect(listener).toHaveBeenCalledTimes(1);

    store.ingest([reading(1000, 1)]);
    expect(listener).toHaveBeenCalledTimes(1);
  });
});

describe('LiveStore events and status', () => {
  const base = { site: 's', line: 'l', device: 'd' };

  it('tracks pushed device status by device', () => {
    const store = new LiveStore();
    const before = store.getStatuses();
    store.handleStatus({ ...base, status: 'offline', time: new Date().toISOString() });

    expect(store.getStatuses()).not.toBe(before);
    expect(store.getStatuses().get('s/l/d')?.status).toBe('offline');
  });

  it('records the last command result per device and logs an event', () => {
    const store = new LiveStore();
    store.handleCommandResult({ ...base, ok: false, error: 'device has no sensor "x"', time: '' });

    expect(store.getResult('s/l/d')?.ok).toBe(false);
    expect(store.getEvents()).toHaveLength(1);
    expect(store.getEvents()[0].kind).toBe('command-failed');
    expect(store.getEvents()[0].text).toContain('no sensor');
  });

  it('forwards alerts to onAlert and keeps only the latest 20 events', () => {
    const store = new LiveStore();
    const onAlert = vi.fn();
    store.onAlert = onAlert;

    for (let i = 0; i < 30; i++) {
      store.handleAlert({
        type: 'opened',
        alertId: i,
        severity: 'warning',
        message: `alert ${i}`,
        ...base,
        sensor: 'temperature',
        value: 1,
        time: '',
      });
    }
    expect(onAlert).toHaveBeenCalledTimes(30);
    expect(store.getEvents()).toHaveLength(20);
    expect(store.getEvents()[19].text).toBe('alert 29');
  });
});
