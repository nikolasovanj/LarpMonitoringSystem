import { useNow } from '../lib/useNow';
import { useLiveEvents } from '../realtime/hooks';

const VISIBLE_MS = 8000;

/** Short-lived notices for alerts and device replies, driven by the live event log. */
export function Toasts() {
  const events = useLiveEvents();
  const now = useNow();
  const recent = events.filter((e) => now - e.at < VISIBLE_MS).slice(-4);
  if (recent.length === 0) return null;

  return (
    <div className="toasts" aria-live="polite">
      {recent.map((e) => (
        <div key={e.id} className={`toast toast-${e.kind}`}>
          {e.text}
        </div>
      ))}
    </div>
  );
}
