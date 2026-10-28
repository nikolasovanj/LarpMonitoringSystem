import type { DeviceStatus } from '../api/types';

export function StatusBadge({ status }: { status: DeviceStatus }) {
  return (
    <span className={`badge status-${status}`}>
      <span className="dot" />
      {status}
    </span>
  );
}
