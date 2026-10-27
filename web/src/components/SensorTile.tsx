import type { DeviceDto, SensorDto } from '../api/types';
import { formatValue } from '../lib/format';
import { useNow } from '../lib/useNow';
import { useLatest, useLiveBuffer } from '../realtime/hooks';
import { seriesKey } from '../realtime/LiveStore';
import { RelativeTime } from './RelativeTime';
import { Sparkline } from './Sparkline';

const STALE_AFTER_MS = 15_000;

/** One sensor on a device card: latest value, sparkline and age. */
export function SensorTile({ device, sensor }: { device: DeviceDto; sensor: SensorDto }) {
  const key = seriesKey(device.site, device.line, device.device, sensor.sensor);
  const latest = useLatest(key);
  const buffer = useLiveBuffer(key);
  const now = useNow();

  const value = latest?.value ?? sensor.latestValue;
  const unit = latest?.unit ?? sensor.unit;
  const time = latest?.time ?? sensor.latestTime;
  const stale = !time || now - Date.parse(time) > STALE_AFTER_MS;

  return (
    <div className={`tile ${stale ? 'tile-stale' : ''}`}>
      <div className="tile-head">
        <span className="tile-name">{sensor.sensor}</span>
        <RelativeTime iso={time} />
      </div>
      <div className="tile-body">
        <span className="tile-value mono">
          {formatValue(value)} <small>{unit ?? ''}</small>
        </span>
        <Sparkline points={buffer} />
      </div>
    </div>
  );
}
