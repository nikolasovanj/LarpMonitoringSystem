import { useState } from 'react';
import type { DeviceDto } from '../api/types';
import { useHistory } from '../api/queries';
import { formatBucket } from '../lib/format';
import { HISTORY_RANGES, type RangeKey } from '../lib/ranges';
import { HistoryChart } from './HistoryChart';
import { ErrorMessage, Loading } from './QueryState';

export function HistoryPanel({ device }: { device: DeviceDto }) {
  const [seriesId, setSeriesId] = useState<number | undefined>(device.sensors[0]?.seriesId);
  const [range, setRange] = useState<RangeKey>('1h');

  // The device's sensors can change between refreshes; fall back to the first one.
  const sensor = device.sensors.find((s) => s.seriesId === seriesId) ?? device.sensors[0];
  const history = useHistory(sensor?.seriesId, range);

  if (!sensor) return null;

  return (
    <section className="card">
      <header className="card-head wrap">
        <h3>History</h3>
        <div className="controls">
          <select
            aria-label="Sensor"
            value={sensor.seriesId}
            onChange={(e) => setSeriesId(Number(e.target.value))}
          >
            {device.sensors.map((s) => (
              <option key={s.seriesId} value={s.seriesId}>
                {s.sensor}
              </option>
            ))}
          </select>
          <div className="segmented" role="group" aria-label="Time range">
            {HISTORY_RANGES.map((r) => (
              <button
                key={r.key}
                type="button"
                className={r.key === range ? 'active' : ''}
                onClick={() => setRange(r.key)}
              >
                {r.label}
              </button>
            ))}
          </div>
        </div>
      </header>

      {history.isPending && <Loading />}
      {history.isError && <ErrorMessage error={history.error} />}
      {history.data &&
        (history.data.points.length === 0 ? (
          <p className="muted empty">No data in this range.</p>
        ) : (
          <>
            <HistoryChart points={history.data.points} unit={sensor.unit} />
            <p className="muted small">
              {history.data.points.length} points, {formatBucket(history.data.bucketMs)} buckets
              {history.data.source === 'Rollup1m' ? ' (from the 1-minute rollup)' : ' (from raw samples)'}
            </p>
          </>
        ))}
    </section>
  );
}
