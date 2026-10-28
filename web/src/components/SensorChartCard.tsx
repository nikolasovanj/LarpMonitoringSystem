import type { DeviceDto, SensorDto } from '../api/types';
import { formatValue } from '../lib/format';
import { useLatest } from '../realtime/hooks';
import { seriesKey } from '../realtime/LiveStore';
import { LiveChart } from './LiveChart';

export function SensorChartCard({ device, sensor }: { device: DeviceDto; sensor: SensorDto }) {
  const key = seriesKey(device.site, device.line, device.device, sensor.sensor);
  const latest = useLatest(key);
  const unit = latest?.unit ?? sensor.unit;
  const value = latest?.value ?? sensor.latestValue;

  return (
    <section className="card">
      <header className="card-head">
        <h3>{sensor.sensor}</h3>
        <span className="mono big">
          {formatValue(value)} <small>{unit ?? ''}</small>
        </span>
      </header>
      <LiveChart seriesKey={key} unit={unit} />
    </section>
  );
}
