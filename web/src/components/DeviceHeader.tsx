import { Link } from 'react-router-dom';
import type { DeviceDto } from '../api/types';
import { useEffectiveStatus } from '../realtime/hooks';
import { RelativeTime } from './RelativeTime';
import { StatusBadge } from './StatusBadge';

export function DeviceHeader({ device }: { device: DeviceDto }) {
  const status = useEffectiveStatus(device);
  return (
    <div className="page-head">
      <div>
        <p className="crumbs">
          <Link to="/">Overview</Link> / {device.site} / {device.line}
        </p>
        <h1>{device.device}</h1>
      </div>
      <div className="head-meta">
        <StatusBadge status={status} />
        <span className="muted small">
          last data <RelativeTime iso={device.lastSeen} />
        </span>
      </div>
    </div>
  );
}
