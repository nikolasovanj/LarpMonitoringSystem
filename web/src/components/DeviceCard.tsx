import { Link } from 'react-router-dom';
import type { DeviceDto } from '../api/types';
import { useEffectiveStatus } from '../realtime/hooks';
import { SensorTile } from './SensorTile';
import { StatusBadge } from './StatusBadge';

export function DeviceCard({ device }: { device: DeviceDto }) {
  const status = useEffectiveStatus(device);
  const href = `/devices/${encodeURIComponent(device.site)}/${encodeURIComponent(device.line)}/${encodeURIComponent(device.device)}`;

  return (
    <article className={`card device status-border-${status}`}>
      <header className="card-head">
        <Link to={href} className="device-name">
          {device.device}
        </Link>
        <StatusBadge status={status} />
      </header>
      <div className="tiles">
        {device.sensors.map((s) => (
          <SensorTile key={s.seriesId} device={device} sensor={s} />
        ))}
      </div>
    </article>
  );
}
