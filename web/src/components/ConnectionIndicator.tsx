import { useConnectionState } from '../realtime/hooks';

const LABELS = {
  connecting: 'Connecting…',
  connected: 'Live',
  reconnecting: 'Reconnecting…',
  disconnected: 'Offline',
} as const;

export function ConnectionIndicator() {
  const state = useConnectionState();
  return (
    <span className={`conn conn-${state}`} title="Live data connection to the API">
      <span className="dot" />
      {LABELS[state]}
    </span>
  );
}
